using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

/// <summary>
/// Keeps the last successfully rendered waveform in the FastCanvas background.
/// FastCanvas.ClearElement only clears its child lists, so the retained image is
/// independent from renderer buffers and virtualization.
/// </summary>
public class WaveformSnapshotPatch : PatchBase
{
    public override string PatchName        => "WaveformSnapshotPatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "DrawRenderedWaveCanvas";
    public override Type[] ArgumentTypes => new[] { typeof(MusicalEditorViewModel) };

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(PianorollView __instance, MusicalEditorViewModel vm)
    {
        WaveformSnapshot.BeforeRedraw(__instance, vm);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(PianorollView __instance, MusicalEditorViewModel vm)
    {
        WaveformSnapshot.AfterRedraw(__instance, vm);
    }
}

public class WaveformSnapshotRenderStartedPatch : PatchBase
{
    public override string PatchName        => "WaveformSnapshotRenderStartedPatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "OnRendererStarted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverStartEventArgs) };

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(PianorollView __instance, RendererObserverStartEventArgs e)
    {
        WaveformSnapshot.RendererStarted(__instance, e);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(PianorollView __instance, RendererObserverStartEventArgs e)
    {
        WaveformSnapshot.RendererStartedRedrawCompleted(__instance, e);
    }
}

public class WaveformSnapshotRenderCompletedPatch : PatchBase
{
    public override string PatchName        => "WaveformSnapshotRenderCompletedPatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "OnRendererCompleted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCompleteEventArgs) };

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(PianorollView __instance, RendererObserverCompleteEventArgs e)
    {
        WaveformSnapshot.RendererCompleting(__instance, e);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(PianorollView __instance, RendererObserverCompleteEventArgs e)
    {
        WaveformSnapshot.RendererCompleted(__instance, e);
    }
}

public class WaveformSnapshotRenderCanceledPatch : PatchBase
{
    public override string PatchName        => "WaveformSnapshotRenderCanceledPatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "OnRendererCanceled";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCancelEventArgs) };

    [HarmonyPostfix]
    private static void Postfix(PianorollView __instance, RendererObserverCancelEventArgs e)
    {
        WaveformSnapshot.RendererCanceled(__instance, e);
    }
}

public class WaveformSnapshotZoomPatch : PatchBase
{
    public override string PatchName        => "WaveformSnapshotZoomPatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "UpdateHorizontalOrVerticalZoomed";
    public override Type[] ArgumentTypes => new[] { typeof(MusicalEditorViewModel) };

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(PianorollView __instance, MusicalEditorViewModel vm)
    {
        WaveformSnapshot.ZoomRedrawStarted(__instance, vm);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(PianorollView __instance, MusicalEditorViewModel vm)
    {
        WaveformSnapshot.ZoomRedrawCompleted(__instance, vm);
    }
}

internal static class WaveformSnapshot
{
    private static readonly Pen RenderBoundaryPen = CreateRenderBoundaryPen();

    private sealed class SnapshotData
    {
        public SnapshotData(DrawingGroup? drawing, Rect mapping)
        {
            Drawing = drawing;
            Mapping = mapping;
            Brush = drawing == null || drawing.Bounds.IsEmpty
                ? null
                : CreateBrush(drawing, mapping);
        }

        public DrawingGroup? Drawing { get; }
        public Rect Mapping { get; }
        public DrawingBrush? Brush { get; }
    }

    private readonly struct RenderProgress
    {
        public RenderProgress(double firstEnd, double secondBegin, double secondEnd, bool blockRenderingEnabled)
        {
            FirstEnd = firstEnd;
            SecondBegin = secondBegin;
            SecondEnd = secondEnd;
            BlockRenderingEnabled = blockRenderingEnabled;
        }

        public double FirstEnd { get; }
        public double SecondBegin { get; }
        public double SecondEnd { get; }
        public bool BlockRenderingEnabled { get; }

        public static RenderProgress From(VSMRendererProgress progress)
        {
            return new RenderProgress(
                Math.Clamp(progress.FirstEnd, 0, 100),
                Math.Clamp(progress.SecondBegin, 0, 100),
                Math.Clamp(progress.SecondEnd, 0, 100),
                progress.BlockRenderingEnabled);
        }
    }

    private readonly struct HorizontalRange
    {
        public HorizontalRange(double left, double right)
        {
            Left = left;
            Right = right;
        }

        public double Left { get; }
        public double Right { get; }
    }

    private sealed class ViewState
    {
        public WIVSMMidiPart? Part;
        public SnapshotData? StableSnapshot;
        public SnapshotData? DisplayedSnapshot;
        public SnapshotData? CompletionFallback;
        public Brush? OriginalBackground;
        public bool OriginalBackgroundCaptured;
        public bool Rendering;
        public bool CompletionPending;
        public RenderProgress DisplayProgress;
        public RenderProgress PendingProgress;
        public bool HasPendingProgress;
        public bool ZoomRedraw;
        public int BackgroundHideRequest;
        public bool EmptyPart;
    }

    private sealed class ViewReference
    {
        public WeakReference<PianorollView>? View;
    }

    private static readonly ConditionalWeakTable<PianorollView, ViewState> States = new();
    private static readonly ConditionalWeakTable<MusicalEditorViewModel, ViewReference> Views = new();

    private static readonly AccessTools.FieldRef<PianorollView, FastCanvas>? WaveCanvas =
        CreateWaveCanvasRef();

    public static void BeforeRedraw(PianorollView view, MusicalEditorViewModel? vm)
    {
        try
        {
            if (WaveCanvas == null)
                return;

            var state = States.GetOrCreateValue(view);
            var canvas = WaveCanvas(view);
            CaptureOriginalBackground(state, canvas);
            if (vm != null)
                RememberView(view, vm);

            if (!IsEnabled(vm))
            {
                Reset(state, canvas);
                return;
            }

            var part = vm!.ActivePart!;
            if (state.Part != null && !state.Part.Equals(part))
            {
                Reset(state, canvas);
                state.Part = part;
                return;
            }

            state.Part = part;
            if (part.NumNotes == 0)
            {
                ClearEmptyPart(state, canvas, vm, part);
                return;
            }

            state.EmptyPart = false;
            if (!state.Rendering && !state.CompletionPending)
            {
                var current = CaptureSnapshot(canvas, vm);
                if (current?.Drawing != null || state.StableSnapshot == null)
                    state.StableSnapshot = current;
            }

            if (state.ZoomRedraw && !state.Rendering && !state.CompletionPending)
            {
                state.DisplayedSnapshot = null;
                canvas.Background = state.OriginalBackground;
                return;
            }

            ApplyCurrentBackground(state, canvas, vm);
        }
        catch
        {
        }
    }

    public static void AfterRedraw(PianorollView view, MusicalEditorViewModel? vm)
    {
        try
        {
            if (WaveCanvas == null || !IsEnabled(vm))
                return;

            var state = States.GetOrCreateValue(view);
            var canvas = WaveCanvas(view);
            CaptureOriginalBackground(state, canvas);
            RememberView(view, vm!);

            var part = vm!.ActivePart!;
            if (state.Part != null && !state.Part.Equals(part))
            {
                Reset(state, canvas);
                state.Part = part;
            }
            else if (state.Part == null)
            {
                state.Part = part;
            }

            if (part.NumNotes == 0)
            {
                ClearEmptyPart(state, canvas, vm, part);
                return;
            }

            state.EmptyPart = false;

            if (state.CompletionPending)
            {
                TryFinalizeCompletedWaveform(view, state, canvas, vm);
                return;
            }

            if (!state.Rendering)
            {
                var current = CaptureSnapshot(canvas, vm);
                if (current != null)
                    state.StableSnapshot = current;

                if (state.ZoomRedraw)
                {
                    state.DisplayedSnapshot = null;
                    canvas.Background = state.OriginalBackground;
                    return;
                }

                ApplyCurrentBackground(state, canvas, vm);
                ScheduleStableBackgroundHide(view, state, canvas);
            }
        }
        catch
        {
        }
    }

    public static void RendererStarted(PianorollView view, RendererObserverStartEventArgs? e)
    {
        try
        {
            if (WaveCanvas == null || e?.MidiPart == null
                || view.DataContext is not MusicalEditorViewModel vm
                || !IsEnabled(vm) || !vm.ActivePart!.Equals(e.MidiPart))
                return;

            var state = States.GetOrCreateValue(view);
            var canvas = WaveCanvas(view);
            CaptureOriginalBackground(state, canvas);
            RememberView(view, vm);
            SynchronizePart(state, canvas, e.MidiPart);
            var current = CaptureSnapshot(canvas, vm);
            if (current?.Drawing != null || state.StableSnapshot == null)
                state.StableSnapshot = current;

            state.Rendering = true;
            state.CompletionPending = false;
            state.CompletionFallback = null;
            state.DisplayProgress = default;
            state.PendingProgress = default;
            state.HasPendingProgress = false;
            ApplyCurrentBackground(state, canvas, vm);

        }
        catch
        {
        }
    }

    public static void RendererStartedRedrawCompleted(
        PianorollView view,
        RendererObserverStartEventArgs? e)
    {
        try
        {
            if (WaveCanvas == null || e?.MidiPart == null
                || view.DataContext is not MusicalEditorViewModel vm
                || !IsEnabled(vm) || !vm.ActivePart!.Equals(e.MidiPart))
                return;

            var state = States.GetOrCreateValue(view);
            var canvas = WaveCanvas(view);
            if (state.Rendering && !state.CompletionPending && HasMountedWave(canvas))
            {
                state.DisplayedSnapshot = null;
                canvas.Background = state.OriginalBackground;
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Called only for block events that will actually redraw the pianoroll. The
    /// renderer preview throttle deliberately does not call this for skipped events.
    /// </summary>
    public static void RendererBlockRendered(
        PianorollView view,
        RendererObserverBlockRenderingEventArgs? e)
    {
        try
        {
            if (WaveCanvas == null || e?.MidiPart == null
                || view.DataContext is not MusicalEditorViewModel vm
                || !IsEnabled(vm) || !vm.ActivePart!.Equals(e.MidiPart))
                return;

            var state = States.GetOrCreateValue(view);
            var canvas = WaveCanvas(view);
            CaptureOriginalBackground(state, canvas);
            RememberView(view, vm);
            SynchronizePart(state, canvas, e.MidiPart);
            if (!state.Rendering)
            {
                state.StableSnapshot ??= CaptureSnapshot(canvas, vm);
                state.Rendering = true;
            }

            // The audio buffer is installed before this event, but the new
            // UIRenderedWave shape has not been drawn yet. Keep the reported range
            // pending until that visual actually completes OnRender.
            state.PendingProgress = RenderProgress.From(e.Progress);
            state.HasPendingProgress = true;
            ApplyCurrentBackground(state, canvas, vm);
        }
        catch
        {
        }
    }

    public static void RendererCompleting(PianorollView view, RendererObserverCompleteEventArgs? e)
    {
        try
        {
            if (WaveCanvas == null || e?.MidiPart == null
                || view.DataContext is not MusicalEditorViewModel vm
                || !IsEnabled(vm) || !vm.ActivePart!.Equals(e.MidiPart))
                return;

            var state = States.GetOrCreateValue(view);
            var canvas = WaveCanvas(view);
            CaptureOriginalBackground(state, canvas);
            SynchronizePart(state, canvas, e.MidiPart);
            state.PendingProgress = default;
            state.HasPendingProgress = false;

            // Preserve exactly what was visible at the end of block rendering.
            // If the final wave file is still locked, this composite remains until
            // RenderedWaveCachePatch successfully loads it and requests a redraw.
            var visibleBackground = state.StableSnapshot == null
                ? null
                : CreateTransitionSnapshot(
                    state.StableSnapshot,
                    vm,
                    state.DisplayProgress,
                    showBoundaries: false);
            state.DisplayedSnapshot = visibleBackground;
            canvas.Background = visibleBackground?.Brush ?? state.OriginalBackground;
            state.CompletionFallback = CaptureComposite(canvas, vm, visibleBackground)
                ?? visibleBackground
                ?? state.StableSnapshot;
            state.Rendering = true;
            state.CompletionPending = true;
            ApplyCurrentBackground(state, canvas, vm);
        }
        catch
        {
        }
    }

    public static void RendererCompleted(PianorollView view, RendererObserverCompleteEventArgs? e)
    {
        try
        {
            if (WaveCanvas == null || e?.MidiPart == null
                || view.DataContext is not MusicalEditorViewModel vm
                || !IsEnabled(vm) || !vm.ActivePart!.Equals(e.MidiPart))
                return;

            var state = States.GetOrCreateValue(view);
            TryFinalizeCompletedWaveform(view, state, WaveCanvas(view), vm);
        }
        catch
        {
        }
    }

    public static void RendererCanceled(PianorollView view, RendererObserverCancelEventArgs? e)
    {
        try
        {
            if (WaveCanvas == null || e?.MidiPart == null
                || view.DataContext is not MusicalEditorViewModel vm
                || vm.ActivePart == null || !vm.ActivePart.Equals(e.MidiPart))
                return;

            var state = States.GetOrCreateValue(view);
            var canvas = WaveCanvas(view);
            state.Rendering = false;
            state.CompletionPending = false;
            state.CompletionFallback = null;
            state.DisplayProgress = default;
            state.PendingProgress = default;
            state.HasPendingProgress = false;
            ApplyCurrentBackground(state, canvas, vm);
        }
        catch
        {
        }
    }

    public static void WaveformDrawingCompleted(UIRenderedWave wave)
    {
        try
        {
            if (WaveCanvas == null || wave.MusicalEditorVM is not { } vm
                || !Views.TryGetValue(vm, out var viewReference)
                || viewReference.View == null
                || !viewReference.View.TryGetTarget(out var view)
                || !ReferenceEquals(view.DataContext, vm)
                || !IsEnabled(vm))
                return;

            var state = States.GetOrCreateValue(view);
            if (!state.Rendering || state.CompletionPending || !state.HasPendingProgress
                || state.Part == null || !state.Part.Equals(vm.ActivePart))
                return;

            var canvas = WaveCanvas(view);
            if (!canvas.Children.Contains(wave))
                return;

            state.DisplayProgress = state.PendingProgress;
            state.PendingProgress = default;
            state.HasPendingProgress = false;
            ApplyCurrentBackground(state, canvas, vm);
        }
        catch
        {
        }
    }

    public static void ZoomRedrawStarted(PianorollView view, MusicalEditorViewModel? vm)
    {
        try
        {
            if (WaveCanvas == null || !IsEnabled(vm))
                return;

            var state = States.GetOrCreateValue(view);
            if (state.Rendering || state.CompletionPending)
                return;

            var canvas = WaveCanvas(view);
            CaptureOriginalBackground(state, canvas);
            SynchronizePart(state, canvas, vm!.ActivePart!);
            state.ZoomRedraw = true;
            state.BackgroundHideRequest++;
            state.DisplayedSnapshot = null;
            canvas.Background = state.OriginalBackground;
        }
        catch
        {
        }
    }

    public static void ZoomRedrawCompleted(PianorollView view, MusicalEditorViewModel? vm)
    {
        try
        {
            if (WaveCanvas == null || !IsEnabled(vm))
                return;

            var state = States.GetOrCreateValue(view);
            if (!state.ZoomRedraw)
                return;

            state.ZoomRedraw = false;
            state.DisplayedSnapshot = null;
            WaveCanvas(view).Background = state.OriginalBackground;
        }
        catch
        {
        }
    }

    private static void TryFinalizeCompletedWaveform(
        PianorollView view,
        ViewState state,
        FastCanvas canvas,
        MusicalEditorViewModel vm)
    {
        if (!state.CompletionPending)
            return;

        var replacement = CaptureSnapshot(canvas, vm);
        if (replacement == null)
        {
            ApplyCurrentBackground(state, canvas, vm);
            return;
        }

        state.StableSnapshot = replacement;
        state.DisplayedSnapshot = replacement;
        state.CompletionFallback = null;
        state.Rendering = false;
        state.CompletionPending = false;
        state.DisplayProgress = default;
        state.PendingProgress = default;
        state.HasPendingProgress = false;
        canvas.Background = replacement.Brush ?? state.OriginalBackground;
        ScheduleStableBackgroundHide(view, state, canvas);
    }

    private static void ScheduleStableBackgroundHide(
        PianorollView view,
        ViewState state,
        FastCanvas canvas)
    {
        int request = ++state.BackgroundHideRequest;
        view.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (request != state.BackgroundHideRequest || state.Rendering
                    || state.CompletionPending || state.ZoomRedraw || !HasMountedWave(canvas))
                    return;

                state.DisplayedSnapshot = null;
                canvas.Background = state.OriginalBackground;
            }
            catch
            {
            }
        }), DispatcherPriority.Render);
    }

    private static bool HasMountedWave(FastCanvas canvas)
    {
        foreach (UIElement child in canvas.Children)
        {
            if (child is UIRenderedWave)
                return true;
        }

        return false;
    }

    private static void ApplyCurrentBackground(
        ViewState state,
        FastCanvas canvas,
        MusicalEditorViewModel vm)
    {
        SnapshotData? display;
        if (state.CompletionPending)
        {
            display = state.CompletionFallback ?? state.StableSnapshot;
        }
        else if (state.Rendering && state.StableSnapshot != null)
        {
            display = CreateTransitionSnapshot(
                state.StableSnapshot,
                vm,
                state.DisplayProgress,
                showBoundaries: true);
        }
        else
        {
            display = state.StableSnapshot;
        }

        state.DisplayedSnapshot = display;
        canvas.Background = display?.Brush ?? state.OriginalBackground;
    }

    private static SnapshotData? CaptureSnapshot(FastCanvas canvas, MusicalEditorViewModel vm)
    {
        try
        {
            bool sampleReady = false;
            if (vm.ActivePart != null)
            {
                try
                {
                    sampleReady = vm.GetSampleEnumerator(vm.ActivePart) != null;
                }
                catch
                {
                }
            }

            var capture = CaptureWaveDrawing(canvas, vm);
            if (!sampleReady && capture.Drawing.Children.Count == 0)
                return null;

            DrawingGroup? drawing = capture.Drawing.Children.Count == 0
                ? null
                : capture.Drawing;
            return new SnapshotData(drawing, capture.Mapping);
        }
        catch
        {
            return null;
        }
    }

    private static SnapshotData? CaptureComposite(
        FastCanvas canvas,
        MusicalEditorViewModel vm,
        SnapshotData? background)
    {
        try
        {
            var capture = CaptureWaveDrawing(canvas, vm);
            if (background?.Drawing == null && capture.Drawing.Children.Count == 0)
                return null;

            var root = new DrawingGroup();
            if (background?.Drawing != null)
                root.Children.Add(background.Drawing);
            if (capture.Drawing.Children.Count > 0)
                root.Children.Add(capture.Drawing);
            if (root.CanFreeze)
                root.Freeze();

            double width = Math.Max(background?.Mapping.Width ?? 0.0, capture.Mapping.Width);
            double height = Math.Max(background?.Mapping.Height ?? 0.0, capture.Mapping.Height);
            if (width <= 0.0 || height <= 0.0)
                return null;

            return new SnapshotData(root, new Rect(0.0, 0.0, width, height));
        }
        catch
        {
            return null;
        }
    }

    private static (DrawingGroup Drawing, Rect Mapping) CaptureWaveDrawing(
        FastCanvas canvas,
        MusicalEditorViewModel vm)
    {
        double viewportLeft = vm.PianorollViewer?.HorizontalOffset ?? 0.0;
        double viewportWidth = Math.Max(1.0, vm.PianorollViewer?.ViewportWidth ?? 1.0);
        double captureLeft = Math.Max(0.0, viewportLeft - 512.0);
        double captureRight = viewportLeft + viewportWidth + 512.0;
        double waveRight = 0.0;
        var root = new DrawingGroup();

        foreach (var child in canvas.VirtualChildren)
        {
            if (child is not UIRenderedWave wave)
                continue;

            double left = Canvas.GetLeft(wave);
            double right = Canvas.GetRight(wave);
            if (!double.IsFinite(left) || !double.IsFinite(right))
                continue;

            waveRight = Math.Max(waveRight, right);
            if (right < captureLeft || left > captureRight)
                continue;

            var drawing = WaveformRenderPatch.CaptureDrawing(wave);
            if (drawing == null || drawing.Bounds.IsEmpty)
                continue;

            var translated = new DrawingGroup
            {
                Transform = new TranslateTransform(left, 0.0)
            };
            translated.Children.Add(drawing);
            if (translated.CanFreeze)
                translated.Freeze();
            root.Children.Add(translated);
        }

        if (root.CanFreeze)
            root.Freeze();

        double width = Math.Max(Math.Max(canvas.ActualWidth, waveRight), captureRight);
        double height = Math.Max(canvas.ActualHeight, root.Bounds.Bottom);
        if (!double.IsFinite(width) || width <= 0.0)
            width = captureRight;
        if (!double.IsFinite(height) || height <= 0.0)
            height = Math.Max(canvas.ActualHeight, 1.0);

        return (root, new Rect(0.0, 0.0, width, height));
    }

    private static SnapshotData CreateTransitionSnapshot(
        SnapshotData stable,
        MusicalEditorViewModel vm,
        RenderProgress progress,
        bool showBoundaries)
    {
        if (stable.Drawing == null || vm.ActivePart == null)
            return stable;

        var ranges = GetRenderedRanges(stable.Mapping, vm, vm.ActivePart, progress);
        if (ranges.Count == 0)
            return stable;

        var root = new DrawingGroup();
        var remainingClip = CreateRemainingGeometry(stable.Mapping, ranges);
        if (remainingClip != null)
        {
            var remaining = new DrawingGroup { ClipGeometry = remainingClip };
            remaining.Children.Add(stable.Drawing);
            if (remaining.CanFreeze)
                remaining.Freeze();
            root.Children.Add(remaining);
        }

        if (showBoundaries)
        {
            Rect waveformBounds = stable.Drawing.Bounds;
            double markerTop = Math.Max(stable.Mapping.Top, waveformBounds.Top);
            double markerBottom = Math.Min(stable.Mapping.Bottom, waveformBounds.Bottom);
            double partRight = vm.CalcTickToViewPosition(vm.ActivePart.AbsEndTick);
            if (double.IsFinite(markerTop) && double.IsFinite(markerBottom)
                && markerBottom - markerTop > 0.5)
            {
                foreach (var range in ranges)
                {
                    if (range.Right >= partRight - 0.5)
                        continue;

                    root.Children.Add(new GeometryDrawing(
                        null,
                        RenderBoundaryPen,
                        new LineGeometry(
                            new Point(range.Right, markerTop),
                            new Point(range.Right, markerBottom))));
                }
            }
        }

        if (root.CanFreeze)
            root.Freeze();
        return new SnapshotData(root, stable.Mapping);
    }

    private static List<HorizontalRange> GetRenderedRanges(
        Rect mapping,
        MusicalEditorViewModel vm,
        WIVSMMidiPart part,
        RenderProgress progress)
    {
        double partLeft = vm.CalcTickToViewPosition(part.AbsBeginTick);
        double partRight = vm.CalcTickToViewPosition(part.AbsEndTick);
        if (!double.IsFinite(partLeft) || !double.IsFinite(partRight) || partRight <= partLeft)
            return new List<HorizontalRange>();

        var ranges = new List<HorizontalRange>(2);
        if (!progress.BlockRenderingEnabled)
            return ranges;

        AddPercentRange(ranges, partLeft, partRight, 0.0, progress.FirstEnd);
        AddPercentRange(ranges, partLeft, partRight, progress.SecondBegin, progress.SecondEnd);

        if (ranges.Count == 0)
            return ranges;

        ranges.Sort((left, right) => left.Left.CompareTo(right.Left));
        var merged = new List<HorizontalRange>(ranges.Count);
        foreach (var range in ranges)
        {
            double left = Math.Clamp(range.Left, mapping.Left, mapping.Right);
            double right = Math.Clamp(range.Right, mapping.Left, mapping.Right);
            if (right <= left)
                continue;

            if (merged.Count > 0 && left <= merged[^1].Right)
            {
                var previous = merged[^1];
                merged[^1] = new HorizontalRange(previous.Left, Math.Max(previous.Right, right));
            }
            else
            {
                merged.Add(new HorizontalRange(left, right));
            }
        }

        return merged;
    }

    private static void AddPercentRange(
        List<HorizontalRange> ranges,
        double partLeft,
        double partRight,
        double beginPercent,
        double endPercent)
    {
        beginPercent = Math.Clamp(beginPercent, 0.0, 100.0);
        endPercent = Math.Clamp(endPercent, 0.0, 100.0);
        if (endPercent <= beginPercent)
            return;

        double width = partRight - partLeft;
        ranges.Add(new HorizontalRange(
            partLeft + width * beginPercent / 100.0,
            partLeft + width * endPercent / 100.0));
    }

    private static Geometry? CreateRemainingGeometry(
        Rect mapping,
        List<HorizontalRange> renderedRanges)
    {
        var geometry = new GeometryGroup();
        double cursor = mapping.Left;
        foreach (var range in renderedRanges)
        {
            if (range.Left > cursor)
            {
                geometry.Children.Add(CreateFrozenRectangle(
                    new Rect(cursor, mapping.Top, range.Left - cursor, mapping.Height)));
            }

            cursor = Math.Max(cursor, range.Right);
        }

        if (cursor < mapping.Right)
        {
            geometry.Children.Add(CreateFrozenRectangle(
                new Rect(cursor, mapping.Top, mapping.Right - cursor, mapping.Height)));
        }

        if (geometry.Children.Count == 0)
            return null;
        if (geometry.CanFreeze)
            geometry.Freeze();
        return geometry;
    }

    private static RectangleGeometry CreateFrozenRectangle(Rect rect)
    {
        var geometry = new RectangleGeometry(rect);
        if (geometry.CanFreeze)
            geometry.Freeze();
        return geometry;
    }

    private static Pen CreateRenderBoundaryPen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(192, 135, 206, 250));
        if (brush.CanFreeze)
            brush.Freeze();

        var pen = new Pen(brush, 1.0);
        if (pen.CanFreeze)
            pen.Freeze();
        return pen;
    }

    private static DrawingBrush CreateBrush(Drawing drawing, Rect mapping)
    {
        var brush = new DrawingBrush(drawing)
        {
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Stretch = Stretch.None,
            TileMode = TileMode.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = mapping,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = mapping
        };
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }

    private static bool IsEnabled(MusicalEditorViewModel? vm)
    {
        return Settings.SvEditorStyle && Settings.AlwaysShowWaveform && vm?.ActivePart != null;
    }

    private static void RememberView(PianorollView view, MusicalEditorViewModel vm)
    {
        Views.GetOrCreateValue(vm).View = new WeakReference<PianorollView>(view);
    }

    private static void SynchronizePart(ViewState state, FastCanvas canvas, WIVSMMidiPart part)
    {
        if (state.Part != null && !state.Part.Equals(part))
            Reset(state, canvas);
        state.Part = part;
    }

    private static void CaptureOriginalBackground(ViewState state, FastCanvas canvas)
    {
        if (state.OriginalBackgroundCaptured)
            return;

        state.OriginalBackground = canvas.Background;
        state.OriginalBackgroundCaptured = true;
    }

    private static void ClearEmptyPart(
        ViewState state,
        FastCanvas canvas,
        MusicalEditorViewModel vm,
        WIVSMMidiPart part)
    {
        bool invalidateCache = !state.EmptyPart || state.Part == null || !state.Part.Equals(part);
        Reset(state, canvas);
        state.Part = part;
        state.EmptyPart = true;
        canvas.ClearElement();
        WaveformSvState.Clear();

        if (invalidateCache)
            RenderedWaveCachePatch.InvalidatePart(vm, part);
    }

    private static void Reset(ViewState state, FastCanvas canvas)
    {
        if (state.OriginalBackgroundCaptured)
            canvas.Background = state.OriginalBackground;

        state.Part = null;
        state.StableSnapshot = null;
        state.DisplayedSnapshot = null;
        state.CompletionFallback = null;
        state.Rendering = false;
        state.CompletionPending = false;
        state.DisplayProgress = default;
        state.PendingProgress = default;
        state.HasPendingProgress = false;
        state.ZoomRedraw = false;
        state.BackgroundHideRequest++;
        state.EmptyPart = false;
    }

    private static AccessTools.FieldRef<PianorollView, FastCanvas>? CreateWaveCanvasRef()
    {
        try
        {
            return AccessTools.FieldRefAccess<PianorollView, FastCanvas>("xRenderedWaveCanvas");
        }
        catch
        {
            return null;
        }
    }
}
