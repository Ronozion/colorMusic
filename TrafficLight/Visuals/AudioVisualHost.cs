using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TrafficLight.Audio;

namespace TrafficLight.Visuals;

public enum VisualMode { TrafficLight, Particles }
public enum ParticleField { ThreePlates, SingleField }

/// <summary>
/// Полноэкранный хост визуализации: светофор либо частицы-фигуры Хладни
/// (три пластины или одно общее поле). Сам следит за временем, мерцанием
/// и плавными переходами. Палитра рассчитана на OLED: фон — чистый чёрный.
/// </summary>
public sealed class AudioVisualHost : FrameworkElement
{
    // Полосы для частиц: 0 = басы (красный), 1 = середина (зелёный), 2 = верха (жёлтый)
    private static readonly Color[] BandColors =
    {
        Color.FromRgb(0xFF, 0x41, 0x36),
        Color.FromRgb(0x37, 0xD4, 0x5A),
        Color.FromRgb(0xFF, 0xC4, 0x00)
    };

    private static readonly string[] BandNames = ["БАСЫ 20–250 Гц", "СЕРЕДИНА 250–2000 Гц", "ВЕРХА 2–16 кГц"];

    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly ParticleEngine _engine = new();
    private readonly Random _rng = new();

    // Состояние ламп (индексы как в LampField: 0 красный/басы, 1 зелёный/середина, 2 жёлтый/верха)
    private readonly double[] _lit = new double[3];
    private readonly double[] _litVel = new double[3]; // скорость яркости (GPU-зеркало)
    private readonly double[] _flicker = { 1, 1, 1 };

    private VisualMode _mode = VisualMode.TrafficLight;
    private ParticleField _field = ParticleField.ThreePlates;

    private double _bass, _mid, _high;
    private double _thB = 0.5, _thM = 0.5, _thH = 0.5;
    private double _freqB = 70.7, _freqM = 707, _freqH = 5657;
    private double _centB = 70.7, _centM = 707, _centH = 5657;
    private double _flxB, _flxM, _flxH;

    private long _lastTick = Environment.TickCount64;

    public VisualMode Mode
    {
        get => _mode;
        set { _mode = value; _lastTick = Environment.TickCount64; InvalidateVisual(); }
    }

    public ParticleField Field
    {
        get => _field;
        set { _field = value; InvalidateVisual(); }
    }

    public void SetFrame(double bass, double mid, double high,
                         double thB, double thM, double thH,
                         double freqB, double freqM, double freqH,
                         double centB, double centM, double centH,
                         double fluxB, double fluxM, double fluxH)
    {
        _bass = bass; _mid = mid; _high = high;
        _thB = thB; _thM = thM; _thH = thH;
        _freqB = freqB; _freqM = freqM; _freqH = freqH;
        _centB = centB; _centM = centM; _centH = centH;
        _flxB = fluxB; _flxM = fluxM; _flxH = fluxH;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        long now = Environment.TickCount64;
        double dt = Math.Clamp((now - _lastTick) / 1000.0, 0.0005, 0.1);
        _lastTick = now;

        double w = ActualWidth, h = ActualHeight;
        if (w < 30 || h < 30)
            return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_mode == VisualMode.TrafficLight)
        {
            UpdateLamps(dt);
            double[] levels = [_bass, _mid, _high];
            LampField.Render(dc, w, h, levels, _lit, _flicker, dpi);
        }
        else
        {
            RenderParticles(dc, w, h, dt, dpi);
        }
    }

    private void UpdateLamps(double dt)
    {
        double[] levels = [_bass, _mid, _high];
        double[] ths = [_thB, _thM, _thH];

        for (int i = 0; i < 3; i++)
        {
            double target = levels[i] >= ths[i]
                ? Math.Clamp(0.55 + (levels[i] - ths[i]) * 1.1, 0.55, 1.0)
                : 0.0;

            // зеркало GPU: критически демпфированный контур — плавно (без ступенек
            // анализатора) и с быстрой атакой
            double wn = target > _lit[i] ? 18.0 : 4.2;
            double zeta = target > _lit[i] ? 0.9 : 1.0;
            double rem = Math.Min(dt, 0.1);
            while (rem > 1e-4)
            {
                double h = Math.Min(rem, 0.008);
                double x = _lit[i] - target;
                double acc = -(wn * wn) * x - 2.0 * zeta * wn * _litVel[i];
                _litVel[i] += acc * h;
                _lit[i] += _litVel[i] * h;
                if (_lit[i] < 0.0) { _lit[i] = 0.0; _litVel[i] = Math.Max(0.0, _litVel[i]); }
                else if (_lit[i] > 1.06) { _lit[i] = 1.06; _litVel[i] = Math.Min(0.0, _litVel[i]); }
                rem -= h;
            }

            if (_lit[i] > 0.08)
            {
                // Случайное блуждание яркости + редкие провалы — «лампа накаливания»
                double f = Math.Clamp(_flicker[i] + (_rng.NextDouble() - 0.5) * 0.30 * dt * 30.0, 0.60, 1.0);
                if (_rng.NextDouble() < 0.02)
                    f *= 0.55;
                _flicker[i] = f;
            }
            else
            {
                _flicker[i] = 1.0;
            }
        }
    }

    private void RenderParticles(DrawingContext dc, double w, double h, double dt, double dpi)
    {
        double[] levels = [_bass, _mid, _high];
        double[] freqs = [_freqB, _freqM, _freqH];
        double[] cents = [_centB, _centM, _centH];
        double[] flux = [_flxB, _flxM, _flxH];
        double[][] bandRange =
        [
            [FftAnalyzer.BassLow, FftAnalyzer.BassHigh],
            [FftAnalyzer.BassHigh, FftAnalyzer.MidHigh],
            [FftAnalyzer.MidHigh, FftAnalyzer.HighMax]
        ];

        Rect[] plates = ParticleLayout.Compute(w, h, _field);
        _engine.EdgeInset = _field == ParticleField.SingleField ? 0.002 : 0.02;
        // пропорции поля (GPU-зеркало): квадратные пластины → 1 (режим не меняется);
        // одно поле: короткий край = единица, длинный продолжается за [0,1]
        double engSide = Math.Min(plates[0].Width, plates[0].Height);
        _engine.AspectX = plates[0].Width / engSide;
        _engine.AspectY = plates[0].Height / engSide;

        for (int i = 0; i < 3; i++)
        {
            double norm = 0.5 * ChladniModes.NormFromFreq(freqs[i], bandRange[i][0], bandRange[i][1])
                        + 0.5 * ChladniModes.NormFromFreq(cents[i], bandRange[i][0], bandRange[i][1]);
            _engine.ConfigureBand(i, norm, flux[i], dt);
            _engine.Step(i, levels[i], dt);
        }

        if (_field == ParticleField.SingleField)
        {
            // Общий фон один раз, потом — все три слоя частиц поверх (иначе последний
            // слой стирал бы предыдущие и был бы виден только жёлтый)
            _engine.DrawBackground(dc, plates[0], border: false);
            for (int i = 0; i < 3; i++)
                _engine.DrawParticles(dc, plates[i], i, levels[i], BandColors[i]);
        }
        else
        {
            for (int i = 0; i < 3; i++)
                _engine.Draw(dc, plates[i], i, levels[i], BandColors[i]);
        }

        // Подписи полос
        for (int i = 0; i < 3; i++)
        {
            var c = BandColors[i];
            var color = Color.FromRgb(
                (byte)(c.R / 2 + 0xA0), (byte)(c.G / 2 + 0xA0), (byte)(c.B / 2 + 0xA0));

            var ft = new FormattedText($"{BandNames[i]} · {freqs[i]:F0} Гц · {levels[i]:P0}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                UiFace, Math.Clamp(plates[i].Width * 0.042, 11, 28),
                new SolidColorBrush(color), dpi);

            if (_field == ParticleField.SingleField)
            {
                double x = plates[i].X + plates[i].Width * i / 3.0 + 4;
                dc.DrawText(ft, new Point(x, plates[i].Y + 4));
            }
            else
            {
                dc.DrawText(ft, new Point(plates[i].X, plates[i].Y - ft.Height - 4));
            }
        }
    }
}
