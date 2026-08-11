using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.Design.UI;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public class AlwaysShowWaveformPatch : PatchBase
{
    public override string PatchName        => "AlwaysShowWaveformPatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "DrawRenderedWaveCanvas";

    public override Type[] ArgumentTypes => new[] { typeof(MusicalEditorViewModel) };

    private static readonly FieldInfo? WaveCanvasField =
        AccessTools.Field(typeof(PianorollView), "xRenderedWaveCanvas");

    private static readonly MethodInfo? InsertMethod =
        AccessTools.Method(typeof(PianorollView), "InsertRenderedWave", new[] { typeof(MusicalEditorViewModel) });

    [HarmonyPrefix]
    private static bool Prefix(PianorollView __instance, MusicalEditorViewModel vm)
    {
        if (!Settings.AlwaysShowWaveform)
            return true;

        if (Settings.SvEditorStyle && vm?.ActivePart?.NumNotes > 0)
            PrecomputeBaselines(vm);
        else if (Settings.SvEditorStyle)
            WaveformSvState.Clear();

        try
        {
            if (vm == null || WaveCanvasField == null || InsertMethod == null)
                return true;

            if (WaveCanvasField.GetValue(__instance) is not FastCanvas canvas)
                return true;

            canvas.ClearElement();
            if (vm.ActivePart?.NumNotes == 0)
                return false;

            InsertMethod.Invoke(__instance, new object[] { vm });
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void PrecomputeBaselines(MusicalEditorViewModel vm)
    {
        try
        {
            if (vm == null)
            {
                WaveformSvState.Clear();
                return;
            }

            var part = vm.ActivePart;
            var seq = vm.VSMSequence;
            if (part == null || seq == null)
            {
                WaveformSvState.Clear();
                return;
            }

            var samples = vm.GetSampleEnumerator(part);
            if (samples == null)
            {
                WaveformSvState.Clear();
                return;
            }

            long samplesPerFrame = seq.NumSampleInFrame;
            if (samplesPerFrame <= 0)
            {
                WaveformSvState.Clear();
                return;
            }

            long frameCount = samples.NumSamples / samplesPerFrame;
            if (WaveformSvState.IsCached(part, frameCount))
            {
                WaveformSvState.EnsurePhonemeSpans(vm, part);
                return;
            }

            var scores = vm.GetScoreEnumerator(part);
            if (scores == null)
            {
                WaveformSvState.Clear();
                return;
            }

            WaveformSvState.Clear();
            WaveformSvState.Precompute(scores, frameCount);
            WaveformSvState.SetCacheKey(part, frameCount);
            WaveformSvState.EnsurePhonemeSpans(vm, part);
        }
        catch
        {
            WaveformSvState.Clear();
        }
    }

    public static void RefreshWaveform() => ShowOtherTracksNotesPatch.RequestRefreshPianoroll();
}

public class WaveformRenderPatch : PatchBase
{
    public override string PatchName        => "WaveformRenderPatch";
    public override Type   TargetClass      => typeof(UIRenderedWave);
    public override string TargetMethodName => "OnRender";

    public override Type[] ArgumentTypes => new[] { typeof(DrawingContext) };

    private const double FadeWidth = 24.0;

    private static readonly Brush PhonemeTextBrush = Brushes.White;
    private static readonly Brush PhonemeBoundaryBrush = Brushes.LightSkyBlue;
    private static readonly Pen PhonemeBoundaryPen = CreateFrozenPen(PhonemeBoundaryBrush, 0.5);

    [HarmonyPrefix]
    private static bool Prefix(UIRenderedWave __instance, DrawingContext drawingContext, out int __state)
    {
        __state = 0;
        if (!Settings.AlwaysShowWaveform || drawingContext == null)
            return true;

        if (Settings.SvEditorStyle && WaveformSvState.HasBaselines)
        {
            try
            {
                if (CustomRender(__instance, drawingContext))
                    return false;
            }
            catch
            {
            }
        }

        if (Settings.SvEditorStyle)
            WaveformSvState.Activate();

        double opacity = Settings.WaveformOpacity;
        if (opacity < 1.0)
        {
            drawingContext.PushOpacity(opacity);
            __state = 1;
        }
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(UIRenderedWave __instance)
    {
        WaveformSnapshot.WaveformDrawingCompleted(__instance);
    }

    [HarmonyFinalizer]
    private static void Finalizer(DrawingContext drawingContext, int __state)
    {
        WaveformSvState.Deactivate();

        if (__state == 1 && drawingContext != null)
            drawingContext.Pop();
    }

    private struct Column
    {
        public double X;
        public double DeltaTop;
        public double DeltaBottom;
        public int Baseline;
        public double Center;
    }

    private static bool CustomRender(UIRenderedWave wave, DrawingContext dc)
    {
        WaveformSvState.Deactivate();

        var vm = wave.MusicalEditorVM;
        var seq = vm?.VSMSequence;
        var scores = wave.ScoreEnumerator;
        var samples = wave.SampleEnumerator;
        if (vm == null || seq == null || scores == null || samples == null)
            return false;

        long samplesPerFrame = seq.NumSampleInFrame;
        if (samplesPerFrame <= 0)
            return false;

        var samplingRate = seq.GetSamplingRate();
        double left = Canvas.GetLeft(wave);
        double right = Canvas.GetRight(wave);
        double oneKeyHeight = vm.OneKeyHeight;
        double waveHeight = Musical.RenderedWaveHeight * (oneKeyHeight / General.KeyBaseHeight) * .3;

        var centers = new Dictionary<int, double>();
        var cols = new List<Column>();

        for (double x = left; x <= right; x += 1.0)
        {
            double q1 = vm.GetQuarterFromViewPosition(x);
            double q2 = vm.GetQuarterFromViewPosition(x + 1.0);
            long sample = seq.GetSampleFromTime(seq.GetTimeFromQuarter(q1) - wave.SampleBeginAbsTime, samplingRate);
            long span = seq.GetSampleFromTime(seq.GetTimeFromQuarter(q1, q2), samplingRate);
            if (span == 0L)
                continue;

            long frame = sample / samplesPerFrame;
            if (scores.ScoreAtIndex(frame).NotePit == VSMScore.UnusedPitchData)
                continue;

            int baseline = WaveformSvState.BaselineAtFrame(frame);
            if (baseline == int.MinValue)
                continue;

            var thumbs = samples.ThumbWithRange(sample, sample + span);
            if (thumbs.Count == 0)
                continue;
            var thumb = thumbs[0];
            if (!thumb.HasValue)
                continue;

            var self = thumb.GetValueOrDefault();
            self.Join((short)0, (short)0);
            double dTop = -(waveHeight / 2.0) * AudioMath.ShortToFloat(self.Max);
            double dBottom = -(waveHeight / 2.0) * AudioMath.ShortToFloat(self.Min);

            if (!centers.TryGetValue(baseline, out double center))
            {
                center = WaveformSvState.Center(vm.CalcNoteNumberTopPosition(baseline), oneKeyHeight);
                centers[baseline] = center;
            }

            cols.Add(new Column
            {
                X = x - left,
                DeltaTop = dTop,
                DeltaBottom = dBottom,
                Baseline = baseline,
                Center = center
            });
        }

        if (cols.Count == 0)
            return false;

        var bg = wave.Background;
        var pen = wave.MainPen;

        double globalOpacity = Settings.WaveformOpacity;
        bool pushedOpacity = globalOpacity < 1.0;
        if (pushedOpacity)
            dc.PushOpacity(globalOpacity);

        try
        {
            var main = new List<(double X, double Top, double Bottom, int Key)>(cols.Count);
            foreach (var c in cols)
                main.Add((c.X, c.Center + c.DeltaTop, c.Center + c.DeltaBottom, c.Baseline));
            dc.DrawGeometry(bg, pen, BuildGeometry(main));

            for (int i = 1; i < cols.Count; i++)
            {
                var a = cols[i - 1];
                var b = cols[i];
                if (b.X != a.X + 1.0 || a.Baseline == b.Baseline)
                    continue;

                DrawGhost(dc, bg, pen, cols, i, a.Center, true);
                DrawGhost(dc, bg, pen, cols, i, b.Center, false);
            }

            DrawPhonemeSpans(wave, dc, vm, cols, left, right, oneKeyHeight);
        }
        finally
        {
            if (pushedOpacity)
                dc.Pop();
        }

        return true;
    }

    internal static DrawingGroup? CaptureDrawing(UIRenderedWave wave)
    {
        try
        {
            // WPF keeps the last OnRender output as retained drawing commands. Prefer
            // that exact image so an edit (especially deleting a note) cannot alter
            // the old waveform while the replacement render is only starting.
            var retained = VisualTreeHelper.GetDrawing(wave);
            if (retained != null && !retained.Bounds.IsEmpty)
            {
                var snapshot = retained.CloneCurrentValue();
                if (snapshot.CanFreeze)
                    snapshot.Freeze();
                return snapshot;
            }

            var drawing = new DrawingGroup();
            using (var drawingContext = drawing.Open())
            {
                if (!CustomRender(wave, drawingContext))
                    return null;
            }

            if (drawing.CanFreeze)
                drawing.Freeze();
            return drawing;
        }
        catch
        {
            return null;
        }
    }

    private static void DrawPhonemeSpans(
        UIRenderedWave wave,
        DrawingContext dc,
        MusicalEditorViewModel vm,
        List<Column> cols,
        double left,
        double right,
        double oneKeyHeight)
    {
        var part = vm.ActivePart;
        if (part == null || right <= left)
            return;

        if (!WaveformSvState.TryGetPhonemeSpans(part, vm.WidthPerTick, out var spans))
        {
            try
            {
                WaveformSvState.EnsurePhonemeSpans(vm, part);
            }
            catch
            {
                return;
            }

            if (!WaveformSvState.TryGetPhonemeSpans(part, vm.WidthPerTick, out spans))
                return;
        }

        int first = FindFirstPhonemeSpan(spans, left);
        if (first >= spans.Count || spans[first].StartX >= right)
            return;

        double fontSize = Math.Clamp(oneKeyHeight * 1.25, 8.0, 12.0);
        double markerHalfHeight = Math.Max(oneKeyHeight * 0.8, 4.0);
        double pixelsPerDip = VisualTreeHelper.GetDpi(wave).PixelsPerDip;
        var typeface = new Typeface(wave.FontFamily, wave.FontStyle, wave.FontWeight, wave.FontStretch);

        dc.PushOpacity(0.82);
        try
        {
            for (int i = first; i < spans.Count; i++)
            {
                var span = spans[i];
                if (span.StartX >= right)
                    break;
                if (span.EndX <= left)
                    continue;

                double startX = Math.Max(span.StartX, left) - left;
                double endX = Math.Min(span.EndX, right) - left;
                double width = endX - startX;
                if (width < 1.0)
                    continue;

                double center = CenterForSpan(cols, startX, endX);
                double top = center - markerHalfHeight;
                double bottom = center + markerHalfHeight;

                if (span.StartX >= left)
                    dc.DrawLine(PhonemeBoundaryPen, new Point(startX, top), new Point(startX, bottom));
                dc.DrawLine(PhonemeBoundaryPen, new Point(startX, bottom), new Point(endX, bottom));
                if (span.EndX <= right)
                    dc.DrawLine(PhonemeBoundaryPen, new Point(endX, top), new Point(endX, bottom));

                var text = new FormattedText(
                    span.Phoneme,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    PhonemeTextBrush,
                    pixelsPerDip);
                double textX = (span.StartX + span.EndX) / 2.0 - left - text.Width / 2.0;
                if (text.Width + 4.0 <= width && textX >= 0.0 && textX + text.Width <= right - left)
                    dc.DrawText(text, new Point(textX, top - text.Height - 1.0));
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private static int FindFirstPhonemeSpan(List<WaveformSvState.PhonemeSpan> spans, double left)
    {
        int low = 0;
        int high = spans.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (spans[middle].EndX <= left)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
            pen.Freeze();
        return pen;
    }

    private static double CenterForSpan(List<Column> cols, double startX, double endX)
    {
        double middle = (startX + endX) / 2.0;
        int lo = 0;
        int hi = cols.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (cols[mid].X < middle)
                lo = mid + 1;
            else
                hi = mid;
        }

        if (lo > 0 && Math.Abs(cols[lo - 1].X - middle) < Math.Abs(cols[lo].X - middle))
            lo--;
        return cols[lo].Center;
    }

    private static void DrawGhost(DrawingContext dc, Brush? bg, Pen? pen, List<Column> cols, int boundaryIndex, double altCenter, bool forward)
    {
        double boundaryX = cols[boundaryIndex].X;
        var pts = new List<(double X, double Top, double Bottom, int Key)>();

        if (forward)
        {
            int baseline = cols[boundaryIndex].Baseline;
            double prevX = boundaryX - 1.0;
            for (int k = boundaryIndex; k < cols.Count; k++)
            {
                var c = cols[k];
                if (c.Baseline != baseline || c.X != prevX + 1.0 || c.X >= boundaryX + FadeWidth)
                    break;
                pts.Add((c.X, altCenter + c.DeltaTop, altCenter + c.DeltaBottom, 0));
                prevX = c.X;
            }
        }
        else
        {
            int baseline = cols[boundaryIndex - 1].Baseline;
            double nextX = boundaryX;
            for (int k = boundaryIndex - 1; k >= 0; k--)
            {
                var c = cols[k];
                if (c.Baseline != baseline || c.X != nextX - 1.0 || c.X <= boundaryX - 1.0 - FadeWidth)
                    break;
                pts.Add((c.X, altCenter + c.DeltaTop, altCenter + c.DeltaBottom, 0));
                nextX = c.X;
            }
            pts.Reverse();
        }

        if (pts.Count == 0)
            return;

        var geo = BuildGeometry(pts);
        var brush = MakeFadeBrush(forward);
        dc.PushOpacityMask(brush);
        try
        {
            dc.DrawGeometry(bg, pen, geo);
        }
        finally
        {
            dc.Pop();
        }
    }

    private static Brush? _fadeRightBrush;
    private static Brush? _fadeLeftBrush;

    private static Brush MakeFadeBrush(bool fadeRight)
    {
        var cached = fadeRight ? _fadeRightBrush : _fadeLeftBrush;
        if (cached != null)
            return cached;

        var opaque = Color.FromArgb(255, 255, 255, 255);
        var clear = Color.FromArgb(0, 255, 255, 255);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.0, 0.0),
            EndPoint = new Point(1.0, 0.0)
        };
        if (fadeRight)
        {
            brush.GradientStops.Add(new GradientStop(opaque, 0.0));
            brush.GradientStops.Add(new GradientStop(clear, 1.0));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(clear, 0.0));
            brush.GradientStops.Add(new GradientStop(opaque, 1.0));
        }
        brush.Freeze();

        if (fadeRight)
            _fadeRightBrush = brush;
        else
            _fadeLeftBrush = brush;

        return brush;
    }

    private static StreamGeometry BuildGeometry(List<(double X, double Top, double Bottom, int Key)> pts)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                Line(ctx, p.X, p.Top, p.X, p.Bottom);
                if (i > 0)
                {
                    var q = pts[i - 1];
                    if (p.X == q.X + 1.0 && p.Key == q.Key)
                    {
                        Line(ctx, q.X, q.Top, p.X, p.Top);
                        Line(ctx, q.X, q.Bottom, p.X, p.Bottom);
                    }
                }
            }
        }
        geo.Freeze();
        return geo;
    }

    private static void Line(StreamGeometryContext ctx, double x0, double y0, double x1, double y1)
    {
        ctx.BeginFigure(new Point(x0, y0), false, false);
        ctx.LineTo(new Point(x1, y1), true, false);
    }
}

public class NoteRowRemapPatch : PatchBase
{
    public override string PatchName        => "NoteRowRemapPatch";
    public override Type   TargetClass      => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "CalcNoteNumberTopPosition";

    public override Type[] ArgumentTypes => new[] { typeof(int) };

    [HarmonyPostfix]
    private static void Postfix(int noteNumber, ref double __result, MusicalEditorViewModel __instance)
    {
        if (!WaveformSvState.Active)
            return;

        __result = WaveformSvState.Adjust(noteNumber, __result, __instance.OneKeyHeight);
    }
}

public class ScoreFrameCaptureListPatch : PatchBase
{
    public override string PatchName        => "ScoreFrameCaptureListPatch";
    public override Type   TargetClass      => typeof(VSMScoreList);
    public override string TargetMethodName => "ScoreAtIndex";

    public override Type[] ArgumentTypes => new[] { typeof(long) };

    [HarmonyPostfix]
    private static void Postfix(long index)
    {
        if (WaveformSvState.Active)
            WaveformSvState.CurrentFrame = index;
    }
}

public class ScoreFrameCaptureFilePatch : PatchBase
{
    public override string PatchName        => "ScoreFrameCaptureFilePatch";
    public override Type   TargetClass      => typeof(VSMScoreFile);
    public override string TargetMethodName => "ScoreAtIndex";

    public override Type[] ArgumentTypes => new[] { typeof(long) };

    [HarmonyPostfix]
    private static void Postfix(long index)
    {
        if (WaveformSvState.Active)
            WaveformSvState.CurrentFrame = index;
    }
}

public class ScoreFrameCaptureCombinedPatch : PatchBase
{
    public override string PatchName        => "ScoreFrameCaptureCombinedPatch";
    public override Type   TargetClass      => typeof(VSMCombinedScore);
    public override string TargetMethodName => "ScoreAtIndex";

    public override Type[] ArgumentTypes => new[] { typeof(long) };

    [HarmonyPostfix]
    private static void Postfix(long index)
    {
        if (WaveformSvState.Active)
            WaveformSvState.CurrentFrame = index;
    }
}

public class WaveformBaselineInvalidatePatch : PatchBase
{
    public override string PatchName        => "WaveformBaselineInvalidatePatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "OnRendererCompleted";

    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCompleteEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(PianorollView __instance, RendererObserverCompleteEventArgs e)
    {
        if (e?.MidiPart != null && __instance.DataContext is MusicalEditorViewModel vm
            && vm.ActivePart != null && vm.ActivePart.Equals(e.MidiPart))
            WaveformSvState.Invalidate();
    }
}

internal static class WaveformSvState
{
    internal readonly struct PhonemeSpan
    {
        public PhonemeSpan(double startX, double endX, string phoneme)
        {
            StartX = startX;
            EndX = endX;
            Phoneme = phoneme;
        }

        public double StartX { get; }
        public double EndX { get; }
        public string Phoneme { get; }
    }

    private const int GroupSemitones = 7;
    private const double DownwardRows = 3.0;
    private const long MaxFrames = 4_000_000;

    public static bool Active { get; private set; }
    public static long CurrentFrame { get; set; }

    private static int[]? _baselineByFrame;

    private static object? _cachedPart;
    private static long _cachedFrameCount;
    private static object? _phonemePart;
    private static double _phonemeWidthPerTick;
    private static List<PhonemeSpan>? _phonemeSpans;

    public static bool HasBaselines => _baselineByFrame != null;

    public static void Activate() => Active = true;
    public static void Deactivate() => Active = false;

    public static void Clear()
    {
        _baselineByFrame = null;
        _cachedPart = null;
        _cachedFrameCount = 0;
        _phonemePart = null;
        _phonemeWidthPerTick = 0.0;
        _phonemeSpans = null;
    }

    public static bool IsCached(object part, long frameCount)
        => _baselineByFrame != null
           && _cachedPart != null
           && _cachedFrameCount == frameCount
           && _cachedPart.Equals(part);

    public static void SetCacheKey(object part, long frameCount)
    {
        _cachedPart = part;
        _cachedFrameCount = frameCount;
    }

    public static void Invalidate()
    {
        _cachedPart = null;
        _cachedFrameCount = 0;
        _phonemePart = null;
        _phonemeWidthPerTick = 0.0;
    }

    public static void EnsurePhonemeSpans(MusicalEditorViewModel vm, WIVSMMidiPart part)
    {
        double widthPerTick = vm.WidthPerTick;
        if (_phonemeSpans != null && _phonemePart != null && _phonemePart.Equals(part)
            && _phonemeWidthPerTick.Equals(widthPerTick))
            return;

        var spans = new List<PhonemeSpan>();
        foreach (var note in part.NotesInPart)
        {
            if (note == null || !note.IsValidPhonemes)
                continue;

            if (SegmentedPhonemeRenderCoordinator.TryGetWavePhonemeSpans(
                    part,
                    note,
                    out var renderedSpans))
            {
                foreach (var renderedSpan in renderedSpans)
                {
                    double renderedStart = vm.CalcTickToViewPosition(
                        new VSMAbsTick(part.AbsPosTick.Value + renderedSpan.StartRelTick));
                    double renderedEnd = vm.CalcTickToViewPosition(
                        new VSMAbsTick(part.AbsPosTick.Value + renderedSpan.EndRelTick));
                    if (renderedEnd > renderedStart)
                    {
                        spans.Add(new PhonemeSpan(
                            renderedStart,
                            renderedEnd,
                            renderedSpan.Phoneme));
                    }
                }

                continue;
            }

            string value = note.Phonemes;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            string[] phonemes = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            List<int> positions = note.GetPhonemePositions();
            int count = Math.Min(phonemes.Length, positions.Count - 1);
            for (int i = 0; i < count; i++)
            {
                double start = vm.CalcTickToViewPosition(
                    note.GetAbsPositionFromNoteBaseTick(positions[i]));
                double end = vm.CalcTickToViewPosition(
                    note.GetAbsPositionFromNoteBaseTick(positions[i + 1]));
                if (end > start)
                    spans.Add(new PhonemeSpan(start, end, phonemes[i]));
            }
        }

        spans.Sort((left, right) => left.StartX.CompareTo(right.StartX));
        _phonemePart = part;
        _phonemeWidthPerTick = widthPerTick;
        _phonemeSpans = spans;
    }

    public static bool TryGetPhonemeSpans(
        WIVSMMidiPart part,
        double widthPerTick,
        out List<PhonemeSpan> spans)
    {
        if (_phonemeSpans != null && _phonemePart != null && _phonemePart.Equals(part)
            && _phonemeWidthPerTick.Equals(widthPerTick))
        {
            spans = _phonemeSpans;
            return spans.Count > 0;
        }

        spans = null!;
        return false;
    }

    public static int BaselineAtFrame(long frame)
    {
        var arr = _baselineByFrame;
        if (arr == null || frame < 0 || frame >= arr.LongLength)
            return int.MinValue;
        return arr[frame];
    }

    public static double Center(double baselineTop, double oneKeyHeight)
        => baselineTop + (DownwardRows + 0.5) * oneKeyHeight;

    public static void Precompute(IVSMScoreEnumerator scores, long frameCount)
    {
        _baselineByFrame = null;
        if (scores == null || frameCount <= 0 || frameCount > MaxFrames)
            return;

        var arr = new int[frameCount];

        long groupStart = -1;
        int groupMin = 0;
        int groupMax = 0;

        for (long i = 0; i < frameCount; i++)
        {
            float pit = scores.ScoreAtIndex(i).NotePit;
            if (pit == float.MinValue)
            {
                arr[i] = int.MinValue;
                continue;
            }

            int noteNumber = (int)VSMScore.GetRawNoteNumberFromPitch(pit);
            if (groupStart < 0)
            {
                groupStart = i;
                groupMin = noteNumber;
                groupMax = noteNumber;
                continue;
            }

            int newMin = Math.Min(groupMin, noteNumber);
            int newMax = Math.Max(groupMax, noteNumber);
            if (newMax - newMin <= GroupSemitones)
            {
                groupMin = newMin;
                groupMax = newMax;
            }
            else
            {
                FillGroup(arr, groupStart, i, groupMin);
                groupStart = i;
                groupMin = noteNumber;
                groupMax = noteNumber;
            }
        }

        if (groupStart >= 0)
            FillGroup(arr, groupStart, frameCount, groupMin);

        _baselineByFrame = arr;
    }

    private static void FillGroup(int[] arr, long start, long end, int baseline)
    {
        for (long i = start; i < end; i++)
            if (arr[i] != int.MinValue)
                arr[i] = baseline;
    }

    public static double Adjust(int noteNumber, double top, double oneKeyHeight)
    {
        var arr = _baselineByFrame;
        long frame = CurrentFrame;
        if (arr == null || frame < 0 || frame >= arr.LongLength)
            return top;

        int baseline = arr[frame];
        if (baseline == int.MinValue)
            return top;

        return top + (noteNumber - baseline + DownwardRows) * oneKeyHeight;
    }
}
