using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace TrafficLight.Visuals.Gpu;

/// <summary>
/// GPU-рендер частиц Хладни: вся физика (поля стоячих волн, тряска, вспышки)
/// считается compute-шейдером D3D11, частицы рисуются instanced-квадами с
/// аддитивным свечением (идеально для OLED: чёрный фон, цвета складываются).
/// HwndHost с собственным swap chain; панель настроек в оконном режиме
/// не перекрывает GPU-область (airspace-ограничение WPF).
/// </summary>
public sealed class GpuParticleHost : HwndHost
{
    private const string WindowClassName = "TrafficLightGpuHost";
    private const int Bands = 3;
    private const int MaxCount = 300_000;
    private const double WaveDur = 0.55;   // сек: волна энергии от центра до края

    // ------------------------------------------------------------------ public

    public bool GpuReady { get; private set; }
    public string? FailReason { get; private set; }

    /// <summary>Двойной клик по GPU-полю (для выхода из полного экрана — child HWND перехватывает мышь).</summary>
    public event Action? DoubleClick;

    private int _count = 60_000;
    private int _groups;

    private static readonly Random InitRandom = new(12345);

    private sealed class BandState
    {
        public double Flash;
        public double PrevLevel;
        public double Blend = 1.0;
        public double Nrm = 0.5;   // сглаженная нормализованная доминанта/тембр (быстрая)
        public double NrmSlow = 0.5; // та же тональность, медленный EMA — рассогласование = наклон
        public double SlopeEma;    // наклон тональности: + вверх, − вниз
        public double FluxEma;     // собственная нормировка потока спектра
        public double Spin;        // поворот узла: направление = наклон тональности
        public int ModeA;
        public int ModeB;
        public double WaveAge = 999; // волна энергии от атаки: возраст в секундах, >DUR = нет
        public double Pulse;       // «дыхание» пластины: масштаб поля на ударах
    }

    private readonly BandState[] _bandState = [new(), new(), new()];

    private static readonly Vortice.Mathematics.Color4[] BandColors =
    [
        new(1f, 0.255f, 0.212f, 1f),     // красный: басы
        new(0.216f, 0.831f, 0.353f, 1f), // зелёный: середина
        new(1f, 0.769f, 0f, 1f)          // жёлтый: верха
    ];

    public void SetCount(int particlesPerBand)
    {
        _count = Math.Clamp(particlesPerBand, 2_000, MaxCount);
        _groups = (_count + 127) / 128;
        if (GpuReady)
            RecreateBandBuffers();
    }

    /// <summary>Обновить состояние полосы (вспышка/узор/бленд) — та же логика, что и в CPU-движке.</summary>
    public void UpdateBand(int band, double level, double norm, double flux, double dt)
    {
        var bs = _bandState[band];

        bs.Flash = Math.Max(0.0, bs.Flash - dt * 3.2);   // спад вспышки резче
        if (level - bs.PrevLevel > 0.22)
        {
            bs.Flash = 1.0;
            if (bs.WaveAge > WaveDur)
                bs.WaveAge = 0.0;   // атака → из центра пластины бежит волна энергии
        }
        else
            bs.Flash = Math.Max(bs.Flash, 0.55 * Math.Clamp((level - 0.82) * 3.0, 0.0, 1.0));
        bs.WaveAge = Math.Min(bs.WaveAge + dt, 9.0);

        // Жизнь узора: поворот задаётся ДВИЖЕНИЕМ ТОНАЛЬНОСТИ полосы.
        // Наклон = рассогласование быстрого (τ=0.625 c) и медленного (τ=2.5 c) EMA
        // тональности, делённое на разницу их постоянных (устойчиво к дрожжи):
        // вверх → крутим в одну сторону, вниз → в другую, скорость ∝ наклону.
        bs.Nrm += (norm - bs.Nrm) * Math.Min(1.0, dt * 1.6);
        bs.NrmSlow += (norm - bs.NrmSlow) * Math.Min(1.0, dt * 0.4);
        double slope = (bs.Nrm - bs.NrmSlow) / 1.875;   // = 1/0.4 − 1/1.6
        bs.SlopeEma += (slope - bs.SlopeEma) * Math.Min(1.0, dt * 2.5);

        // поток спектра — лишь лёгкий «довесок», чтобы узор не замирал совсем
        bs.FluxEma += (flux - bs.FluxEma) * Math.Min(1.0, dt * 0.5);
        double fn = Math.Clamp(flux / (bs.FluxEma + 1e-9), 0.0, 2.0);

        // «дыхание» пластины: на ударах поле слегка раздувается и сдувается
        double pulseTarget = 0.038 * bs.Flash + 0.018 * Math.Min(1.0, fn);
        bs.Pulse += (pulseTarget - bs.Pulse) * Math.Min(1.0, dt * 7.0);

        double spinRate = 3.2 * Math.Tanh(bs.SlopeEma * 4.0) + 0.35 * fn;
        bs.Spin = (bs.Spin + spinRate * dt) % (Math.PI * 2.0);

        double pos = bs.Nrm * (ChladniModes.Modes.Length - 1);
        int i0 = (int)Math.Floor(pos);
        bs.ModeA = Math.Clamp(i0, 0, ChladniModes.Modes.Length - 1);
        bs.ModeB = Math.Min(bs.ModeA + 1, ChladniModes.Modes.Length - 1);
        double fr = pos - i0;
        bs.Blend = fr * fr * (3.0 - 2.0 * fr);   // smoothstep: у endpoints поля чистые

        bs.PrevLevel = level;
    }

    /// <summary>Уровни полос текущего кадра (уровень, нормализованная частота).</summary>
    public readonly (double Level, double FreqNorm)[] CurrentBandLevel =
    [new(0, 0.5), new(0, 0.5), new(0, 0.5)];

    // ------------------------------------------------------------------ светофор

    /// <summary>Состояние ламп: мгновенное включение, плавное затухание, «накаливание» — зеркало CPU LampField.</summary>
    public void UpdateLampBands(double[] levels, double[] thresholds, double dt)
    {
        for (int i = 0; i < Bands; i++)
        {
            double target = levels[i] >= thresholds[i]
                ? Math.Clamp(0.55 + (levels[i] - thresholds[i]) * 1.1, 0.55, 1.0)
                : 0.0;

            // Следящий контур 2-го порядка (критическое демпфирование): ступеньки
            // анализатора (~21 Гц) интегрируются в плавную C¹-кривую без лестницы,
            // а скорость яркости даёт «отзыв» быстрее прежнего однополюсного EMA.
            double wn = target > _lampLit[i] ? 18.0 : 4.2;  // атака быстрая, спад калильный
            double zeta = target > _lampLit[i] ? 0.9 : 1.0; // 0.9 — лёгкий задор атаки без звона
            double rem = Math.Min(dt, 0.1);
            while (rem > 1e-4)
            {
                double h = Math.Min(rem, 0.008);             // фикс. подшаг — устойчивость при лагах
                double x = _lampLit[i] - target;
                double acc = -(wn * wn) * x - 2.0 * zeta * wn * _lampVel[i];
                _lampVel[i] += acc * h;
                _lampLit[i] += _lampVel[i] * h;
                if (_lampLit[i] < 0.0) { _lampLit[i] = 0.0; _lampVel[i] = Math.Max(0.0, _lampVel[i]); }
                else if (_lampLit[i] > 1.06) { _lampLit[i] = 1.06; _lampVel[i] = Math.Min(0.0, _lampVel[i]); }
                rem -= h;
            }

            if (_lampLit[i] > 0.08)
            {
                double f = Math.Clamp(_lampFlick[i] + (_lampRng.NextDouble() - 0.5) * 0.30 * dt * 30.0, 0.60, 1.0);
                if (_lampRng.NextDouble() < 0.02)
                    f *= 0.55;
                _lampFlick[i] = f;
            }
            else
            {
                _lampFlick[i] = 1.0;
            }
        }
        _lampTime += dt;
    }

    /// <summary>Кадр светофора: clear → процедурные лампы пиксельным шейдером → present.</summary>
    public void RenderLamps(double hostWidthDip, double hostHeightDip)
    {
        if (!GpuReady || _lampVs is null || _lampPs is null)
            return;
        GetClientRect(_hwnd, out RECT rc);
        int px = rc.Right - rc.Left, py = rc.Bottom - rc.Top;
        if (px < 16 || py < 16 || hostWidthDip < 1 || hostHeightDip < 1)
            return;

        EnsureSwapChainSize(px, py);
        double scale = px / hostWidthDip;
        double r = Math.Min(hostWidthDip * 0.135, hostHeightDip * 0.32) * scale;
        double cy = hostHeightDip * 0.5 * scale;
        double[] cx =
        [
            hostWidthDip * 0.16 * scale,
            hostWidthDip * 0.50 * scale,
            hostWidthDip * 0.84 * scale
        ];

        var m = _ctx.Map(_cbLamp, MapMode.WriteNoOverwrite, Vortice.Direct3D11.MapFlags.None);
        IntPtr p = m.DataPointer;
        W(p, 0, (float)px); W(p, 4, (float)py); W(p, 8, (float)_lampTime); W(p, 12, 0f);
        for (int i = 0; i < Bands; i++)
        {
            int oL = 16 + i * 16, oF = 64 + i * 16, oC = 112 + i * 16;
            W(p, oL, (float)cx[i]); W(p, oL + 4, (float)cy);
            W(p, oL + 8, (float)r); W(p, oL + 12, (float)_lampLit[i]);
            W(p, oF, (float)_lampFlick[i]); W(p, oF + 4, (float)CurrentBandLevel[i].Level);
            W(p, oF + 8, 0f); W(p, oF + 12, 0f);
            var bc = BandColors[i];
            W(p, oC, bc.R); W(p, oC + 4, bc.G);
            W(p, oC + 8, bc.B); W(p, oC + 12, 1f);
        }
        _ctx.Unmap(_cbLamp);

        _ctx.ClearRenderTargetView(_rtv, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        _ctx.OMSetRenderTargets(_rtv, null);
        _ctx.OMSetBlendState(null);   // шейдер сам собирает картину — пишем непрозрачно
        _ctx.IASetInputLayout(null);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_lampVs);
        _ctx.PSSetShader(_lampPs);
        _ctx.PSSetConstantBuffer(1, _cbLamp);
        _ctx.Draw(3, 0);              // fullscreen-треугольник из SV_VertexID
        _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>Один кадр: compute (физика) → два прохода отрисовки на полосу → present.</summary>
    public void Render(Rect[] platesDip, double hostWidthDip)
    {
        if (!GpuReady)
            return;
        RenderCore(platesDip, hostWidthDip);
    }

    private void RenderCore(Rect[] platesDip, double hostWidthDip)
    {
        GetClientRect(_hwnd, out RECT rc);
        int px = rc.Right - rc.Left, py = rc.Bottom - rc.Top;
        if (px < 16 || py < 16 || hostWidthDip < 1)
            return;

        EnsureSwapChainSize(px, py);

        long now = Environment.TickCount64;
        double dt = Math.Clamp((now - _lastTick) / 1000.0, 0.0005, 0.1);
        _lastTick = now;
        _frame++;

        double scale = px / hostWidthDip; // DIP → пиксели клиента

        // одно поле («объединённый» режим) — почти без рамок (0.2 %):
        // все три «пластины» совпадают; три пластины — прежний заметный отступ
        bool single = platesDip.Length == 3
            && platesDip[0] == platesDip[1] && platesDip[1] == platesDip[2];
        double clampLo = single ? 0.002 : 0.02;
        double clampHi = 1.0 - clampLo;

        _ctx.ClearRenderTargetView(_rtv, new Vortice.Mathematics.Color4(0, 0, 0, 1));

        // 1) физика на GPU (compute-шейдер)
        _ctx.CSSetShader(_cs);
        for (int b = 0; b < Bands; b++)
        {
            var bs = _bandState[b];
            double lv = CurrentBandLevel[b].Level;
            // пропорции: у квадратной пластины (3 пластины) аспект = 1 — режим не затронут;
            // одно поле: короткий край = единица поля, длинный — естественное
            // продолжение узора за [0,1] (cos-моды periodic), без кропа и без плющения
            Rect plB = platesDip[b];
            double side = Math.Min(plB.Width, plB.Height);
            double aspX = plB.Width / side, aspY = plB.Height / side;
            WriteCompConstants(b, dt, lv, bs.Flash, bs.Blend,
                ChladniModes.Modes[bs.ModeA], ChladniModes.Modes[bs.ModeB], bs.Spin,
                clampLo, clampHi, bs.Pulse, bs.WaveAge, aspX, aspY);
            _ctx.CSSetConstantBuffer(0, _cbComp[b]);
            _ctx.CSSetUnorderedAccessViews(0, [_uav[b]]);
            _ctx.Dispatch((uint)_groups, 1, 1);
        }
        _ctx.CSSetShader(null);
        _ctx.CSSetUnorderedAccessViews(0, [null!]);

        // 2) отрисовка: ореол + ядро на каждую полосу (аддитивно)
        _ctx.RSSetState(RasterState);
        _ctx.OMSetRenderTargets(_rtv, null);
        _ctx.OMSetBlendState(_blend);
        _ctx.IASetInputLayout(null);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);

        double minSide = double.MaxValue;
        foreach (var p in platesDip)
            minSide = Math.Min(minSide, Math.Min(p.Width, p.Height));
        double dotBase = Math.Clamp(minSide / 900.0, 0.9, 1.8); // маленькие частицы

        for (int b = 0; b < Bands; b++)
        {
            var bs = _bandState[b];
            double level = CurrentBandLevel[b].Level;
            // на атаке точки чуть МЕЛЬЧЕ и ЯРЧЕ: акцент светимостью, картинка в фокусе
            double dot = dotBase * (1.0 - 0.12 * bs.Flash);
            double coreAlpha = Math.Clamp(0.25 + 0.80 * level + 0.55 * bs.Flash, 0.0, 1.0);

            WriteDrawConstants(b, platesDip[b], scale, px, py,
                dot * 2.4,
                0.10 + 0.28 * level, 1f);
            _ctx.VSSetConstantBuffer(0, _cbDraw[b]);
            _ctx.PSSetConstantBuffer(0, _cbDraw[b]);
            _ctx.VSSetShaderResources(0, [_srv[b]]);
            _ctx.PSSetShaderResources(0, [_srv[b]]);
            _ctx.DrawInstanced(6, (uint)_count, 0, 0);

            WriteDrawConstants(b, platesDip[b], scale, px, py, dot, coreAlpha, 0f);
            _ctx.VSSetConstantBuffer(0, _cbDraw[b]);
            _ctx.PSSetConstantBuffer(0, _cbDraw[b]);
            _ctx.DrawInstanced(6, (uint)_count, 0, 0);
        }

        _swapChain.Present(1, PresentFlags.None);
    }

    // ------------------------------------------------------------------ HwndHost

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Дочернее окно через WPF HwndSource: без ручной регистрации оконного класса
        var parameters = new HwndSourceParameters("TrafficLightGpuParticles", 64, 64)
        {
            ParentWindow = hwndParent.Handle,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)(WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS))
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProcHook);
        _hwnd = _hwndSource.Handle;
        StripFrameEdges(_hwnd); // на всякий случай снимаем любые рамки/канты (белые линии по краям экрана)

        try
        {
            CreateDeviceAndResources();
            // прогревка: один полный кадр (compute + draw + present) прямо при инициализации,
            // чтобы ошибки шейдеров/драйвера проявились сразу, а не в случайном кадре
            var warmRect = new Rect(0, 0, 64, 64);
            RenderCore([warmRect, warmRect, warmRect], 64);
            GpuReady = true;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "debug.log"),
                    $"[{DateTime.Now:HH:mm:ss}] GPU init OK, adapters tried: HW; count={_count}{Environment.NewLine}");
            }
            catch { }
        }
        catch (Exception ex)
        {
            FailReason = ex.Message;
            GpuReady = false;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] GPU init failed (CPU fallback): {ex}{Environment.NewLine}");
            }
            catch { /* лог вторичен */ }
        }

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _hwndSource?.Dispose();
        _hwndSource = null;
        ReleaseGpu();
    }

    private long _lastClickTick;
    private int _lastClickX = -9999, _lastClickY = -9999;

    private IntPtr WndProcHook(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Двойной клик: класс окна HwndSource может не иметь CS_DBLCLKS, тогда
        // WM_LBUTTONDBLCLK не приходит вообще — синтезируем из двух WM_LBUTTONDOWN.
        if (msg == WM_LBUTTONDBLCLK)
        {
            DoubleClick?.Invoke();
            _lastClickTick = 0;
        }
        else if (msg == WM_LBUTTONDOWN)
        {
            int lp = lParam.ToInt32();
            int x = (short)(lp & 0xFFFF), y = (short)(lp >> 16);
            long now = Environment.TickCount64;
            bool isDouble = now - _lastClickTick <= GetDoubleClickTime()
                            && Math.Abs(x - _lastClickX) <= 8 && Math.Abs(y - _lastClickY) <= 8;
            if (isDouble)
            {
                DoubleClick?.Invoke();
                _lastClickTick = 0;
            }
            else
            {
                _lastClickTick = now;
                _lastClickX = x;
                _lastClickY = y;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Снимает с дочернего окна любые рамочные стили и канты — иначе DWM рисует белые линии по краям.</summary>
    private static void StripFrameEdges(IntPtr hwnd)
    {
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const int Frame = 0x00C00000 /*CAPTION*/ | 0x00800000 /*BORDER*/ | 0x00400000 /*DLGFRAME*/ | 0x00040000 /*THICKFRAME*/;
        const int ExEdge = 0x00000001 /*DLGMODALFRAME*/ | 0x00000100 /*WINDOWEDGE*/
                         | 0x00000200 /*CLIENTEDGE*/ | 0x00020000 /*STATICEDGE*/;
        int st = GetWindowLong(hwnd, GWL_STYLE) & ~Frame;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE) & ~ExEdge;
        SetWindowLong(hwnd, GWL_STYLE, st);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ | 0x0004 /*NOZORDER*/ | 0x0020 /*FRAMECHANGED*/);
    }

    // ------------------------------------------------------------------ D3D11

    private ID3D11Device _device = null!;
    private IDXGISwapChain _swapChain = null!;
    private ID3D11DeviceContext _ctx = null!;
    private ID3D11ComputeShader _cs = null!;
    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _ps = null!;
    private ID3D11BlendState _blend = null!;
    private ID3D11RasterizerState? _raster;
    private ID3D11RenderTargetView _rtv = null!;

    private readonly ID3D11Buffer[] _parts = new ID3D11Buffer[Bands];
    private readonly ID3D11ShaderResourceView[] _srv = new ID3D11ShaderResourceView[Bands];
    private readonly ID3D11UnorderedAccessView[] _uav = new ID3D11UnorderedAccessView[Bands];
    private readonly ID3D11Buffer[] _cbComp = new ID3D11Buffer[Bands];
    private readonly ID3D11Buffer[] _cbDraw = new ID3D11Buffer[Bands];

    // ---------- шейдерный проход светофора (fullscreen-треугольник) ----------
    private ID3D11VertexShader? _lampVs;
    private ID3D11PixelShader? _lampPs;
    private ID3D11Buffer _cbLamp = null!;
    private readonly double[] _lampLit = new double[3];
    private readonly double[] _lampVel = new double[3];   // скорость яркости (критический демпфер)
    private readonly double[] _lampFlick = { 1, 1, 1 };
    private double _lampTime;
    private readonly Random _lampRng = new();

    private uint _frame;
    private long _lastTick = Environment.TickCount64;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private int _bbW, _bbH;

    private ID3D11RasterizerState RasterState
        => _raster ??= _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid
        });

    private void CreateDeviceAndResources()
    {
        GetClientRect(_hwnd, out RECT rc);
        int w = Math.Max(16, rc.Right - rc.Left), h = Math.Max(16, rc.Bottom - rc.Top);

        var swapDesc = new SwapChainDescription
        {
            BufferDescription = new ModeDescription((uint)w, (uint)h, new Rational(60, 1), Format.R8G8B8A8_UNorm),
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            OutputWindow = _hwnd,
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
            Flags = SwapChainFlags.None
        };

        var res = D3D11.D3D11CreateDeviceAndSwapChain(
            null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            swapDesc,
            out _swapChain, out _device, out FeatureLevel? fl, out _ctx);

        if (res.Failure)
        {
            res = D3D11.D3D11CreateDeviceAndSwapChain(
                null, DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                swapDesc,
                out _swapChain, out _device, out fl, out _ctx);
        }
        res.CheckError();

        // шейдеры
        var csBlob = Compiler.Compile(CsHlsl, "CS", "cs", "cs_5_0", ShaderFlags.None, EffectFlags.None);
        var vsBlob = Compiler.Compile(VsHlsl, "VS", "vs", "vs_5_0", ShaderFlags.None, EffectFlags.None);
        var psBlob = Compiler.Compile(PsHlsl, "PS", "ps", "ps_5_0", ShaderFlags.None, EffectFlags.None);
        _cs = _device.CreateComputeShader(csBlob.Span, null!);
        _vs = _device.CreateVertexShader(vsBlob.Span, null!);
        _ps = _device.CreatePixelShader(psBlob.Span, null!);

        // светофор: тот же файл компилируем под обе точки входа
        var lampLv = Compiler.Compile(LampHlsl, "LampVS", "vs", "vs_5_0", ShaderFlags.None, EffectFlags.None);
        var lampPs = Compiler.Compile(LampHlsl, "LampPS", "ps", "ps_5_0", ShaderFlags.None, EffectFlags.None);
        _lampVs = _device.CreateVertexShader(lampLv.Span, null!);
        _lampPs = _device.CreatePixelShader(lampPs.Span, null!);
        _cbLamp = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 160,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        // аддитивное смешивание
        var bd = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.One,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blend = _device.CreateBlendState(bd);

        // constant buffers (compute — 112B: clamp @64-76, pulse/wave @80-92, aspect @96-100)
        var cbDesc = new BufferDescription
        {
            ByteWidth = 112,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        for (int b = 0; b < Bands; b++)
        {
            _cbComp[b] = _device.CreateBuffer(cbDesc);
            _cbDraw[b] = _device.CreateBuffer(cbDesc);
        }

        _groups = (_count + 127) / 128;
        RecreateBandBuffers();
        _bbW = _bbH = 0;
        EnsureSwapChainSize(w, h);
    }

    private void RecreateBandBuffers()
    {
        for (int b = 0; b < Bands; b++)
        {
            _srv[b]?.Dispose();
            _uav[b]?.Dispose();
            _parts[b]?.Dispose();

            // x,y,spark,wave: spark=1 у каждой 64-й частицы — «искра», бегущая по линии
            var data = new float[_count * 4];
            for (int i = 0; i < _count; i++)
            {
                data[i * 4] = 0.02f + (float)InitRandom.NextDouble() * 0.96f;
                data[i * 4 + 1] = 0.02f + (float)InitRandom.NextDouble() * 0.96f;
                data[i * 4 + 2] = (i & 63) == 0 ? 1f : 0f;
                data[i * 4 + 3] = 0f;
            }

            var bufDesc = new BufferDescription
            {
                ByteWidth = (uint)(_count * 16),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 16
            };

            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _parts[b] = _device.CreateBuffer(bufDesc, pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }

            _srv[b] = _device.CreateShaderResourceView(_parts[b]);
            _uav[b] = _device.CreateUnorderedAccessView(_parts[b]);
        }
    }

    private void EnsureSwapChainSize(int w, int h)
    {
        if (w == _bbW && h == _bbH)
            return;
        _bbW = w; _bbH = h;

        _rtv?.Dispose();
        _rtv = null;
        var rb = _swapChain.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None);
        if (rb.Code != 0)
            LogDebug("ResizeBuffers err 0x" + rb.Code.ToString("X8"));

        _rtv?.Dispose();
        var bb = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(bb);
        bb.Dispose();

        _ctx.RSSetViewports(new[]
        {
            new Vortice.Mathematics.Viewport { X = 0, Y = 0, Width = w, Height = h, MinDepth = 0f, MaxDepth = 1f }
        });
    }

    private static void LogDebug(string s)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
        }
        catch { }
    }

    private void WriteCompConstants(int band, double dt, double level, double flash, double blend,
                                    ChladniModes.Mode modeA, ChladniModes.Mode modeB, double spin,
                                    double clampLo, double clampHi, double pulse, double waveAge,
                                    double aspectX, double aspectY)
    {
        var m = _ctx.Map(_cbComp[band], MapMode.WriteNoOverwrite, Vortice.Direct3D11.MapFlags.None);
        IntPtr p = m.DataPointer;
        W(p, 0, (float)dt); W(p, 4, (float)level); W(p, 8, (float)flash); W(p, 12, (float)blend);
        Marshal.WriteInt32(p, 16, modeA.Fam); Marshal.WriteInt32(p, 20, modeA.A);
        Marshal.WriteInt32(p, 24, modeA.B);   W(p, 28, (float)modeA.C);
        Marshal.WriteInt32(p, 32, modeB.Fam); Marshal.WriteInt32(p, 36, modeB.A);
        Marshal.WriteInt32(p, 40, modeB.B);   W(p, 44, (float)modeB.C);
        W(p, 48, (float)_count); Marshal.WriteInt32(p, 52, unchecked((int)_frame));
        W(p, 56, (float)spin); W(p, 60, (float)spin);
        // четыре скаляра: lo.x@64 lo.y@68 hi.x@72 hi.y@76 (скаляры упакованы последовательно)
        W(p, 64, (float)clampLo); W(p, 68, (float)clampLo);
        W(p, 72, (float)clampHi); W(p, 76, (float)clampHi);
        // дыхание и волна энергии: uPulse@80 uWaveR@84 uWaveW@88 uWaveA@92 → CB 96B
        W(p, 80, (float)pulse);
        double waveR = 0, waveW = 0.05, waveA = 0;
        // конец фронта — до угла экрана в экранных пропорциях (wide = дальше)
        double waveEnd = 0.5 * Math.Sqrt(aspectX * aspectX + aspectY * aspectY) + 0.08;
        if (waveAge <= WaveDur)
        {
            double pr = waveAge / WaveDur;
            waveR = pr * waveEnd;
            waveW = 0.035 + 0.06 * pr;
            waveA = Math.Pow(1.0 - pr, 0.5) * (0.6 + 0.4 * level) * 1.6;
        }
        W(p, 84, (float)waveR); W(p, 88, (float)waveW); W(p, 92, (float)waveA);
        // пропорции поля: одинаковый масштаб по X и Y (без «плющения» на wide-экране)
        W(p, 96, (float)aspectX); W(p, 100, (float)aspectY);
        _ctx.Unmap(_cbComp[band]);
    }

    private void WriteDrawConstants(int band, Rect plateDip, double scale, int px, int py,
                                    double sizePx, double alpha, float soft)
    {
        var c = BandColors[band];
        var m = _ctx.Map(_cbDraw[band], MapMode.WriteNoOverwrite, Vortice.Direct3D11.MapFlags.None);
        IntPtr p = m.DataPointer;
        W(p, 0, c.R); W(p, 4, c.G); W(p, 8, c.B); W(p, 12, 1f);
        W(p, 16, (float)(plateDip.X * scale)); W(p, 20, (float)(plateDip.Y * scale));
        W(p, 24, (float)(plateDip.Width * scale)); W(p, 28, (float)(plateDip.Height * scale));
        W(p, 32, (float)sizePx); W(p, 36, (float)Math.Clamp(alpha, 0.0, 0.9));
        W(p, 40, (float)px); W(p, 44, (float)py);
        W(p, 48, soft); W(p, 52, 0f);
        _ctx.Unmap(_cbDraw[band]);
    }

    private static void W(IntPtr p, int offset, float v)
        => Marshal.WriteInt32(p, offset, BitConverter.SingleToInt32Bits(v));

    private void ReleaseGpu()
    {
        try
        {
            for (int b = 0; b < Bands; b++)
            {
                _srv[b]?.Dispose(); _uav[b]?.Dispose(); _parts[b]?.Dispose();
                _cbComp[b]?.Dispose(); _cbDraw[b]?.Dispose();
            }
            _rtv?.Dispose(); _blend?.Dispose(); _raster?.Dispose();
            _cs?.Dispose(); _vs?.Dispose(); _ps?.Dispose();
            _swapChain?.Dispose(); _device?.Dispose();
        }
        catch { /* при закрытии окна */ }
        GpuReady = false;
    }

    // ------------------------------------------------------------------ светофор HLSL

    private const string LampHlsl = """
        struct VS_OUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        VS_OUT LampVS(uint id : SV_VERTEXID)
        {
            VS_OUT o;
            // один треугольник, покрывающий весь экран
            float2 p = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0);
            o.pos = float4(p, 0.0, 1.0);
            o.uv = p * 0.5 + 0.5;
            return o;
        }

        cbuffer LCB : register(b1)
        {
            float2 uRes;    float uTime; float uLPad;
            float4 uL0; float4 uL1; float4 uL2;   // cx,cy (px), radius, lit
            float4 uF0; float4 uF1; float4 uF2;   // flicker, level, pad, pad
            float4 uC0; float4 uC1; float4 uC2;   // base color rgb, 1
        };

        static const float PI = 3.14159265358979;

        float hash11(float x)
        {
            x = frac(x * 0.1031);
            x *= x + 33.33;
            x *= x + x;
            return frac(x);
        }

        // Процедурная светофорная линза: fake-сфера + френелевские концентрические
        // рёбра (как у настоящей линзы Френеля) + diffuse сверху-слева + стеклянный
        // блик + кант Френеля + наружное свечение. Выключенная лампа — тёмное стекло.
        float3 lamp(float2 px, float4 lp, float4 fp, float3 col, float3 acc)
        {
            float2 d = px - lp.xy;
            float R = lp.z;
            float rr = length(d) / R;
            float lit = lp.w * fp.x;
            if (rr > 2.1 && lit < 0.004)
                return 0;

            float3 outc = 0;

            // --- тонкая скруглённая «таблетка» шкалы уровня под линзой ---
            float2 q = px - float2(lp.xy.x, lp.xy.y + R * 1.46);
            float segL = R * 0.60;                       // полуфина трека
            float cr = max(2.0, R * 0.042);              // радиус скругления торцов
            // SDF капсулы: треки и заполнения скруглены с обоих концов
            float sdT = length(float2(max(abs(q.x) - segL, 0.0), q.y)) - cr;
            float lv = saturate(fp.y);
            float sdF = length(float2(max(abs(q.x) - segL * lv, 0.0), q.y)) - (cr - 1.2);
            if (min(sdT, sdF) < 0.6)
            {
                float topF = 1.0 - smoothstep(-cr, cr, q.y);   // глянец: верх светлее (y вниз)
                float3 trk = float3(0.055, 0.062, 0.075) + float3(0.02, 0.022, 0.03) * topF;
                float3 fil = col * (0.62 + 0.55 * topF)
                           + float3(1.0, 1.0, 1.0) * (topF * topF * 0.22);
                float3 bar = (sdF < 0.0) ? fil : trk;
                outc = max(outc, bar);
            }

            // --- ванночка-корпус вокруг линзы (почти чёрная, с тонким ободком) ---
            float inTub = 1.0 - smoothstep(1.12, 1.32, rr);
            float wall = smoothstep(1.00, 1.04, rr);
            outc += float3(0.040, 0.045, 0.053) * inTub * wall;
            float lipLine = smoothstep(0.995, 1.005, rr) * (1.0 - smoothstep(1.03, 1.06, rr));
            outc += float3(0.16, 0.18, 0.21) * lipLine;

            // --- наружное свечение ---
            if (rr > 1.0)
            {
                float g = exp(-(rr - 1.0) * 4.5);
                outc += (col * 0.8 + acc * 0.2) * g * lit * 0.75;
                return outc;
            }

            // --- поверхность линзы: 3D-нормаль и стеклянная оптика ---
            float sn = min(rr, 1.0);
            float z = sqrt(max(0.0, 1.0 - sn * sn));
            float2 nXY = d / R;
            float3 N = float3(nXY, z);
            float3 V = float3(0.0, 0.0, 1.0);

            // Два «оконных» блика, медленно дрейфующих по стеклу — блеск живого стекла
            float wob = sin(uTime * 0.5);
            float3 L1 = normalize(float3(-0.45 + 0.05 * wob, -0.50, 0.74));   // свет для diffuse
            float ndl = max(dot(N, L1), 0.0);
            float fres = pow(1.0 - z, 2.8);

            // Настоящее отражение окружения: вектор отражения смотрим в процедурную
            // «комнату» (два окна). Такое отражение само растягивается по сфере и
            // уезжает к канту с углом — главный признак стекла, а не пластика.
            float3 Rv = reflect(-V, N);
            float3 D1 = normalize(float3(-0.55, -0.75, 0.36 + 0.04 * wob)); // дальнее окно
            float3 D2 = normalize(float3(0.50, -0.58, 0.64 - 0.03 * wob));  // ближайшее
            float d1 = max(dot(Rv, D1), 0.0);
            float d2 = max(dot(Rv, D2), 0.0);
            // дисперсия: красный шире, синий уже — тепная кайма у стекла, холодное ядро
            float3 refl = float3(
                pow(d1, 46.0) + 0.55 * pow(d2, 32.0),
                pow(d1, 58.0) + 0.55 * pow(d2, 42.0),
                pow(d1, 74.0) + 0.55 * pow(d2, 55.0));
            float sheen = pow(d1, 9.0) * 0.55 + pow(d2, 7.0) * 0.25;  // мягкий стеклянный сатин

            // Каустика: стекло собирает свет в яркий серп у нижнего правого края
            float cband = smoothstep(0.70, 0.985, sn) * (1.0 - smoothstep(0.985, 1.0, sn));
            float wBR = pow(max(dot(normalize(d + float2(1e-4, 1e-4)),
                                   normalize(float2(0.55, 0.62))), 0.0), 2.0);
            float caustic = cband * wBR;

            // --- рёбра Френеля: концентрические ступеньки линзы ---
            float rings = acos(sn) * (R * 0.040);                  // ~9–20 колец
            float band = frac(rings);
            float ridge = 0.5 - 0.5 * cos(band * 2.0 * PI);        // 0..1 гребень
            float rib = 0.66 + 0.34 * ridge;                       // затенение ступеньки
            float facet = 0.78 + 0.30 * ridge;                     // ступеньки ловят свет

            // --- выключенное стекло: тёмное, но живое (блики + каустика!) ---
            float3 offc = col * (0.04 + 0.10 * ndl * facet);
            offc += float3(0.10, 0.12, 0.15) * fres * 0.35;                    // холодный кант Френеля
            offc += refl * (0.45 + 0.95 * fres);                     // отражение «комнаты»: к канту сильнее
            offc += float3(0.62, 0.72, 0.92) * sheen * 0.055;        // стеклянный сатин
            offc += (col * 0.75 + float3(0.72, 0.78, 0.88) * 0.35) * caustic * 0.55; // свет, собранный краем

            // --- включённая: ядро + насыщенный цветной край, рёбра модулируют свет ---
            float hot = pow(max(0.0, 1.0 - sn * 1.02), 1.25);
            float3 onc = col * (0.55 + 0.75 * hot) + acc * pow(hot, 2.0) * 0.85;
            onc *= lit * rib;
            onc += col * smoothstep(0.6, 1.0, sn) * lit * 0.22;     // плотнее к канту
            onc += col * fres * lit * 0.30;                         // светящийся кант
            // отражения на стекле ярче, когда лампа горит (свет+отражение вместе)
            onc += refl * (0.5 + 0.9 * lit) * (0.45 + 0.8 * fres);
            onc += (col + float3(1.0, 1.0, 1.0) * 0.5) * caustic * lit * 1.15; // горячая каустика
            // живая зернь стекла
            onc += (hash11(px.x * 1.7 + px.y * 3.3 + uTime * 13.0) - 0.5) * 0.03 * lit;

            outc += offc + onc;   // аддитивно: выключенное стекло + свет (OLED-friendly)
            return outc;
        }

        float4 LampPS(VS_OUT i) : SV_TARGET
        {
            float3 acc = float3(1.0, 0.97, 0.92);   // тёплый белый для горячих сердец
            // uv.y у треугольника растёт вниз по NDC, а у нас y вниз в пикселях клиента:
            float2 px = float2(i.uv.x, 1.0 - i.uv.y) * uRes;
            float3 c = 0;
            c += lamp(px, uL0, uF0, uC0.rgb, acc);
            c += lamp(px, uL1, uF1, uC1.rgb, acc);
            c += lamp(px, uL2, uF2, uC2.rgb, acc);
            c = min(c, 1.0);
            return float4(c, 1.0);
        }
        """;

    // ------------------------------------------------------------------ HLSL

    private const string CsHlsl = """
        cbuffer CB : register(b0)
        {
            float  uDt;   float uLevel; float uFlash; float uBlend;
            int    uFamA; int uMA; int uNA; float uCA;
            int    uFamB; int uMB; int uNB; float uCBf;
            float  uCount; uint uFrame; float uSpinA; float uSpinB;
            // скаляры, не float2: упаковка float2 в cbuffer не детерминирована (72 vs 80)
            float  uCLx; float uCLy; float uCHx; float uCHy;
            float  uPulse; float uWaveR; float uWaveW; float uWaveA;
            float  uASx; float uASy;   // пропорции поля (wide-экран без «плющения»)
        };
        struct P { float2 pos; float spark; float wave; };
        RWStructuredBuffer<P> g_P : register(u0);

        static const float PI = 3.14159265358979;

        // --- семейства полей, CPU-зеркало: ChladniModes.Field ---
        void PairField(float2 p, int m, int n, float c, out float z, out float2 g)
        {
            float u = PI * p.x, v = PI * p.y;
            z = cos(n*u) * cos(m*v) + c * cos(m*u) * cos(n*v);
            g = float2(
                PI * (-n * sin(n*u) * cos(m*v) - c * m * sin(m*u) * cos(n*v)),
                PI * (-m * cos(n*u) * sin(m*v) - c * n * cos(m*u) * sin(n*v)));
        }

        void FieldF(float2 p, int fam, int a, int b, float c, out float z, out float2 g)
        {
            if (fam == 1)   // повёрнутая пара мод
            {
                float s = sin(c), co = cos(c);
                float dx = p.x - 0.5, dy = p.y - 0.5;
                float2 pr = float2(co*dx - s*dy + 0.5, s*dx + co*dy + 0.5);
                PairField(pr, a, b, 0.4, z, g);
                float gxr = g.x, gyr = g.y;   // Rᵀ
                g = float2(co*gxr + s*gyr, -s*gxr + co*gyr);
                return;
            }
            if (fam == 2)   // гексагональная интерференция трёх волн
            {
                float q = PI * (float)a;
                float px = p.x - 0.5, py = p.y - 0.5;
                z = 0.0; g = float2(0.0, 0.0);
                for (int i = 0; i < 3; i = i + 1)
                {
                    float an = c + (float)i * (2.0 * PI / 3.0);
                    float kx = cos(an) * q, ky = sin(an) * q;
                    float ph = kx * px + ky * py;
                    z += cos(ph);
                    g += float2(-sin(ph) * kx, -sin(ph) * ky);
                }
                return;
            }
            if (fam == 3)   // полярная розетка: кольца × лучи
            {
                float px = p.x - 0.5, py = p.y - 0.5;
                float r = sqrt(px*px + py*py) + 1e-6;
                float r2 = 2.0 * r, t = atan2(py, px);
                float c1 = cos((float)a * PI * r2), s1 = sin((float)a * PI * r2);
                float c2 = cos((float)b * t + c * PI), s2 = sin((float)b * t + c * PI);
                z = c1 * c2;
                float dzr = -(float)a * PI * 2.0 * s1 * c2;
                float dzt = -(float)b * c1 * s2;
                g = float2(dzr * px / r - dzt * py / (r*r),
                           dzr * py / r + dzt * px / (r*r));
                return;
            }
            PairField(p, a, b, c, z, g);
        }
        float Rand(uint2 c)
        {
            uint h = c.x * 1973u + c.y * 9277u + uFrame * 26699u;
            h = (h ^ (h >> 13u)) * 1274126177u;
            h = h ^ (h >> 16u);
            return (h & 0xFFFFu) / 65535.0;
        }

        [numthreads(128, 1, 1)]
        void CS(uint3 id : SV_DispatchThreadID)
        {
            uint i = id.x;
            if (i >= (uint)uCount) return;
            P p = g_P[i];

            float drive = min(uLevel + 0.6 * uFlash, 1.0);
            // поворот узора целиком (spin) — крутится только пока шевелится спектр
            // «дыхание»: на ударах поле слегка раздувается (координаты сжимаем)
            float2 pc = (p.pos - 0.5f) * (1.0f - uPulse);
            // порядок: экран → равномерные «пиксельные» координаты (aspect, за
            // [0,1] поле продолжается естественно) → поворот (spin) → поле.
            // Спираль/скашив нет: вращение происходит в изотропном пространстве.
            float2 aS = float2(uASx, uASy);
            float sa = sin(uSpinA), ca = cos(uSpinA);
            float2 qA = aS * pc;
            float2 rA = float2(ca * qA.x - sa * qA.y, sa * qA.x + ca * qA.y);
            float2 pA = rA + 0.5f;
            float zA; float2 gA; FieldF(pA, uFamA, uMA, uNA, uCA, zA, gA);
            // ∇ в экранных координатах: Rᵀ, затем масштаб aspect (цепное правило)
            float2 tA = float2(ca * gA.x + sa * gA.y, -sa * gA.x + ca * gA.y);
            gA = aS * tA;

            float sb = sin(uSpinB), cb2 = cos(uSpinB);
            float2 qB = aS * pc;
            float2 rB = float2(cb2 * qB.x - sb * qB.y, sb * qB.x + cb2 * qB.y);
            float2 pB = rB + 0.5f;
            float zB; float2 gB; FieldF(pB, uFamB, uMB, uNB, uCBf, zB, gB);
            float2 tB = float2(cb2 * gB.x + sb * gB.y, -sb * gB.x + cb2 * gB.y);
            gB = aS * tB;

            float z = lerp(zA, zB, uBlend);
            float2 g = lerp(gA, gB, uBlend);

            // Ньютонова проекция к узлу (см. CPU-движок): шаг = -λ·Z·∇Z/|∇Z|²,
            // экспоненциальная сходимость без перелётов через линию.
            float gm2 = dot(g, g) + 1e-8f;
            float lam = min(1.0, (16.0f + 34.0f * drive) * uDt);
            float2 v = -lam * z / gm2 * g;
            float vm = length(v);
            float maxStep = 1.5f * uDt;
            if (vm > maxStep) v = v * (maxStep / vm);

            // фоновое дрожание песка — минимальное: линии тонкие и «в фокусе»,
            // живость дают вспышки яркости, а не разлёт
            float jit = 0.0007f + 0.006f * drive;
            float2 r = float2(Rand(uint2(i, 11u)), Rand(uint2(i, 37u))) * 2.0 - 1.0;
            p.pos += v + r * jit;   // v — смещение за шаг, dt уже внутри λ

            // разлёт — только на самой атаке, короткий и слабый: акцент делает
            // вспышка светимости, а не песок во всё поле
            if (drive > 0.6 && Rand(uint2(i, 53u)) < 0.001 + 0.012 * uFlash)
            {
                float2 kick = float2(Rand(uint2(i, 71u)), Rand(uint2(i, 97u))) * 2.0 - 1.0;
                p.pos += kick * 0.020;
            }

            // искры: каждой 64-й частице добавляем скольжение ВДОЛЬ узловой линии —
            // касательное движение даёт настоящее «бегущее» оживление узора,
            // без разлёта поперёк (картинка остаётся в фокусе)
            if (p.spark > 0.5f)
            {
                float2 tg = float2(-g.y, g.x);
                float tl = length(tg);
                if (tl > 1e-5f)
                {
                    // направление искры медленно «передумывает» — искры вьются по линии
                    float dir = Rand(uint2(i, uFrame / 41u)) > 0.5f ? 1.0f : -1.0f;
                    p.pos += (tg / tl) * dir * (0.0035f + 0.0085f * uFlash) * (0.35f + drive);
                }
            }

            p.pos = clamp(p.pos, float2(uCLx, uCLy), float2(uCHx, uCHy));

            // волна энергии: фронт от центра (uWaveR) подсвечивает частицы на проходе
            // (в экранных пропорциях — фронт круглый на экране, а не эллипс)
            float dw = length((p.pos - 0.5f) * aS);
            p.wave = uWaveA * max(0.0, 1.0 - abs(dw - uWaveR) / max(uWaveW, 1e-4f));
            g_P[i] = p;
        }
        """;

    private const string VsHlsl = """
        cbuffer CB : register(b0)
        {
            float4 uColor;
            float2 uPlatePos; float2 uPlateSize;
            float  uSize;  float uAlpha; float2 uTarget;
            float  uSoft;  float2 uPad2;
        };
        struct P { float2 pos; float spark; float wave; };
        StructuredBuffer<P> g_P : register(t0);

        struct VSOut
        {
            float4 pos : SV_Position;
            float2 local : TEXCOORD0;
            float  soft : TEXCOORD1;
            float2 sw : TEXCOORD2;    // spark, wave-hit
        };

        VSOut VS(uint vid : SV_VertexID, uint iid : SV_InstanceID)
        {
            static const float2 CORNERS[6] = {
                float2(-1,-1), float2(1,-1), float2(1,1),
                float2(-1,-1), float2(1,1),  float2(-1,1)
            };
            VSOut o;
            P pt = g_P[iid];
            float2 center = uPlatePos + pt.pos * uPlateSize;
            // искры крупнее обычных частиц
            float2 px = center + CORNERS[vid] * (uSize * (1.0 + 0.7 * pt.spark));
            o.local = CORNERS[vid];
            o.pos = float4(px.x / uTarget.x * 2.0 - 1.0,
                           1.0 - px.y / uTarget.y * 2.0, 0.0, 1.0);
            o.soft = uSoft;
            o.sw = float2(pt.spark, pt.wave);
            return o;
        }
        """;

    private const string PsHlsl = """
        cbuffer CB : register(b0)
        {
            float4 uColor;
            float2 uPlatePos; float2 uPlateSize;
            float  uSize;  float uAlpha; float2 uTarget;
            float  uSoft;  float2 uPad2;
        };
        float4 PS(float4 pos : SV_Position, float2 local : TEXCOORD0, float soft : TEXCOORD1,
                  float2 sw : TEXCOORD2) : SV_Target
        {
            float d = length(local);
            if (d > 1.0) discard;
            float a = lerp(max(0.0, 1.0 - d * d),  // ядро: плотный диск
                           (1.0 - d) * (1.0 - d),  // ореол: мягкий
                           soft);
            a *= uAlpha;
            // волна энергии и искры: вспышка яркости + сдвиг к белому (расфокуса нет!)
            float boost = 1.0 + 1.5 * sw.y + 0.7 * sw.x;
            a = min(a * boost, 1.35);
            float3 c = uColor.rgb + float3(0.5, 0.55, 0.7) * min(1.0, 0.9 * sw.y + 0.35 * sw.x);
            return float4(c * a, a);      // аддитивное смешивание
        }
        """;

    // ------------------------------------------------------------------ Win32 child window

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000; // НЕ 0x00400000 — это WS_DLGFRAME (белая рамка)!
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_LBUTTONDOWN = 0x0201;
}
