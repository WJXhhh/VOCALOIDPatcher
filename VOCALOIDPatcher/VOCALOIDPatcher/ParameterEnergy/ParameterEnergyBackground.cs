using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using UpdateViewTypeFlag = Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag;

namespace VOCALOIDPatcher.ParameterEnergy;

internal sealed class ParameterEnergyBackground
{
    private static readonly ConditionalWeakTable<ParameterView, ParameterEnergyBackground> Instances = new();
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private readonly ParameterView _view;
    private readonly UIElement? _nativeToolTip;
    private readonly UIElement? _nativeCursorGuide;
    private readonly List<EnergyTargetGuide> _targetGuides = new();
    private readonly List<Canvas> _customLayers = new();
    private readonly List<Surface> _surfaces = new();
    private readonly HashSet<nint> _rendering = new();
    private MusicalEditorViewModel? _vm;
    private CancellationTokenSource? _cancellation;
    private EnergyEnvelope? _envelope;
    private string? _path;
    private nint _part;
    private int _generation;
    private bool _pending;
    private DrawingKey? _drawingKey;
    private Drawing? _drawing;
    private int _tempoRevision;
    private int _analysisWeighting = -1;

    private sealed record DrawingKey(EnergyEnvelope Envelope, double Left, double Right, double Height,
        double Origin, double WidthPerTick, double Minimum, double Maximum, double Dpi, int TempoRevision);

    private ParameterEnergyBackground(ParameterView view)
    {
        _view = view;
        _nativeToolTip = AccessTools.Field(typeof(ParameterView), "xToolTip")?.GetValue(view) as UIElement;
        _nativeCursorGuide = AccessTools.Field(typeof(ParameterView), "xMouseCursorGuide")?.GetValue(view) as UIElement;
        if (AccessTools.Field(typeof(ParameterView), "xBaseCanvas")?.GetValue(view) is not Canvas canvas)
            throw new InvalidOperationException("Parameter background canvas is unavailable.");
        // Keep the reference below both audio geometry and editable parameters.
        // A top-level overlay obscures curves and their rightmost edit points.
        canvas.Children.Insert(0, CreateTargetGuide());
        canvas.Children.Add(CreateSurface());
        view.Loaded += (_, _) => Bind();
        view.Unloaded += (_, _) => Unbind();
        view.DataContextChanged += (_, _) => { Unbind(); Bind(); };
        view.SizeChanged += (_, _) => QueueRefresh();
        if (view.IsLoaded) Bind();
    }

    internal static void Attach(ParameterView view)
    {
        try { Instances.GetValue(view, v => new(v)); }
        catch (Exception e) { Debug.Print("Parameter energy attach: " + e.GetType().Name); }
    }

    // BVL/REG have an opaque custom canvas; give that canvas a second surface
    // sharing the same envelope and drawing, below its editable bars.
    internal static FrameworkElement CreateCustomSurface(ParameterView view)
    {
        Attach(view);
        return Instances.TryGetValue(view, out var instance) ? instance.CreateCustomLayer() : new FrameworkElement();
    }

    private EnergyTargetGuide CreateTargetGuide()
    {
        var guide = new EnergyTargetGuide(value =>
        {
            Settings.ParameterEnergyTargetDb = value;
            RefreshAll();
        }, () =>
        {
            if (_nativeToolTip != null) _nativeToolTip.Visibility = Visibility.Hidden;
            if (_nativeCursorGuide != null) _nativeCursorGuide.Visibility = Visibility.Hidden;
        });
        _targetGuides.Add(guide);
        return guide;
    }

    private Canvas CreateCustomLayer()
    {
        var layer = new Canvas { Focusable = false };
        layer.Children.Add(CreateTargetGuide());
        layer.Children.Add(CreateSurface());
        _customLayers.Add(layer);
        return layer;
    }

    private Surface CreateSurface()
    {
        var surface = new Surface { IsHitTestVisible = false, Focusable = false };
        _surfaces.Add(surface);
        return surface;
    }

    internal static void RefreshAll()
    {
        foreach (var entry in Instances) entry.Value.QueueRefresh();
    }

    internal static void InvalidatePart(WIVSMMidiPart part)
    {
        foreach (var entry in Instances)
        {
            var instance = entry.Value;
            if (instance._part != (nint)part) continue;
            instance.CancelAnalysis();
            instance._path = null;
            instance.QueueRefresh();
        }
    }

    private void Bind()
    {
        if (!_view.IsLoaded || _vm != null || _view.DataContext is not MusicalEditorViewModel vm) return;
        _vm = vm;
        vm.UpdateViewEvent += OnUpdate;
        vm.RendererStarted += OnRenderStarted;
        vm.RendererCompleted += OnRenderCompleted;
        vm.RendererCanceled += OnRenderCanceled;
        QueueRefresh();
    }

    private void Unbind()
    {
        if (_vm is { } vm)
        {
            vm.UpdateViewEvent -= OnUpdate;
            vm.RendererStarted -= OnRenderStarted;
            vm.RendererCompleted -= OnRenderCompleted;
            vm.RendererCanceled -= OnRenderCanceled;
        }
        _vm = null;
        foreach (var guide in _targetGuides) guide.Hide();
        _rendering.Clear();
        _part = 0;
        _path = null;
        _envelope = null;
        CancelAnalysis();
        Publish(null);
    }

    private void OnUpdate(object sender, UpdateViewTypeFlag flags, UpdateObserverNotifyEventArgs? observer, object? addition)
        => OnUi(() => ProcessUpdate(flags));

    private void ProcessUpdate(UpdateViewTypeFlag flags)
    {
        if (flags is UpdateViewTypeFlag.TempoSectionInfosChanged or UpdateViewTypeFlag.MeasureOffsetChanged
            or UpdateViewTypeFlag.SequenceChanged)
            _tempoRevision++;
        switch (flags)
        {
            case UpdateViewTypeFlag.SequenceChanged:
                CancelAnalysis();
                _rendering.Clear();
                _part = 0;
                _path = null;
                _envelope = null;
                QueueRefresh();
                break;
            case UpdateViewTypeFlag.ActiveTrackChanged:
            case UpdateViewTypeFlag.ActivePartChanged:
            case UpdateViewTypeFlag.ShowMusicalEditor:
            case UpdateViewTypeFlag.HorizontalZoomed:
            case UpdateViewTypeFlag.VerticalZoomed:
            case UpdateViewTypeFlag.Scrolled:
            case UpdateViewTypeFlag.ModelChanged:
            case UpdateViewTypeFlag.DisplayControlParameterChanged:
            case UpdateViewTypeFlag.ControlParameterAreaSizeChanged:
            case UpdateViewTypeFlag.OpenParameterView:
            case UpdateViewTypeFlag.TempoSectionInfosChanged:
            case UpdateViewTypeFlag.MeasureOffsetChanged:
            case UpdateViewTypeFlag.NoteChanged:
                QueueRefresh();
                break;
        }
    }

    private void OnRenderStarted(object? sender, RendererObserverStartEventArgs e)
        => OnUi(() => RenderStarted((nint)e.MidiPart));

    private void RenderStarted(nint part)
    {
        _rendering.Add(part);
        if (_part == part)
        {
            CancelAnalysis();
            _path = null;
            // Retain the last envelope until the replacement audio is ready.
        }
    }

    private void OnRenderCompleted(object? sender, RendererObserverCompleteEventArgs e)
        => RenderFinished(e.MidiPart);

    private void OnRenderCanceled(object? sender, RendererObserverCancelEventArgs e)
        => RenderFinished(e.MidiPart);

    private void RenderFinished(WIVSMMidiPart part)
        => OnUi(() =>
        {
            _rendering.Remove((nint)part);
            if (_part == (nint)part) { _path = null; QueueRefresh(); }
        });

    private void OnUi(Action action)
    {
        var expected = _vm;
        void Apply()
        {
            if (expected == null || _vm != expected) return;
            try { action(); }
            catch (Exception e) { Debug.Print("Parameter energy notification: " + e.GetType().Name); }
        }
        if (_view.Dispatcher.CheckAccess()) Apply();
        else if (!_view.Dispatcher.HasShutdownStarted) _view.Dispatcher.BeginInvoke((Action)Apply);
    }

    private void QueueRefresh()
    {
        if (!_view.Dispatcher.CheckAccess())
        {
            if (!_view.Dispatcher.HasShutdownStarted)
                _view.Dispatcher.BeginInvoke((Action)QueueRefresh, DispatcherPriority.Background);
            return;
        }
        if (_pending) return;
        _pending = true;
        _view.Dispatcher.BeginInvoke((Action)(() =>
        {
            _pending = false;
            try { Refresh(); }
            catch (Exception e)
            {
                Publish(null);
                Debug.Print("Parameter energy refresh: " + e.GetType().Name);
            }
        }), DispatcherPriority.Background);
    }

    private void Refresh()
    {
        foreach (var guide in _targetGuides)
        {
            if (_view.IsLoaded && _vm?.ParameterViewer is { } targetViewer)
                guide.Update(targetViewer.ViewportRect(), Math.Max(_view.ActualWidth, _vm.SongWidth), _vm.ViewHeight,
                    Settings.ParameterEnergyMinimumDb, Settings.ParameterEnergyMaximumDb, Settings.ParameterEnergyTargetDb,
                    Settings.ParameterEnergy && Settings.ParameterEnergyTargetLine);
            else guide.Hide();
        }
        if (!Settings.ParameterEnergy || !_view.IsLoaded || _vm is not { } vm ||
            vm.ActivePart is not { } part || vm.VSMSequence is not { IsOpen: true } sequence || part.NumNotes == 0)
        {
            CancelAnalysis();
            _path = null;
            _envelope = null;
            Publish(null);
            return;
        }
        int weighting = Settings.ParameterEnergyWeighting;
        if (_part != (nint)part || _analysisWeighting != weighting)
        {
            CancelAnalysis();
            _part = (nint)part;
            _envelope = null;
            _path = null;
            _analysisWeighting = weighting;
        }
        if (!_rendering.Contains(_part) && part.HasValidRenderedWave)
        {
            string path = part.WaveFilePath;
            if (!string.IsNullOrEmpty(path) && !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                CancelAnalysis();
                _path = path;
                _cancellation = new();
                _ = Analyze(path, _generation, weighting, _cancellation.Token);
            }
        }
        if (_envelope is not { Decibels.Length: > 0 } envelope || vm.ParameterViewer is not { } viewer)
        {
            Publish(null);
            return;
        }
        Rect viewport = viewer.ViewportRect();
        double left = Math.Max(0, viewport.Left), right = Math.Min(vm.SongWidth, viewport.Right);
        double height = vm.ViewHeight;
        if (!double.IsFinite(left + right + height) || right <= left || height <= 8) { Publish(null); return; }
        double origin = sequence.GetTimeFromTick(part.AbsBeginTick) - sequence.PresendTimeSec;
        // Only create geometry for the visible viewport, bounded independently
        // of song length. Convert through the tempo map, including preroll.
        var key = new DrawingKey(envelope, left, right, height, origin, vm.WidthPerTick,
            Settings.ParameterEnergyMinimumDb, Settings.ParameterEnergyMaximumDb,
            VisualTreeHelper.GetDpi(_view).PixelsPerDip, _tempoRevision);
        // Note/selection/parameter notifications often leave the envelope and
        // coordinates unchanged. Reuse its frozen geometry across those updates.
        if (_drawingKey != key)
        {
            _drawing = EnergyEnvelopeDrawing.Create(envelope, left, right, height, key.Minimum, key.Maximum,
                x => sequence.GetTimeFromQuarter(vm.GetQuarterFromViewPosition(x)) - origin, key.Dpi);
            _drawingKey = key;
        }
        Publish(_drawing);
    }

    private void CancelAnalysis()
    {
        _generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task Analyze(string path, int generation, int weighting, CancellationToken token)
    {
        try
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            await Worker.WaitAsync(token).ConfigureAwait(false);
            EnergyEnvelope? envelope = null;
            try
            {
                // Rendering can finish before the file is unlocked. Retry only
                // this generation, without touching native objects on a worker.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        envelope = await Task.Run(() =>
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            long length = stream.Length;
                            DateTime written = File.GetLastWriteTimeUtc(path);
                            var result = EnergyEnvelope.Read(stream, token, weighting);
                            if (stream.Length != length || File.GetLastWriteTimeUtc(path) != written)
                                throw new IOException("Energy source changed.");
                            return result;
                        }, token).ConfigureAwait(false);
                        break;
                    }
                    catch (IOException) when (attempt < 5) { await Task.Delay(150 * (attempt + 1), token).ConfigureAwait(false); }
                }
            }
            finally { Worker.Release(); }
            token.ThrowIfCancellationRequested();
            if (_view.Dispatcher.HasShutdownStarted) return;
            await _view.Dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation || token.IsCancellationRequested) return;
                _envelope = envelope;
                QueueRefresh();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Debug.Print("Parameter energy analysis: " + e.GetType().Name); }
    }

    private void Publish(Drawing? drawing)
    {
        if (drawing == null) { _drawingKey = null; _drawing = null; }
        foreach (var layer in _customLayers)
        {
            layer.Width = Math.Max(1, _view.ActualWidth);
            layer.Height = Math.Max(1, _vm?.ViewHeight ?? _view.ActualHeight);
        }
        foreach (var surface in _surfaces)
        {
            surface.Width = Math.Max(1, _view.ActualWidth);
            surface.Height = Math.Max(1, _vm?.ViewHeight ?? _view.ActualHeight);
            surface.Opacity = Settings.ParameterEnergyOpacity / 100d;
            if (!ReferenceEquals(surface.Drawing, drawing))
            {
                surface.Drawing = drawing;
                surface.InvalidateVisual();
            }
        }
    }

    private sealed class Surface : FrameworkElement
    {
        internal Drawing? Drawing;
        protected override void OnRender(DrawingContext drawingContext)
        {
            if (Settings.ParameterEnergy && Drawing != null) drawingContext.DrawDrawing(Drawing);
        }
    }
}
