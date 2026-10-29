using NAudio.Dsp;

namespace TrafficLight.Audio;

/// <summary>
/// Считает три частотные полосы (басы / середина / верха) через FFT.
/// Уровень полосы = средний уровень верхних 1/8 бинов полосы (иначе разреженные
/// компоненты вроде тарелок «размазываются» пустыми бинами и тонут).
/// Каждая полоса автоматически нормализуется по своему скользящему 95-му
/// перцентилю (~7 с); шипение и тишина отсекаются гейтом и порогом шума.
/// </summary>
public sealed class FftAnalyzer
{
    private const int Order = 13;          // 2^13 = 8192 отсчётов в окне
    private const int Size = 1 << Order;
    private const int Hop = Size / 4;      // обработка каждые 2048 отсчётов (~23 раза/с при 48 кГц)

    private const int HistSize = 100;      // ~4 с истории для перцентиля
    private const double TimePercentile = 0.95;
    private const double Release = 0.90;   // сглаживание спада
    private const double FixedRef = 0.15;  // опорный уровень при выключенной автономизации

    private const double SilenceGate = 0.001;  // RMS всего звука ниже — считаем тишиной
    private const double MinFloor = 0.00001;   // защита от деления на цифровой дитер

    // Границы полос, Гц
    public const double BassLow = 20.0;
    public const double BassHigh = 250.0;
    public const double MidHigh = 2000.0;
    public const double HighMax = 16000.0;

    private readonly float[] _ring = new float[Size];
    private readonly float[] _window = new float[Size];
    private readonly Complex[] _fft = new Complex[Size];
    private readonly float[] _hann = new float[Size];

    // Амплитуды бинов по полосам (заполняются каждый цикл, сортируются)
    private readonly float[] _bassAmps;
    private readonly float[] _midAmps;
    private readonly float[] _highAmps;
    private readonly int _maxBin;

    // История уровней полос во времени (кольцевые буферы)
    private readonly float[] _histBass = new float[HistSize];
    private readonly float[] _histMid = new float[HistSize];
    private readonly float[] _histHigh = new float[HistSize];
    private readonly float[] _histMaster = new float[HistSize];
    private readonly float[] _timeScratch = new float[HistSize];
    private int _histPos;
    private int _histFilled;

    private int _write;
    private long _total;
    private long _lastProcessed;

    // Присутствие полосы: безразмерные отношения сырых уровней к соседям
    // (лог-EMA ~2 с). Если полосы нет в треке (нет баса / срезанные верха),
    // нормализатор не имеет права «вытягивать» её шум до 100 %.
    private double _lpB = Math.Log(1.2);    // ln(rawB / rawM)
    private double _lpM = Math.Log(0.7);    // ln(rawM / (rawB + rawH))
    private double _lpH = Math.Log(0.18);   // ln(rawH / rawM)
    private double _pgB = 1, _pgM = 1, _pgH = 1;

    /// <summary>Текущие presence-гейты полос (0..1) — для диагностики.</summary>
    public double[] GetBandGates() => [_pgB, _pgM, _pgH];

    private double _bass;
    private double _mid;
    private double _high;

    // Доминирующая частота каждой полосы (сглажена в лог-шкале) — для узоров частиц
    private double _logFreqB = Math.Log(70.7);   // геометрические центры полос по умолчанию
    private double _logFreqM = Math.Log(707.1);
    private double _logFreqH = Math.Log(5656.9);

    // Геометрический центроид энергии полосы (тоже в лог-шкале, сглажен) — тембр,
    // двигает узор сильнее и живее, чем одна доминанта
    private double _centB = Math.Log(70.7);
    private double _centM = Math.Log(707.1);
    private double _centH = Math.Log(5656.9);

    // Поток спектра полосы: средняя |Δамплитуда| бинов между кадрами — «как сильно
    // музыка шевелится». Управляете скоростью жизни узора (пока музыка стоит — стоит и узор).
    private readonly float[] _prevAmp;
    private double _fluxB, _fluxM, _fluxH;

    private readonly int _sampleRate;
    private readonly double _binHz;
    private const float Scale = 4f / Size; // нормировка окна Ханна: синус амплитуды A -> бины ~A

    public FftAnalyzer(int sampleRate)
    {
        _sampleRate = sampleRate;
        _binHz = (double)sampleRate / Size;
        _maxBin = (int)Math.Min(HighMax / _binHz, _sampleRate / 2.0);

        _bassAmps = new float[_maxBin + 1];
        _midAmps = new float[_maxBin + 1];
        _highAmps = new float[_maxBin + 1];
        _prevAmp = new float[_maxBin + 1];

        for (int i = 0; i < Size; i++)
            _hann[i] = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / Size));
    }

    /// <summary>Включена ли автономизация по каждой полосе.</summary>
    public bool AutoGain { get; set; } = true;

    /// <summary>Общая чувствительность — множитель к уже нормализованным уровням, 0.5..2.</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>Пороги включения ламп, 0..1 (доля от пика самой полосы).</summary>
    public double BassThreshold { get; set; } = 0.50;
    public double MidThreshold { get; set; } = 0.50;
    public double HighThreshold { get; set; } = 0.50;

    /// <summary>Добавить порцию моно-отсчётов (вызывается из потока захвата).</summary>
    public void AddSamples(float[] mono, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _ring[_write] = mono[i];
            _write = (_write + 1) % Size;
            _total++;
        }

        if (_total < Size)
            return;

        while (_total - _lastProcessed >= Hop)
        {
            ProcessWindow();
            _lastProcessed += Hop;
        }
    }

    /// <summary>Сглаженные нормализованные уровни полос 0..1.</summary>
    public (double Bass, double Mid, double High) GetLevels() => (_bass, _mid, _high);

    /// <summary>Доминирующие частоты полос, Гц (для подбора узора частиц).</summary>
    public (double BassHz, double MidHz, double HighHz) GetBandFrequencies()
        => (Math.Exp(_logFreqB), Math.Exp(_logFreqM), Math.Exp(_logFreqH));

    /// <summary>Геометрические центроиды энергии полос, Гц (тембр).</summary>
    public (double BassHz, double MidHz, double HighHz) GetBandCentroids()
        => (Math.Exp(_centB), Math.Exp(_centM), Math.Exp(_centH));

    /// <summary>Поток спектра полос (средняя |Δамплитуд| бинов между кадрами) — «движение музыки».</summary>
    public (double Bass, double Mid, double High) GetBandFlux() => (_fluxB, _fluxM, _fluxH);

    private void ProcessWindow()
    {
        // _write указывает на самый старый отсчёт последних Size сэмплов
        Array.Copy(_ring, _write, _window, 0, Size - _write);
        Array.Copy(_ring, 0, _window, Size - _write, _write);

        for (int i = 0; i < Size; i++)
        {
            float v = _window[i];
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            _fft[i].X = v * _hann[i];
            _fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(false, Order, _fft);

        int bc = 0, mc = 0, hc = 0;
        double sumMag = 0;
        float topB = 0, topM = 0, topH = 0;   // сильнейший бин каждой полосы
        double topFB = 0, topFM = 0, topFH = 0;
        double fluxB = 0, fluxM = 0, fluxH = 0;      // поток спектра (|Δамплитуд|)
        double wB = 0, wM = 0, wH = 0, lfb = 0, lfm = 0, lfh = 0; // центроиды (лог-вес)
        for (int k = 1; k <= _maxBin; k++)
        {
            double f = k * _binHz;
            if (f < BassLow)
                continue;

            double mag = (double)_fft[k].X * _fft[k].X + (double)_fft[k].Y * _fft[k].Y;
            float amp = (float)(Math.Sqrt(mag) * Scale);

            sumMag += mag;
            double dA = Math.Abs(amp - _prevAmp[k]);
            _prevAmp[k] = amp;
            double lnF = Math.Log(f);
            if (f < BassHigh)
            {
                _bassAmps[bc++] = amp;
                fluxB += dA; wB += amp; lfb += lnF * amp;
                if (amp > topB) { topB = amp; topFB = f; }
            }
            else if (f < MidHigh)
            {
                _midAmps[mc++] = amp;
                fluxM += dA; wM += amp; lfm += lnF * amp;
                if (amp > topM) { topM = amp; topFM = f; }
            }
            else
            {
                _highAmps[hc++] = amp;
                fluxH += dA; wH += amp; lfh += lnF * amp;
                if (amp > topH) { topH = amp; topFH = f; }
            }
        }

        double rawB = BinLevel(_bassAmps, bc);
        double rawM = BinLevel(_midAmps, mc);
        double rawH = BinLevel(_highAmps, hc);

        // Центроид тембра и поток спектра полосы
        double floorW = 20 * MinFloor;
        if (wB > floorW) _centB = _centB * 0.6 + (lfb / wB) * 0.4;
        if (wM > floorW) _centM = _centM * 0.6 + (lfm / wM) * 0.4;
        if (wH > floorW) _centH = _centH * 0.6 + (lfh / wH) * 0.4;
        _fluxB = bc > 0 ? fluxB / bc : 0;
        _fluxM = mc > 0 ? fluxM / mc : 0;
        _fluxH = hc > 0 ? fluxH / hc : 0;

        // Куда «смотрит» полоса по частоте — плавно, в лог-шкале, только когда полоса звучит
        if (topFB > 0 && rawB > 5 * MinFloor) _logFreqB = _logFreqB * 0.6 + Math.Log(topFB) * 0.4;
        if (topFM > 0 && rawM > 5 * MinFloor) _logFreqM = _logFreqM * 0.6 + Math.Log(topFM) * 0.4;
        if (topFH > 0 && rawH > 5 * MinFloor) _logFreqH = _logFreqH * 0.6 + Math.Log(topFH) * 0.4;
        // RMS сигнала по Парсевалю (Σ|X|² = Size·Σx²); x2 — компенсация усиления окна Ханна (0.5)
        double masterRms = 2.0 * Math.Sqrt(sumMag) / Size;

        // История общего RMS — для гейта тишины
        int hp = _histPos;
        _histMaster[hp] = (float)masterRms;
        _histPos = (hp + 1) % HistSize;
        if (_histFilled < HistSize) _histFilled++;

        bool silent = GetTimePercentile(_histMaster, _histFilled) < SilenceGate;
        if (silent)
        {
            // Тишина/фоновый шум: обнуляем уровни, историю полос и поток спектра,
            // чтобы перцентили съехали вниз, лампы погасли, а узор «остановился».
            rawB = rawM = rawH = 0;
            _fluxB = _fluxM = _fluxH = 0;
        }

        // Гейт присутствия: где реально «живёт» энергия трека. Отношения сырых
        // уровней безразмерны, лог-EMA ~2 с; если полоса систематически в разы
        // тише соседей — она отсутствует в треке, и авто-нормализация не должна
        // вытягивать её шум до полного огня (иначе «бас загорается» от тишины).
        if (!silent && bc > 0 && mc > 0 && hc > 0)
        {
            double e = MinFloor;
            double pb = Math.Clamp(rawB / (rawM + e), 1e-3, 1e3);
            double pm = Math.Clamp(rawM / (rawB + rawH + e), 1e-3, 1e3);
            double ph = Math.Clamp(rawH / (rawM + e), 1e-3, 1e3);
            _lpB = _lpB * 0.95 + Math.Log(pb) * 0.05;
            _lpM = _lpM * 0.95 + Math.Log(pm) * 0.05;
            _lpH = _lpH * 0.95 + Math.Log(ph) * 0.05;
            _pgB = Smooth01(Math.Exp(_lpB), 0.25, 0.55);
            _pgM = Smooth01(Math.Exp(_lpM), 0.05, 0.20);
            _pgH = Smooth01(Math.Exp(_lpH), 0.025, 0.08);
        }

        if (AutoGain)
        {
            _histBass[hp] = (float)rawB;
            _histMid[hp] = (float)rawM;
            _histHigh[hp] = (float)rawH;

            int n = _histFilled;
            rawB = Normalize(rawB, Math.Max(GetTimePercentile(_histBass, n), MinFloor));
            rawM = Normalize(rawM, Math.Max(GetTimePercentile(_histMid, n), MinFloor));
            rawH = Normalize(rawH, Math.Max(GetTimePercentile(_histHigh, n), MinFloor));
        }
        else
        {
            rawB = Normalize(rawB, FixedRef);
            rawM = Normalize(rawM, FixedRef);
            rawH = Normalize(rawH, FixedRef);
        }

        // Presence-гейт активен при автономизации: пустая полоса не зажигается,
        // даже когда её p95-усиление вылезло из шума
        double gb = AutoGain ? _pgB : 1.0;
        double gm = AutoGain ? _pgM : 1.0;
        double gh = AutoGain ? _pgH : 1.0;
        _bass = Math.Max(rawB * gb, _bass * Release);
        _mid = Math.Max(rawM * gm, _mid * Release);
        _high = Math.Max(rawH * gh, _high * Release);
    }

    /// <summary>
    /// Уровень полосы: среднее от верхних 1/8 отсортированных амплитуд бинов
    /// (не менее 4). Плотные полосы (бас) так почти не страдают, а разреженные
    /// (верхи: тарелки, свист согласных) перестают тонуть в пустых бинах.
    /// </summary>
    private static double BinLevel(float[] amps, int count)
    {
        if (count == 0)
            return 0.0;

        Array.Sort(amps, 0, count);
        int k = Math.Max(4, count / 8);
        double sum = 0;
        for (int i = count - k; i < count; i++)
            sum += amps[i];
        return sum / k;
    }

    private double Normalize(double raw, double reference)
    {
        double v = raw / Math.Max(reference, 1e-6);
        return Math.Clamp(v * Sensitivity, 0.0, 1.0);
    }

    private static double Smooth01(double x, double a, double b)
    {
        double t = Math.Clamp((x - a) / (b - a), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private double GetTimePercentile(float[] hist, int n)
    {
        if (n == 0)
            return 0.0;
        Array.Copy(hist, _timeScratch, n);
        Array.Sort(_timeScratch, 0, n);
        return _timeScratch[(int)(TimePercentile * (n - 1))];
    }
}
