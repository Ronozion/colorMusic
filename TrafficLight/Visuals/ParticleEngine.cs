using System.Windows;
using System.Windows.Media;

namespace TrafficLight.Visuals;

/// <summary>
/// Частицы-«песок» на трёх вибрирующих пластинах (опыты Хладни).
/// Каждая полоса звука — своя пластина: частицы сходятся к узловым линиям
/// стоячей волны. Вид узора зависит от доминирующей частоты полосы:
/// чем выше частота — тем сложнее мода пластины. Уровень полосы — это
/// «тряска»: на сильных атаках частицы разлетаются, потом снова оседают на узлах.
/// </summary>
public sealed class ParticleEngine
{
    public static readonly int PerBand = 1000;

    // Моды взяты из общей таблицы ChladniModes

    private static readonly Brush PlateFill = Frozen(new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)));
    private static readonly Pen PlatePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x33, 0x39, 0x42)), 1));

    private sealed class BandParticles
    {
        public readonly float[] X = new float[PerBand];
        public readonly float[] Y = new float[PerBand];
        public int From;
        public int To;
        public double T = 1.0;          // бленд двух соседних узоров (из спектра, не из таймера)
        public double PrevLevel;
        public double Flash;            // вспышка: 1 в момент резкой атаки, потом гаснет
        public double Nrm = 0.5;        // сглаженная доминанта+тембр (быстрая)
        public double NrmSlow = 0.5;    // медленный EMA той же тональности
        public double SlopeEma;         // наклон тональности: + вверх, − вниз
        public double FluxEma;          // собственная нормировка «движения музыки»
        public double Spin;             // поворот узла: направление = наклон тональности

        public BandParticles(Random rng)
        {
            for (int i = 0; i < PerBand; i++)
            {
                X[i] = 0.02f + (float)rng.NextDouble() * 0.96f;
                Y[i] = 0.02f + (float)rng.NextDouble() * 0.96f;
            }
        }
    }

    /// <summary>Отступ clamp-края от границы поля: для одного поля — почти ноль,
    /// для трёх пластин заметный (визуальный зазор). Ставится хостом под режим.</summary>
    public double EdgeInset { get; set; } = 0.02;

    /// <summary>Пропорции поля (GPU-зеркало): wide-экран одного поля без «плющения».
    /// Для квадратных пластин = 1 (режим трёх пластин не меняется).</summary>
    public double AspectX { get; set; } = 1.0;
    public double AspectY { get; set; } = 1.0;

    private readonly BandParticles[] _bands =
    {
        new(Random.Shared), new(Random.Shared), new(Random.Shared)
    };
    private readonly Random _rng = new();

    /// <summary>Полоса получила доминирующую частоту (0..1 внутри своего диапазона) — выбираем моду узора.</summary>
    public void ConfigureBand(int band, double norm, double flux, double dt)
    {
        var bp = _bands[band];

        // позиция в таблице узоров — непрерывная функция спектра (как в GPU-хосте):
        // морфинг идёт за музыкой, а не за таймером
        bp.Nrm += (norm - bp.Nrm) * Math.Min(1.0, dt * 1.6);
        double pos = bp.Nrm * (ChladniModes.Modes.Length - 1);
        int i0 = (int)Math.Floor(pos);
        bp.From = Math.Clamp(i0, 0, ChladniModes.Modes.Length - 1);
        bp.To = Math.Min(bp.From + 1, ChladniModes.Modes.Length - 1);
        double fr = pos - i0;
        bp.T = fr * fr * (3.0 - 2.0 * fr);   // smoothstep

        // поворот узла направлен движением тональности: вверх/вниз по спектру —
        // разные стороны; наклон = рассогласование двух EMA / разницу их постоянных
        bp.NrmSlow += (norm - bp.NrmSlow) * Math.Min(1.0, dt * 0.4);
        double slope = (bp.Nrm - bp.NrmSlow) / 1.875;
        bp.SlopeEma += (slope - bp.SlopeEma) * Math.Min(1.0, dt * 2.5);
        bp.FluxEma += (flux - bp.FluxEma) * Math.Min(1.0, dt * 0.5);
        double fn = Math.Clamp(flux / (bp.FluxEma + 1e-9), 0.0, 2.0);
        double spinRate = 3.2 * Math.Tanh(bp.SlopeEma * 4.0) + 0.35 * fn;
        bp.Spin = (bp.Spin + spinRate * dt) % (Math.PI * 2.0);
    }

    public void Step(int band, double level, double dt)
    {
        var bp = _bands[band];

        var modeA = ChladniModes.Modes[bp.From];
        var modeB = ChladniModes.Modes[bp.To];
        double blend = bp.T;
        double ss = Math.Sin(bp.Spin), cs = Math.Cos(bp.Spin);

        // Вспышка: резкая атака полосы зажигает ЯРЧЕ сами частицы (фон не подкрашиваем),
        // гаснет ~за полсекунды
        bp.Flash = Math.Max(0.0, bp.Flash - dt * 2.2);
        if (level - bp.PrevLevel > 0.22)
            bp.Flash = 1.0;
        else
            bp.Flash = Math.Max(bp.Flash, 0.55 * Math.Clamp((level - 0.82) * 3.0, 0.0, 1.0));

        // Уровень с поправкой на вспышку — сильнее тряска пластины
        double drive = Math.Min(1.0, level + 0.6 * bp.Flash);

        bp.PrevLevel = level;

        for (int i = 0; i < PerBand; i++)
        {
            float x = bp.X[i], y = bp.Y[i];

            // координаты: экран → равномерные «пиксельные» (aspect, поле продолжается
            // за [0,1]) → поворот spin → поле; градиент обратно: Rᵀ, затем aspect
            double dxr = (x - 0.5) * AspectX, dyr = (y - 0.5) * AspectY;
            double xr = cs * dxr - ss * dyr + 0.5, yr = ss * dxr + cs * dyr + 0.5;
            var (z, fgx, fgy) = FieldBlended(xr, yr, modeA, modeB, blend);
            double tgx = cs * fgx + ss * fgy, tgy = -ss * fgx + cs * fgy;
            double gx = AspectX * tgx;
            double gy = AspectY * tgy;

            // Проекция к узловой линии методом Ньютона: шаг = -λ·Z·∇Z/|∇Z|².
            // В линейной зоне это точный прыжок на линию; |Z| гаснет в (1-λ) раз
            // за шаг — перелётов через узел и «дрожи» линии нет ни при каком dt
            // (в отличие от пружины v=-k·Z·∇Z, где k|∇Z|²·dt ≫ 2 = расчёт неустойчив).
            double gm2 = gx * gx + gy * gy + 1e-9;
            double lam = Math.Min(1.0, (16.0 + 34.0 * drive) * dt);
            double vx = -lam * z * gx / gm2;
            double vy = -lam * z * gy / gm2;
            double vm = Math.Sqrt(vx * vx + vy * vy);
            double maxStep = 1.5 * dt;
            if (vm > maxStep)
            {
                vx *= maxStep / vm;
                vy *= maxStep / vm;
            }

            // джиттер минимальный (GPU-зеркало с поправкой на ~2.6× более редкие шаги):
            // резкие линии, акценты — светимостью на атаке, а не разлётом
            double jitter = 0.0009 + 0.010 * drive;
            // vx/vy — смещение за шаг (не скорость): множитель dt уже внутри λ
            x += (float)vx + (float)(_rng.NextDouble() * 2 - 1) * (float)jitter;
            y += (float)vy + (float)(_rng.NextDouble() * 2 - 1) * (float)jitter;

            // Разлёт — только на самой атаке, короткий и слабый
            if (drive > 0.6 && _rng.NextDouble() < 0.0025 + 0.03 * bp.Flash)
            {
                x += (float)(_rng.NextDouble() * 2 - 1) * 0.024f;
                y += (float)(_rng.NextDouble() * 2 - 1) * 0.024f;
            }

            float lo = (float)EdgeInset, hi = (float)(1.0 - EdgeInset);
            if (x < lo) x = lo; else if (x > hi) x = hi;
            if (y < lo) y = lo; else if (y > hi) y = hi;

            bp.X[i] = x;
            bp.Y[i] = y;
        }
    }

    /// <summary>Фон пластины (рисуется один раз, до всех слоёв частиц).</summary>
    public void DrawBackground(DrawingContext dc, Rect plate, bool border = true)
    {
        dc.DrawRectangle(PlateFill, border ? PlatePen : null, plate);
    }

    /// <summary>Только частицы полосы: фон не трогаем, вспышки — исключительно на самих частицах.</summary>
    public void DrawParticles(DrawingContext dc, Rect plate, int band, double level, Color color)
    {
        var bp = _bands[band];
        double a = Math.Clamp(0.18 + 0.82 * level, 0.0, 1.0);
        double flash = bp.Flash;
        double dot = Math.Clamp(Math.Min(plate.Width, plate.Height) / 260.0, 1.2, 3.6);

        // Ореолы частиц: растут и разгораются на вспышке
        double glowAlpha = a * (level > 0.4 ? 0.24 : 0.08) + 0.45 * flash;
        if (glowAlpha > 0.03)
        {
            double glowR = dot * (3.4 + 1.6 * flash);
            var glow = Frozen(Color.FromArgb((byte)(255 * Math.Min(glowAlpha, 0.85)), color.R, color.G, color.B));
            for (int i = 0; i < PerBand; i++)
                dc.DrawEllipse(glow, null, Map(bp, i, plate), glowR, glowR);
        }

        // Самые частицы: на вспышке — ярче и крупнее
        double dotAlpha = Math.Clamp(a + 0.45 * flash, 0.0, 1.0);
        double dotR = dot * (1.0 + 0.7 * flash);
        var brush = Frozen(Color.FromArgb((byte)(255 * dotAlpha), color.R, color.G, color.B));
        for (int i = 0; i < PerBand; i++)
            dc.DrawEllipse(brush, null, Map(bp, i, plate), dotR, dotR);
    }

    public void Draw(DrawingContext dc, Rect plate, int band, double level, Color color)
    {
        DrawBackground(dc, plate);
        DrawParticles(dc, plate, band, level, color);
    }

    private static Point Map(BandParticles bp, int i, Rect plate)
        => new(plate.X + bp.X[i] * plate.Width, plate.Y + bp.Y[i] * plate.Height);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // --- поля узоров: общий_evaluator ChladniModes.Field (CPU и HLSL совпадают 1-в-1) ---

    private static (double Z, double GX, double GY) FieldBlended(
        double x, double y, ChladniModes.Mode a, ChladniModes.Mode b, double t)
    {
        ChladniModes.Field(a, x, y, out double za, out double gxa, out double gya);
        if (t >= 1.0)
            return (za, gxa, gya);

        ChladniModes.Field(b, x, y, out double zb, out double gxb, out double gyb);
        return (za * (1 - t) + zb * t,
                gxa * (1 - t) + gxb * t,
                gya * (1 - t) + gyb * t);
    }
}
