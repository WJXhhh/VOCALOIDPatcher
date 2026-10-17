using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using Debug = VOCALOIDPatcher.Utils.Debug;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private DispatcherTimer? _queryTimer;
    private PendingTiming? _pending;

    private sealed class DragState
    {
        internal required WIVSMNote Note;
        internal required MusicalEditorViewModel ViewModel;
        internal required nint SequenceId, PartId;
        internal required long DownTick, NoteTick, Duration;
        internal required int InitialVelocity, Pitch, VowelIndex;
        internal required double FrameWidth;
        internal required string Phonemes, Bank;
        internal required ConsonantTimingDragSearch Search;
        internal Point Point;
        internal bool Moved;
        internal int Queries;
        internal long InitialOnset;
        internal long Revision;
    }

    private sealed record PendingTiming(nint Sequence, nint Part, nint Note, int Velocity, long[] Boundaries)
    {
        internal bool Completed;
        internal long Revision = ConsonantTimingRuntime.Revision;
    }

    private bool TryBeginDrag(Point point)
    {
        if (_view.DataContext is not MusicalEditorViewModel vm || vm.VSMSequence is not { } sequence ||
            sequence.IsStaged || _view.IsMouseCaptured ||
            Hit(point) is not { } onset || onset.Visual.Note is not { } note) return false;
        if (ConsonantTimingRuntime.Backend is not { } backend)
        {
            ConsonantTimingRuntime.WarmUp(_view.Dispatcher);
            ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Unavailable"), point);
            return true;
        }
        try
        {
            var baseline = backend.Query(note, note.NoteVelocity, nativeWaitBudgetMs: 0).Boundaries;
            long[] displayed = _pending is { } pending && pending.Note == (nint)note && pending.Velocity == note.NoteVelocity
                ? pending.Boundaries : note.GetPhonemePositions().Select(value => (long)value).ToArray();
            if (baseline.Length < 2 || !baseline.SequenceEqual(displayed))
                throw new NotSupportedException("Local timing baseline differs from the rendered context.");
            int vowel = Array.FindIndex(note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries), note.IsVowel);
            if (vowel <= 0 || vowel >= baseline.Length - 1) return false;
            _drag = new DragState
            {
                Note = note, ViewModel = vm, SequenceId = (nint)sequence, PartId = (nint)vm.ActivePart!,
                DownTick = vm.CalcViewPositionToTick(point.X, QuantizeStrategy.None).Value,
                NoteTick = note.AbsPosTick.Value, Duration = note.DurationTick.Value,
                InitialVelocity = note.NoteVelocity, Pitch = note.NoteNumber, Phonemes = note.Phonemes,
                Bank = note.Parent!.VoiceBankID, VowelIndex = vowel,
                FrameWidth = onset.Visual.FramePen?.Thickness ?? 1,
                Search = new ConsonantTimingDragSearch(note.NoteVelocity, baseline),
                InitialOnset = baseline[0], Point = point,
                Revision = ConsonantTimingRuntime.Revision,
            };
            if (!_view.CaptureMouse()) { _drag = null; return false; }
            vm.EditorMode.IsIdleMouseOperation = false;
            _view.Cursor = Cursors.SizeWE;
            SetHover(onset, point);
            DrawCandidate();
            Debug.Print("Timing drag begin; VEL=" + note.NoteVelocity);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or NotSupportedException)
        {
            ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Unavailable"), point);
            return true; // The acoustic handle must not fall into native VEL math.
        }
    }

    private bool DragContextValid()
    {
        if (_drag is not { } drag || !Enabled(_view) || _view.DataContext != drag.ViewModel ||
            drag.Revision != ConsonantTimingRuntime.Revision ||
            drag.ViewModel.VSMSequence is not { } current || (nint)current != drag.SequenceId ||
            drag.ViewModel.ActivePart is not { } part || (nint)part != drag.PartId || current.IsStaged)
            return false;
        return drag.Note.Parent?.Equals(part) == true && drag.Note.NoteVelocity == drag.InitialVelocity &&
            drag.Note.AbsPosTick.Value == drag.NoteTick && drag.Note.DurationTick.Value == drag.Duration &&
            drag.Note.NoteNumber == drag.Pitch && drag.Note.Phonemes == drag.Phonemes && part.VoiceBankID == drag.Bank;
    }

    private void OnDragMouseMove(object sender, MouseEventArgs e)
    {
        if (_durationDrag != null)
        {
            try { MoveDurationDrag(e); }
            catch (Exception error) { DurationFailure(error, e.GetPosition(_view)); }
            return;
        }
        if (_volumeDrag != null)
        {
            try { MoveVolumeDrag(e); }
            catch (Exception exception) { VolumeFailure(exception); }
            return;
        }
        if(_phonemeDrag!=null) { MovePhonemeDrag(e); return; }
        if (_drag is not { } drag) return;
        e.Handled = true;
        try
        {
            if (!DragContextValid() || e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
            drag.Point = e.GetPosition(_view);
            long tick = drag.ViewModel.CalcViewPositionToTick(drag.Point.X, QuantizeStrategy.None).Value;
            if (!drag.Moved && Math.Abs(tick - drag.DownTick) < 2) return;
            drag.Moved = true;
            drag.Search.SetGoal(drag.InitialOnset + tick - drag.DownTick);
            drag.ViewModel.HorizontalDragScroll = drag.Point.X;
            DrawCandidate();
            _queryTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
                (_, _) => QueryLatest(), _view.Dispatcher);
            if (!drag.Search.Complete) _queryTimer.Start();
        }
        catch (Exception exception) { Fail(_view, exception); }
    }

    private void QueryLatest()
    {
        try
        {
            if (!DragContextValid() || _drag is not { Moved: true } drag) { CancelDrag(); return; }
            var backend = ConsonantTimingRuntime.Backend;
            if (backend == null) { CancelDrag(); return; }
            drag.Search.Step(velocity =>
            {
                try
                {
                    drag.Queries++;
                    return backend.Query(drag.Note, velocity, nativeWaitBudgetMs: 0).Boundaries;
                }
                catch (TimeoutException) { return null; }
            });
            DrawCandidate();
            if (drag.Search.Complete) _queryTimer?.Stop();
        }
        catch (NotSupportedException)
        {
            var point = _drag?.Point ?? default;
            CancelDrag();
            ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Unavailable"), point);
        }
        catch (Exception exception) { Fail(_view, exception); }
    }

    private void DrawCandidate()
    {
        if (_drag is not { } drag) return;
        var candidate = drag.Search.Best;
        var vm = drag.ViewModel;
        double bodyLeft = vm.CalcTickToViewPosition(new VSMAbsTick(drag.NoteTick)) + drag.FrameWidth;
        foreach (var span in _pool)
        {
            var line = span.Line;
            if (line.Visibility != Visibility.Visible || line.Note == null || (nint)line.Note != (nint)drag.Note) continue;
            int i = line.PhonemePositionIndex;
            if (i < 0 || i + 1 >= candidate.Boundaries.Length) continue;
            long start = drag.NoteTick + candidate.Boundaries[i], end = drag.NoteTick + candidate.Boundaries[i + 1];
            double x = vm.CalcTickToViewPosition(new VSMAbsTick(start));
            double right = vm.CalcTickToViewPosition(new VSMAbsTick(end));
            line.AbsPosTick = new VSMAbsTick(start); line.EndPosTick = new VSMAbsTick(end);
            line.Width = Math.Max(0, (i == 0 ? Math.Max(right, bodyLeft) : right) - x); line.SignedWidth = right - x;
            Canvas.SetLeft(line, x); line.InvalidateVisual();
            span.Gain.Width = Math.Max(0, right - x); Canvas.SetLeft(span.Gain, x);
            span.Label.RelPosTick = new VSMRelTick(candidate.Boundaries[i]);
            Canvas.SetLeft(span.Label, (x + right - (span.Label.FormattedPhoneme?.Width ?? 0)) / 2);
            span.Label.InvalidateVisual();
        }
        var sequence = vm.VSMSequence!;
        double milliseconds = 1000 * (sequence.GetTimeFromTick(new VSMAbsTick(drag.NoteTick + candidate.Boundaries[drag.VowelIndex])) -
            sequence.GetTimeFromTick(new VSMAbsTick(drag.NoteTick + candidate.Boundaries[0])));
        ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Preview",
            milliseconds.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture), candidate.Velocity), drag.Point);
    }

    private void OnDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_durationDrag != null)
        {
            try { EndDurationDrag(e); }
            catch (Exception error) { DurationFailure(error, e.GetPosition(_view)); }
            return;
        }
        if (_volumeDrag != null)
        {
            try { EndVolumeDrag(e); }
            catch (Exception exception) { VolumeFailure(exception); }
            return;
        }
        if(_phonemeDrag!=null) { EndPhonemeDrag(e); return; }
        if (_drag is not { } drag) return;
        e.Handled = true;
        try
        {
            if (DragContextValid() && drag.Moved && drag.Search.Best.Velocity != drag.InitialVelocity)
            {
                // Commit exactly the last displayed verified candidate, even
                // when the mouse is released before inverse search finishes.
                var candidate = drag.Search.Best;
                _pending = new PendingTiming(drag.SequenceId, drag.PartId, (nint)drag.Note,
                    candidate.Velocity, candidate.Boundaries.ToArray());
                using (var transaction = new Transaction(drag.ViewModel.VSMSequence))
                {
                    transaction.Result = false;
                    drag.Note.NoteVelocity = candidate.Velocity;
                    transaction.Result = drag.Note.NoteVelocity == candidate.Velocity;
                    if (!transaction.Result) _pending = null;
                }
                if (_pending != null) _pending.Revision = ConsonantTimingRuntime.Revision;
                Debug.Print($"Timing commit; VEL={candidate.Velocity}; onset={candidate.Boundaries[0]}; queries={drag.Queries}");
            }
            CancelDrag();
        }
        catch (Exception exception) { _pending = null; Fail(_view, exception); }
    }

    private void OnDragKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_durationDrag != null || DurationMode))
        { InvalidateDurationContext(); _tip.IsOpen = false; e.Handled = true; return; }
        if (_volumeDrag != null) { e.Handled = true; if (e.Key == Key.Escape) CancelVolumeDrag(); return; }
        if(_phonemeDrag!=null) { e.Handled=true; if(e.Key==Key.Escape) CancelPhonemeDrag(); return; }
        if (_drag == null) return;
        e.Handled = true;
        if (e.Key == Key.Escape) CancelDrag();
    }

    private void OnDragLostMouseCapture(object sender, MouseEventArgs e)
    {
        // LostMouseCapture bubbles from descendants. Capturing the pianoroll
        // can release a child first; that event must not cancel this new drag.
        if (ReferenceEquals(e.OriginalSource, _view) && !_view.IsMouseCaptured) { CancelDurationDrag(); CancelVolumeDrag(); CancelPhonemeDrag(); CancelDrag(); }
    }

    private void CancelDrag()
    {
        _queryTimer?.Stop();
        if (_drag is not { } drag) return;
        _drag = null;
        drag.ViewModel.EditorMode.IsIdleMouseOperation = true;
        SetHover(null);
        _view.Cursor = null;
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        RequestRefresh();
    }

    private List<int> PendingPositions(MusicalEditorViewModel vm, WIVSMNote note, List<int> actual)
    {
        if (_pending is not { } pending || pending.Note != (nint)note) return actual;
        if (pending.Revision != ConsonantTimingRuntime.Revision || vm.VSMSequence == null || (nint)vm.VSMSequence != pending.Sequence ||
            note.NoteVelocity != pending.Velocity) { _pending = null; return actual; }
        if (actual.Select(x => (long)x).SequenceEqual(pending.Boundaries) ||
            (pending.Completed && note.Parent?.HasValidRenderedScore == true))
        {
            bool equal = actual.Select(x => (long)x).SequenceEqual(pending.Boundaries);
            Debug.Print("Timing render verified; equal=" + equal + "; VEL=" + pending.Velocity);
            _pending = null;
            return actual;
        }
        return pending.Boundaries.Select(value => checked((int)value)).ToList();
    }

    internal static void RenderCompleted(MusicalEditorViewModel vm, WIVSMMidiPart part)
    {
        if (vm.PianorollView is not { } view || !States.TryGetValue(view, out var state)) return;
        state._renderedFeedbackPart = (nint)part;
        state._renderedFeedbackRevision = ConsonantTimingRuntime.Revision;
        state._feedbackRevision = -1;
        if (state._pending is { } pending && pending.Part == (nint)part)
        {
            pending.Completed = true;
        }
        state.RequestRefresh();
    }
}
