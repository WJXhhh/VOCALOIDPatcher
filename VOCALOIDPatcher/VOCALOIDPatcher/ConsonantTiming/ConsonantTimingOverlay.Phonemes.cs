using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private sealed record BoundaryHandle(UIEmotionNote Visual, SpanVisual Span, int Index,
        double X, double Y, double Height, int[] Positions);
    private readonly List<BoundaryHandle> _phonemeHandles = new();
    private BoundaryHandle? _phonemeHover;
    private PhonemeDrag? _phonemeDrag;
    private DispatcherTimer? _phonemeTimer;
    private sealed record PhonemePending(nint Source, nint Note, TimingNoteAddress Address, string Fingerprint, int[] Positions);
    private PhonemePending? _phonemePending;
    private string? _phonemeFailure;

    private sealed class PhonemeDrag
    {
        internal required WIVSMNote Note;
        internal required UIEmotionNote Visual;
        internal required MusicalEditorViewModel VM;
        internal required SampledTimingSession Session;
        internal required LocalConsonantTimingBackend.PreparedCoefficientTiming Prepared;
        internal required TimingNoteAddress Address;
        internal required SampledTimingPlan Original, Best;
        internal required int Index;
        internal required long Revision, SourceRevision, DownTick;
        internal required int BaselineTick, InitialTick;
        internal required int[] Positions;
        internal IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?>? Plans;
        internal IReadOnlyDictionary<nint, int[]>? RelatedPositions;
        internal double FrameWidth;
        internal nint Source;
        internal Point Point;
        internal bool Moved, QueryPending;
    }

    private void ConfigureTerminalBoundary(SpanVisual span, UIEmotionNote visual, MusicalEditorViewModel vm, long tick, int relative, double y)
    {
        var line = span.Line;
        line.VM = vm; line.Note = visual.Note; line.NoteNumber = visual.Note!.NoteNumber;
        line.AbsPosTick = new VSMAbsTick(tick); line.EndPosTick = new VSMAbsTick(tick);
        line.PhonemePositionIndex = visual.Note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        line.Width = 0; line.SignedWidth = 0; line.Height = vm.OneKeyHeight;
        line.HasEndLine = false; line.NormalLinePen = _editable; line.HighlightLinePen = _editable;
        line.DisabledLinePen = _reference; line.HighlightFillBrush = _highlight;
        line.IsHighlight = false; line.Visibility = Visibility.Visible; line.BuddyPhonemes = null;
        Canvas.SetLeft(line, vm.CalcTickToViewPosition(new VSMAbsTick(tick))); Canvas.SetTop(line, y);
        line.InvalidateVisual(); span.Label.Visibility = Visibility.Collapsed;
    }

    private BoundaryHandle? HitPhoneme(Point point) => _phonemeHandles
        .Where(h => point.Y >= h.Y - 3 && point.Y <= h.Y + h.Height + 3 && Math.Abs(point.X - h.X) <= 7)
        .OrderBy(h => Math.Abs(point.X - h.X)).FirstOrDefault();

    private bool SearchPhonemeBoundary(Point point, out PianorollView.EmotionModeSearchResults result)
    {
        result = default;
        if (_phonemeDrag is { } drag)
        {
            double x = drag.VM.CalcTickToViewPosition(new VSMAbsTick(drag.Note.AbsPosTick.Value + drag.Positions[drag.Index]));
            result = new(drag.Visual, PianorollView.HitObjectTypeEnum.Velocity, x, x, true);
            return true;
        }
        var handle = HitPhoneme(point);
        if (_phonemeHover != null && !ReferenceEquals(_phonemeHover, handle))
        { _phonemeHover.Span.Line.IsHighlight = false; _phonemeHover.Span.Line.InvalidateVisual(); }
        _phonemeHover = handle;
        if (handle == null) return false;
        if (_hover != null) SetHover(null);
        handle.Span.Line.IsHighlight = true; handle.Span.Line.InvalidateVisual();
        ShowPhonemeTip(handle.Visual.Note!, handle.Index, handle.Positions, point);
        result = new(handle.Visual, PianorollView.HitObjectTypeEnum.Velocity, handle.X, handle.X, true);
        return true;
    }

    private void ShowPhonemeTip(WIVSMNote note, int index, int[] positions, Point point)
    {
        if (note.Sequence is not { } source || index < 0 || index >= positions.Length) return;
        string[] phones = note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (positions.Length != phones.Length + 1 || index > phones.Length) return;
        string left = index == 0 ? TranslationManager.Tr("VOCALOIDPatcher_PhonemeTiming_Start") : phones[index - 1];
        string right = index == phones.Length ? TranslationManager.Tr("VOCALOIDPatcher_PhonemeTiming_End") : phones[index];
        double ms = 1000 * (source.GetTimeFromTick(new VSMAbsTick(note.AbsPosTick.Value + positions[index])) - source.GetTimeFromTick(note.AbsPosTick));
        ShowTip(TranslationManager.Tr("VOCALOIDPatcher_PhonemeTiming_Value", left, right,
            ms.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)), point);
    }

    private static SampledTimingPlan BoundaryPlan(SampledTimingPlan original, int index, float? seconds)
        => PhonemeBoundaryEdit.WithBoundary(original, index, seconds);

    private void ClearPhonemeHover()
    {
        if (_phonemeHover is not { } handle) return;
        _phonemeHover = null;
        handle.Span.Line.IsHighlight = false;
        handle.Span.Line.InvalidateVisual();
    }

    private bool TryBeginPhonemeDrag(Point point, int clicks)
    {
        if (_phonemeDrag != null) return true;
        var handle = HitPhoneme(point);
        if (handle?.Visual.Note is not { } note || _view.DataContext is not MusicalEditorViewModel vm ||
            vm.VSMSequence is not { } source || source.IsStaged || _view.IsMouseCaptured) return false;
        LocalConsonantTimingBackend.PreparedCoefficientTiming? prepared = null;
        try
        {
            var session = SampledTimingHost.ForPhonemeEditing(source);
            var address = SampledTimingVsmBinding.Address(note);
            var original = session.Document.Get(address) ?? new(note.Phonemes, []);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || clicks == 2)
            {
                var reset = BoundaryPlan(original, handle.Index, null);
                SampledTimingHost.SetPhonemePlan(note, reset.Adjustments.Count == 0 ? null : reset, handle.Index);
                return true;
            }
            prepared = session.Prepare(note);
            var baseline = prepared.QueryPhonemes(BoundaryPlan(original, handle.Index, null), session.Revision, nativeWaitBudgetMs: 0, editedBoundary: handle.Index);
            var current = prepared.QueryPhonemes(original, session.Revision, nativeWaitBudgetMs: 0);
            if (current.Result.Boundaries.Length != handle.Positions.Length) throw new NotSupportedException("Timing topology unavailable.");
            _phonemeDrag = new()
            {
                Note = note,
                Visual = handle.Visual,
                VM = vm,
                Session = session,
                Prepared = prepared,
                Address = address,
                Original = original,
                Best = original,
                Index = handle.Index,
                Revision = session.Revision,
                SourceRevision = ConsonantTimingRuntime.Revision,
                Source = (nint)source,
                DownTick = vm.CalcViewPositionToTick(point.X, QuantizeStrategy.None).Value,
                Point = point,
                BaselineTick = checked((int)baseline.Result.Boundaries[handle.Index]),
                InitialTick = checked((int)current.Result.Boundaries[handle.Index]),
                Positions = current.Result.Boundaries.Select(v => checked((int)v)).ToArray(),
                FrameWidth = handle.Visual.FramePen?.Thickness ?? 1
            };
            prepared = null;
            _phonemeFailure = null;
            if (!_view.CaptureMouse()) { CancelPhonemeDrag(); return true; }
            vm.EditorMode.IsIdleMouseOperation = false; _view.Cursor = Cursors.SizeWE;
            DrawPhonemeCandidate();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or TimeoutException or EntryPointNotFoundException)
        { _phonemeFailure = "begin: " + e.GetType().Name + ": " + e.Message;
            ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Unavailable"), point); }
        finally { prepared?.Dispose(); }
        return true;
    }

    private bool PhonemeContextValid(PhonemeDrag drag) => Enabled(_view) && _view.DataContext == drag.VM &&
        drag.SourceRevision == ConsonantTimingRuntime.Revision && drag.Revision == drag.Session.Revision &&
        drag.VM.VSMSequence is { IsStaged: false } source && (nint)source == drag.Source &&
        drag.VM.ActivePart is { } part && (nint)part == (nint)drag.Note.Parent! &&
        drag.Note.Phonemes == drag.Address.Topology && drag.Note.RelPosTick.Value == drag.Address.Position &&
        drag.Note.DurationTick.Value == drag.Address.Duration && drag.Note.NoteNumber == drag.Address.Pitch && part.VoiceBankID == drag.Address.Bank;

    private void MovePhonemeDrag(MouseEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (_phonemeDrag is not { } drag) return;
            if (!PhonemeContextValid(drag) || e.LeftButton != MouseButtonState.Pressed) { CancelPhonemeDrag(); return; }
            drag.Point = e.GetPosition(_view);
            long tick = drag.VM.CalcViewPositionToTick(drag.Point.X, QuantizeStrategy.None).Value;
            if (!drag.Moved && Math.Abs(tick - drag.DownTick) < 2) return;
            drag.Moved = true; drag.QueryPending = true; drag.VM.HorizontalDragScroll = drag.Point.X;
            _phonemeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render,
                (_, _) => QueryPhonemeCandidate(), _view.Dispatcher);
            _phonemeTimer.Start();
        }
        catch (Exception e2) { Fail(_view, e2); }
    }

    private void QueryPhonemeCandidate()
    {
        if (_phonemeDrag is not { QueryPending: true } drag) { _phonemeTimer?.Stop(); return; }
        if (!PhonemeContextValid(drag)) { CancelPhonemeDrag(); return; }
        try
        {
            long goal = drag.InitialTick + drag.VM.CalcViewPositionToTick(drag.Point.X, QuantizeStrategy.None).Value - drag.DownTick;
            long absolute = drag.Note.AbsPosTick.Value;
            float offset = (float)(drag.Note.Sequence!.GetTimeFromTick(new VSMAbsTick(absolute + goal)) -
                drag.Note.Sequence.GetTimeFromTick(new VSMAbsTick(absolute + drag.BaselineTick)));
            offset = (float)(Math.Round(offset * (44100d / 256)) * (256d / 44100));
            var candidate = BoundaryPlan(drag.Original, drag.Index, offset);
            var layout = drag.Prepared.QueryPhonemes(candidate, drag.Revision, nativeWaitBudgetMs: 0, editedBoundary: drag.Index);
            drag.Best = candidate; drag.Positions = layout.Result.Boundaries.Select(v => checked((int)v)).ToArray();
            drag.Plans = layout.Plans;
            drag.RelatedPositions = layout.RelatedBoundaries?.ToDictionary(p =>
                (nint)(drag.Note.Parent!.GetNote(checked((ulong)p.Key.Note)) ?? throw new InvalidOperationException("Shared timing note detached.")),
                p => p.Value.Select(v => checked((int)v)).ToArray());
            drag.QueryPending = false; _phonemeFailure = null; DrawPhonemeCandidate();
        }
        catch (TimeoutException) { } // Retry only the latest goal on the next timer beat.
        catch (InvalidOperationException e) { _phonemeFailure = "query: " + e.GetType().Name + ": " + e.Message; }
        catch (NotSupportedException e)
        {
            _phonemeFailure = "query: " + e.GetType().Name + ": " + e.Message;
            drag.QueryPending = false;
            ShowTip(TranslationManager.Tr("VOCALOIDPatcher_PhonemeTiming_Limit"), drag.Point);
        }
        catch (Exception e) { Fail(_view, e); }
    }

    private void DrawPhonemeCandidate()
    {
        if (_phonemeDrag is not { } drag) return;
        foreach (var span in _pool)
        {
            var line = span.Line;
            if (line.Visibility != Visibility.Visible || line.Note is not { } note) continue;
            var positions = (nint)note == (nint)drag.Note ? drag.Positions :
                drag.RelatedPositions?.GetValueOrDefault((nint)note);
            if (positions == null) continue;
            int i = line.PhonemePositionIndex;
            if (i < 0 || i >= positions.Length) continue;
            long start = note.AbsPosTick.Value + positions[i];
            long end = i + 1 < positions.Length ? note.AbsPosTick.Value + positions[i + 1] : start;
            double x = drag.VM.CalcTickToViewPosition(new VSMAbsTick(start));
            double right = drag.VM.CalcTickToViewPosition(new VSMAbsTick(end));
            line.AbsPosTick = new VSMAbsTick(start); line.EndPosTick = new VSMAbsTick(end);
            double bodyLeft = drag.VM.CalcTickToViewPosition(note.AbsPosTick) + drag.FrameWidth;
            line.Width = Math.Max(0, (i == 0 ? Math.Max(right, bodyLeft) : right) - x); line.SignedWidth = right - x; Canvas.SetLeft(line, x); line.InvalidateVisual();
            span.Gain.Width = Math.Max(0, right - x); Canvas.SetLeft(span.Gain, x);
            if (span.Label.Visibility == Visibility.Visible)
            { span.Label.RelPosTick = new VSMRelTick(positions[i]); Canvas.SetLeft(span.Label, (x + right - (span.Label.FormattedPhoneme?.Width ?? 0)) / 2); span.Label.InvalidateVisual(); }
        }
        ShowPhonemeTip(drag.Note, drag.Index, drag.Positions, drag.Point);
    }

    private void EndPhonemeDrag(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_phonemeDrag is not { } drag) return;
        try
        {
            if (!PhonemeContextValid(drag)) { CancelPhonemeDrag(); return; }
            drag.Point = e.GetPosition(_view);
            if (drag.Moved) drag.QueryPending = true;
            QueryPhonemeCandidate();
            if (_phonemeDrag != drag || !PhonemeContextValid(drag)) { CancelPhonemeDrag(); return; }
            bool commit = drag.Moved && (drag.Plans?.Any(p =>
                drag.Session.Document.Get(p.Key)?.Fingerprint != p.Value?.Fingerprint) ?? drag.Best.Fingerprint != drag.Original.Fingerprint);
            var positions = drag.Positions.ToArray(); var plan = drag.Best;
            CancelPhonemeDrag();
            if (commit)
            {
                SampledTimingHost.SetPhonemePlan(drag.Note, plan.Adjustments.Count == 0 ? null : plan, drag.Index);
                _phonemePending = new(drag.Source, (nint)drag.Note, drag.Address, plan.Fingerprint, positions);
                RequestRefresh();
            }
        }
        catch (Exception e2) when (e2 is NotSupportedException or InvalidOperationException or TimeoutException)
        { _phonemeFailure = "commit: " + e2.GetType().Name + ": " + e2.Message;
            CancelPhonemeDrag(); ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Unavailable"), drag.Point); }
        catch (Exception e2) { CancelPhonemeDrag(); Fail(_view, e2); }
    }

    private void CancelPhonemeDrag()
    {
        _phonemeTimer?.Stop();
        if (_phonemeDrag is not { } drag) return;
        _phonemeDrag = null; drag.Prepared.Dispose(); drag.VM.EditorMode.IsIdleMouseOperation = true;
        _phonemeHover = null; _tip.IsOpen = false; _view.Cursor = null;
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        RequestRefresh();
    }

    private List<int> PendingPhonemePositions(MusicalEditorViewModel vm, WIVSMNote note, List<int> actual)
    {
        if (_phonemePending is not { } pending || (nint)note != pending.Note) return actual;
        if (vm.VSMSequence is not { } source || (nint)source != pending.Source ||
            note.RelPosTick.Value != pending.Address.Position || note.DurationTick.Value != pending.Address.Duration ||
            note.NoteNumber != pending.Address.Pitch || note.Phonemes != pending.Address.Topology || note.Parent?.VoiceBankID != pending.Address.Bank ||
            SampledTimingHost.Find(source)?.Document.Get(pending.Address)?.Fingerprint != pending.Fingerprint)
        { _phonemePending = null; return actual; }
        if (actual.SequenceEqual(pending.Positions)) { _phonemePending = null; return actual; }
        return pending.Positions.ToList();
    }
}
