using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TrafficLight.Audio;
using TrafficLight.Visuals;
using TrafficLight.Visuals.Gpu;

namespace TrafficLight;

public partial class MainWindow : Window
{
    private sealed class DeviceChoice
    {
        public string Label { get; init; } = "";
        public MMDevice? Device { get; init; }
        public bool IsLoopback { get; init; }
    }

    private WasapiCapture? _capture;
    private FftAnalyzer? _analyzer;
    private float[] _monoBuf = Array.Empty<float>();
    private bool _closing;

    // Выбранные режимы (и для CPU-хоста, и для GPU)
    private int _modeIndex;                                  // 0 светофор, 1 три пластины, 2 одно поле
    private ParticleField _field = ParticleField.ThreePlates;
    private static readonly int[] ParticleCounts = [20_000, 60_000, 150_000];
    private long _gpuLastTick = Environment.TickCount64;

    // Полноэкранный режим
    private bool _fullscreen;
    private WindowStyle _prevStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResize = ResizeMode.CanResize;
    private RECT _prevBounds; // физические пиксели: DIP-координаты ломаются при смене DPI монитора

    public MainWindow()
    {
        InitializeComponent();
        CompositionTarget.Rendering += OnRendering;
        PopulateDevices();
        PopulateMonitors();
        GpuHost.DoubleClick += () => { if (_fullscreen) SetFullscreen(false); };
        GpuHost.SetCount(ParticleCounts[1]);
    }

    // ---------- визуальный цикл ----------

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_closing || !IsLoaded)
            return;

        double b = 0, m = 0, h = 0, fb = 70.7, fm = 707, fh = 5657;
        double cb = 70.7, cm = 707, ch = 5657;
        double flxB = 0, flxM = 0, flxH = 0;
        var analyzer = _analyzer;
        if (analyzer is not null)
        {
            (b, m, h) = analyzer.GetLevels();
            (fb, fm, fh) = analyzer.GetBandFrequencies();
            (cb, cm, ch) = analyzer.GetBandCentroids();
            (flxB, flxM, flxH) = analyzer.GetBandFlux();
        }

        double[] levels = [b, m, h];
        // узоре «куда смотрит полоса»: половина — высота (доминанта), половина —
        // тембр (центроид): тембр меняется живее и чаще, узор привязан к обоим
        double[] freqNorm =
        [
            MixedNorm(fb, cb, FftAnalyzer.BassLow, FftAnalyzer.BassHigh),
            MixedNorm(fm, cm, FftAnalyzer.BassHigh, FftAnalyzer.MidHigh),
            MixedNorm(fh, ch, FftAnalyzer.MidHigh, FftAnalyzer.HighMax)
        ];
        double[] flux = [flxB, flxM, flxH];

        bool gpu = GpuHost.GpuReady;

        if (gpu)
        {
            double gw = GpuHost.ActualWidth, gh = GpuHost.ActualHeight;
            if (gw > 30 && gh > 30)
            {
                long now = Environment.TickCount64;
                double dt = Math.Clamp((now - _gpuLastTick) / 1000.0, 0.0005, 0.1);
                _gpuLastTick = now;

                for (int i = 0; i < 3; i++)
                {
                    GpuHost.UpdateBand(i, levels[i], freqNorm[i], flux[i], dt);
                    GpuHost.CurrentBandLevel[i] = (levels[i], freqNorm[i]);
                }

                if (_modeIndex != 0)
                {
                    var rects = ParticleLayout.Compute(gw, gh, _field);
                    GpuHost.Render(rects, gw);
                }
                else
                {
                    double[] ths = [BassThresholdSlider.Value, MidThresholdSlider.Value, HighThresholdSlider.Value];
                    GpuHost.UpdateLampBands(levels, ths, dt);
                    GpuHost.RenderLamps(gw, gh);
                }
            }
        }
        else
        {
            VisualHost.SetFrame(b, m, h,
                BassThresholdSlider.Value, MidThresholdSlider.Value, HighThresholdSlider.Value,
                fb, fm, fh, cb, cm, ch, flxB, flxM, flxH);
        }
    }

    private static double MixedNorm(double dominantHz, double centroidHz, double lo, double hi)
        => 0.5 * ChladniModes.NormFromFreq(dominantHz, lo, hi)
         + 0.5 * ChladniModes.NormFromFreq(centroidHz, lo, hi);

    // ---------- видимость CPU-слоя / GPU-слоя (airspace) ----------

    private void UpdateGpuPresentation()
    {
        // GPU теперь ведёт все режимы: и частицы, и шейдерный светофор
        bool gpu = GpuHost.GpuReady;
        GpuHost.Visibility = gpu ? Visibility.Visible : Visibility.Hidden;
        VisualHost.Visibility = gpu ? Visibility.Hidden : Visibility.Visible;
        // В оконном режиме GPU-поле не заезжает под панель настроек: полоса =
        // фактическая ширина плавающей панели + её правый отступ 24 (не на глаз!)
        double strip = SettingsPanel.Visibility == Visibility.Visible
            ? SettingsPanel.ActualWidth + 24
            : 0;
        GpuHost.Margin = new Thickness(0, 0, gpu ? strip : 0, 0);
        VisualHost.Margin = new Thickness(0, 0, gpu ? 0 : strip, 0);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGpuPresentation();
        TryDarkTitleBar();
        if (_modeIndex != 0 && !GpuHost.GpuReady && GpuHost.FailReason is not null)
            StatusText.Text = string.Format(App.T("L.StatusGpuFallbackFmt"), GpuHost.FailReason);
    }

    /// <summary>Тёмная системная шапка окна (DWM), чтобы не выбивалась из тёмной темы.</summary>
    private void TryDarkTitleBar()
    {
        try
        {
            nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref on, 4);      // старый билд Win10
        }
        catch { /* decorative */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    // ---------- список устройств / мониторов ----------

    private void PopulateDevices()
    {
        var choices = new List<DeviceChoice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defRenderId = null, defCaptureId = null;
            try { defRenderId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            try { defCaptureId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { }

            int preferred = -1;
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                bool isDef = d.ID == defRenderId;
                choices.Add(new DeviceChoice { Label = string.Format(App.T("L.DevSystemFmt"), d.FriendlyName) + (isDef ? App.T("L.DevDefault") : ""), Device = d, IsLoopback = true });
                if (isDef && preferred < 0)
                    preferred = choices.Count - 1;
            }

            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                bool isDef = d.ID == defCaptureId;
                choices.Add(new DeviceChoice { Label = string.Format(App.T("L.DevMicFmt"), d.FriendlyName) + (isDef ? App.T("L.DevDefault") : ""), Device = d, IsLoopback = false });
            }

            DeviceBox.ItemsSource = choices;
            if (choices.Count > 0)
                DeviceBox.SelectedIndex = preferred >= 0 ? preferred : 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(App.T("L.StatusEnumErrFmt"), ex.Message);
        }
    }

    private void PopulateMonitors()
    {
        var monitors = Displays.Enumerate();
        MonitorBox.ItemsSource = monitors;
        var primary = monitors.Find(m => m.Primary);
        MonitorBox.SelectedItem = primary ?? monitors.FirstOrDefault();
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => PopulateMonitors();

    // ---------- запуск / остановка ----------

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is null)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (DeviceBox.SelectedItem is not DeviceChoice choice || choice.Device is null)
        {
            StatusText.Text = App.T("L.StatusNoDevice");
            return;
        }

        try
        {
            _capture = choice.IsLoopback
                ? new WasapiLoopbackCapture(choice.Device)
                : new WasapiCapture(choice.Device);

            var wf = _capture.WaveFormat;
            _analyzer = new FftAnalyzer(wf.SampleRate)
            {
                AutoGain = AutoNormCheck.IsChecked == true,
                Sensitivity = SensSlider.Value,
                BassThreshold = BassThresholdSlider.Value,
                MidThreshold = MidThresholdSlider.Value,
                HighThreshold = HighThresholdSlider.Value
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            StartStopButton.Content = App.T("L.Stop");
            StatusText.Text = choice.IsLoopback
                ? string.Format(App.T("L.StatusSysFmt"), choice.Device.FriendlyName, wf.SampleRate)
                : string.Format(App.T("L.StatusMicFmt"), choice.Device.FriendlyName, wf.SampleRate);
        }
        catch (Exception ex)
        {
            Stop();
            StatusText.Text = string.Format(App.T("L.StatusStartErrFmt"), ex.Message);
        }
    }

    private void Stop()
    {
        var cap = _capture;
        _capture = null;
        _analyzer = null;
        if (cap is not null)
        {
            cap.DataAvailable -= OnDataAvailable;
            cap.RecordingStopped -= OnRecordingStopped;
            try { cap.StopRecording(); } catch { /* уже остановлен */ }
            cap.Dispose();
        }
        StartStopButton.Content = App.T("L.Start");
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_capture is not null)
                Stop();
            StatusText.Text = e.Exception is null
                ? App.T("L.StatusStopped")
                : string.Format(App.T("L.StatusInterruptedFmt"), e.Exception.Message);
        });
    }

    // ---------- приём аудио (поток захвата) ----------

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var cap = _capture;
        var analyzer = _analyzer;
        if (cap is null || analyzer is null || e.BytesRecorded == 0)
            return;

        var wf = cap.WaveFormat;
        int frames = e.BytesRecorded / wf.BlockAlign;
        if (frames <= 0)
            return;

        if (_monoBuf.Length < frames)
            _monoBuf = new float[frames];

        int ch = Math.Max(1, wf.Channels);

        if (wf.Encoding == WaveFormatEncoding.IeeeFloat && wf.BitsPerSample == 32)
        {
            int sampleCount = e.BytesRecorded / 4;
            for (int f = 0, s = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch && s < sampleCount; c++, s++)
                    sum += BitConverter.ToSingle(e.Buffer, s * 4);
                _monoBuf[f] = sum / ch;
            }
        }
        else if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 16)
        {
            int sampleCount = e.BytesRecorded / 2;
            for (int f = 0, s = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch && s < sampleCount; c++, s++)
                    sum += BitConverter.ToInt16(e.Buffer, s * 2) / 32768f;
                _monoBuf[f] = sum / ch;
            }
        }
        else if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 32)
        {
            int sampleCount = e.BytesRecorded / 4;
            for (int f = 0, s = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch && s < sampleCount; c++, s++)
                    sum += BitConverter.ToInt32(e.Buffer, s * 4) / 2147483648f;
                _monoBuf[f] = sum / ch;
            }
        }
        else
        {
            return; // неподдерживаемый формат
        }

        analyzer.AddSamples(_monoBuf, frames);
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VisualHost is null)
            return;

        _modeIndex = ModeBox.SelectedIndex;
        switch (_modeIndex)
        {
            case 1:
                VisualHost.Mode = VisualMode.Particles;
                VisualHost.Field = _field = ParticleField.ThreePlates;
                break;
            case 2:
                VisualHost.Mode = VisualMode.Particles;
                VisualHost.Field = _field = ParticleField.SingleField;
                break;
            default:
                VisualHost.Mode = VisualMode.TrafficLight;
                break;
        }
        UpdateGpuPresentation();
    }

    private void ParticleCount_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ParticleCountBox is null)
            return;
        GpuHost.SetCount(ParticleCounts[Math.Clamp(ParticleCountBox.SelectedIndex, 0, ParticleCounts.Length - 1)]);
    }

    // ---------- полноэкранный режим ----------

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen)
            return;
        _fullscreen = on;

        if (on)
        {
            var saved = Displays.GetBoundsPx(this);
            if (saved.Right - saved.Left > 0 && saved.Bottom - saved.Top > 0
                && saved.Left > -32000) // свёрнутое окно отдаёт -32000-координаты — не сохраняем
                _prevBounds = saved;
            _prevStyle = WindowStyle; _prevResize = ResizeMode;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;

            // Пиксель-в-пиксель на выбранный монитор (или на основной, если снял выбор)
            var monitor = MonitorBox.SelectedItem as MonitorInfo
                          ?? Displays.Enumerate().Find(m => m.Primary);
            if (monitor is not null)
                Displays.ApplyFullscreen(this, monitor);

            SettingsPanel.Visibility = Visibility.Collapsed;
            FullscreenButton.Content = App.T("L.Windowed");
        }
        else
        {
            var monitors = Displays.Enumerate();
            var b = _prevBounds;
            bool sane = b.Right - b.Left > 0 && b.Bottom - b.Top > 0
                        && monitors.Any(m => b.Left < m.Bounds.Right && b.Right > m.Bounds.Left
                                             && b.Top < m.Bounds.Bottom && b.Bottom > m.Bounds.Top);
            if (!sane)
            {
                var pri = monitors.Find(m => m.Primary) ?? monitors.FirstOrDefault();
                if (pri is not null)
                {
                    int w = Math.Min(1200, pri.WidthPx - 80), h = Math.Min(760, pri.HeightPx - 80);
                    b = new RECT
                    {
                        Left = pri.Bounds.Left + (pri.WidthPx - w) / 2,
                        Top = pri.Bounds.Top + (pri.HeightPx - h) / 3,
                        Right = pri.Bounds.Left + (pri.WidthPx - w) / 2 + w,
                        Bottom = pri.Bounds.Top + (pri.HeightPx - h) / 3 + h
                    };
                }
            }
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            Displays.ApplyBoundsPx(this, b);
            // WPF при смене WindowStyle сам пытается вернуть «свои» DIP-координаты —
            // перекрываем его позднейшим повтором физических границ.
            Dispatcher.BeginInvoke(() => Displays.ApplyBoundsPx(this, b),
                DispatcherPriority.Loaded);
            SettingsPanel.Visibility = Visibility.Visible;
            FullscreenButton.Content = App.T("L.Fullscreen");
        }
        UpdateGpuPresentation();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when _fullscreen:
                SetFullscreen(false);
                e.Handled = true;
                break;
            case Key.Tab:
                SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                UpdateGpuPresentation();
                e.Handled = true;
                break;
        }
    }

    private void VisualHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _fullscreen)
            SetFullscreen(false);
    }

    // ---------- слайдеры ----------

    private void AutoNorm_Changed(object sender, RoutedEventArgs e)
    {
        if (_analyzer is not null)
            _analyzer.AutoGain = AutoNormCheck.IsChecked == true;
    }

    private void SensSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SensValue is not null)
            SensValue.Text = $"{e.NewValue:P0}";
        if (_analyzer is not null)
            _analyzer.Sensitivity = e.NewValue;
    }

    private void Threshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_analyzer is null || BassThresholdSlider is null)
            return;

        _analyzer.BassThreshold = BassThresholdSlider.Value;
        _analyzer.MidThreshold = MidThresholdSlider.Value;
        _analyzer.HighThreshold = HighThresholdSlider.Value;

        BassThresholdValue.Text = $"{BassThresholdSlider.Value:P0}";
        MidThresholdValue.Text = $"{MidThresholdSlider.Value:P0}";
        HighThresholdValue.Text = $"{HighThresholdSlider.Value:P0}";
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        CompositionTarget.Rendering -= OnRendering;
        Stop();
    }
}
