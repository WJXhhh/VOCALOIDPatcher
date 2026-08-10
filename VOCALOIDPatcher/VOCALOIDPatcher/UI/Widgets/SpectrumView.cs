using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using VOCALOIDPatcher.Utils.Audio;

namespace VOCALOIDPatcher.UI.Widgets;

public sealed class SpectrumView : FrameworkElement
{
    private const int FftSize = 4096;
    private const int Points = 128;
    private const double MinFreq = 40.0;
    private const double MaxFreq = 18000.0;
    private const double MinDb = -82.0;
    private const double MaxDb = -12.0;
    private const long FrameIntervalMs = 16;
    private const double AttackSmoothing = 0.92;
    private const double DecaySmoothing = 0.6;

    private static readonly Pen CurvePen;
    private static readonly Brush FillBrush;
    private static readonly Brush BaselineBrush;

    private readonly SpectrumAudioCapture _capture = new();
    private readonly SimpleFft _fft = new(FftSize);
    private readonly float[] _samples = new float[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2];
    private readonly double[] _levels = new double[Points];
    private readonly double[] _display = new double[Points];
    private readonly double[] _render = new double[Points];
    private readonly object _dataLock = new();

    private bool _enabled;
    private bool _hooked;
    private volatile bool _analyzing;
    private int _analysisVersion;
    private int _renderVersion;
    private Thread? _analysisThread;

    static SpectrumView()
    {
        var accent = Color.FromRgb(0x29, 0xAB, 0xE2);

        CurvePen = new Pen(new SolidColorBrush(accent), 1.3);
        CurvePen.Brush.Freeze();
        CurvePen.Freeze();

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0x29, 0xAB, 0xE2), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0x29, 0xAB, 0xE2), 1));
        fill.Freeze();
        FillBrush = fill;

        BaselineBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        BaselineBrush.Freeze();
    }

    public SpectrumView()
    {
        Width = 150;
        Height = 26;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;

        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Unhook();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Sync();
        }
    }

    private void Sync()
    {
        if (_enabled && IsLoaded)
        {
            if (!_capture.IsRunning) _capture.Start();
            StartAnalysis();
            Hook();
        }
        else
        {
            Unhook();
            StopAnalysis();
            if (_capture.IsRunning) _capture.Stop();
            lock (_dataLock)
            {
                Array.Clear(_levels, 0, _levels.Length);
                Array.Clear(_display, 0, _display.Length);
            }
            InvalidateVisual();
        }
    }

    private void StartAnalysis()
    {
        if (_analyzing) return;
        _analyzing = true;
        _analysisThread = new Thread(AnalysisLoop)
        {
            IsBackground = true,
            Name = "VOCALOIDPatcher.SpectrumAnalysis",
            Priority = ThreadPriority.BelowNormal
        };
        _analysisThread.Start();
    }

    private void StopAnalysis()
    {
        _analyzing = false;
        var thread = _analysisThread;
        _analysisThread = null;
        if (thread != null && thread != Thread.CurrentThread && thread.IsAlive)
            thread.Join(100);
    }

    private void AnalysisLoop()
    {
        while (_analyzing)
        {
            try
            {
                Analyze();
            }
            catch
            {
                /* ignore */
            }

            Thread.Sleep((int)FrameIntervalMs);
        }
    }

    private void Hook()
    {
        if (_hooked) return;
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var version = Volatile.Read(ref _analysisVersion);
        if (version == _renderVersion) return;

        _renderVersion = version;
        InvalidateVisual();
    }

    private void Analyze()
    {
        _capture.ReadLatest(_samples);
        _fft.MagnitudeSpectrum(_samples, _magnitudes);

        var sampleRate = _capture.SampleRate;
        var binHz = sampleRate / (double)FftSize;
        var bins = _magnitudes.Length;
        var highestFrequency = Math.Min(MaxFreq, sampleRate * 0.5 - binHz);
        if (highestFrequency <= MinFreq) return;

        var ratio = highestFrequency / MinFreq;
        var norm = _fft.AmplitudeScale;

        for (var i = 0; i < Points; i++)
        {
            var loPos = MinFreq * Math.Pow(ratio, i / (double)Points) / binHz;
            var hiPos = MinFreq * Math.Pow(ratio, (i + 1.0) / Points) / binHz;

            double mag;
            if (hiPos - loPos >= 2.0)
            {
                var lo = (int)loPos;
                var hi = (int)hiPos;
                if (lo < 1) lo = 1;
                if (hi > bins) hi = bins;

                var peak = 0f;
                for (var b = lo; b < hi; b++)
                    if (_magnitudes[b] > peak)
                        peak = _magnitudes[b];
                mag = peak;
            }
            else
            {
                var pos = (loPos + hiPos) * 0.5;
                var lo = (int)pos;
                if (lo < 1) lo = 1;
                var hi = lo + 1;
                if (hi >= bins) hi = bins - 1;
                var frac = pos - lo;
                mag = _magnitudes[lo] * (1 - frac) + _magnitudes[hi] * frac;
            }

            var db = 20.0 * Math.Log10(mag * norm + 1e-9);
            var level = (db - MinDb) / (MaxDb - MinDb);
            if (!(level > 0.0)) level = 0.0;
            else if (level > 1.0) level = 1.0;

            var smoothing = level > _levels[i] ? AttackSmoothing : DecaySmoothing;
            _levels[i] += (level - _levels[i]) * smoothing;
        }

        lock (_dataLock)
            Array.Copy(_levels, _display, Points);
        Interlocked.Increment(ref _analysisVersion);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(BaselineBrush, null, new Rect(0, h - 1, w, 1));

        if (!_enabled) return;

        var display = _render;
        lock (_dataLock)
            Array.Copy(_display, display, Points);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);

            for (var i = 0; i < Points; i++)
            {
                var x = i / (double)(Points - 1) * w;
                var y = h - display[i] * (h - 1);
                ctx.LineTo(new Point(x, y), true, false);
            }

            ctx.LineTo(new Point(w, h), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(FillBrush, null, geometry);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(0, h - display[0] * (h - 1)), false, false);
            for (var i = 1; i < Points; i++)
            {
                var x = i / (double)(Points - 1) * w;
                var y = h - display[i] * (h - 1);
                ctx.LineTo(new Point(x, y), true, true);
            }
        }

        line.Freeze();
        dc.DrawGeometry(null, CurvePen, line);
    }
}
