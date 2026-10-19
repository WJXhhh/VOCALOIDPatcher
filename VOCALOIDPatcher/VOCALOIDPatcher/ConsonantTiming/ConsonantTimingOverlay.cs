using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using Debug = VOCALOIDPatcher.Utils.Debug;
using NoteDesign = Yamaha.VOCALOID.Design.UI.Note;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private const int MaximumVisuals = 2048;
    private static readonly ConditionalWeakTable<PianorollView, ConsonantTimingOverlay> States = new();
    private static readonly FieldInfo? PanelField = AccessTools.Field(typeof(PianorollView), "xPanel");
    private static readonly FieldInfo? NoteCanvasField = AccessTools.Field(typeof(PianorollView), "xNoteInsideActiveTrackCanvas");
    private static readonly FieldInfo? PhonemeCanvasField = AccessTools.Field(typeof(PianorollView), "xPhonemeCanvas");
    private static readonly FieldInfo? BoundaryCanvasField = AccessTools.Field(typeof(PianorollView), "xPhonemeTimingBoundaryLineCanvas");
    private static readonly FieldInfo? TextBrushField = AccessTools.Field(typeof(PianorollView), "brushLyricFontWhenLetterMode");

    private readonly PianorollView _view;
    private readonly Canvas _root = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<SpanVisual> _pool = new();
    private readonly List<Onset> _onsets = new();
    private readonly ToolTip _tip = new() { StaysOpen = true, Placement = PlacementMode.Relative, IsHitTestVisible = false };
    private readonly Pen _editable;
    private readonly Pen _reference;
    private readonly Brush _highlight;
    private readonly FastCanvas _nativePhonemes;
    private readonly Dictionary<UIEmotionPhoneme, double> _hiddenGlyphs = new();
    private readonly Dictionary<string, FormattedText> _textCache = new(StringComparer.Ordinal);
    private Appearance? _appearance;
    private bool _refreshPending;
    private bool _refreshing;
    private Onset? _hover;
    private DragState? _drag;
    private nint _feedbackNote;
    private int _feedbackVelocity = -1;
    private long _feedbackRevision = -1;
    private double _feedbackMilliseconds;
    private string? _feedbackMessage;
    private long _feedbackRetryAt;
    private nint _renderedFeedbackPart;
    private long _renderedFeedbackRevision = -1;

    private sealed record SpanVisual(UIPhonemeTimingBoundaryLine Line, UIPhonemeTimingPhonemes Label, GainGlyph Gain);
    private sealed record Onset(UIEmotionNote Visual, SpanVisual Span,
        double X, double Right, double Y, double Height, double Milliseconds);
    private sealed record Appearance(Typeface Typeface, Brush Brush, double Dpi, string Culture);

    private ConsonantTimingOverlay(PianorollView view)
    {
        _view = view;
        if (PanelField?.GetValue(view) is not Grid panel ||
            PhonemeCanvasField?.GetValue(view) is not FastCanvas phonemes ||
            BoundaryCanvasField?.GetValue(view) is not Canvas boundary)
            throw new NotSupportedException("Timing layers unavailable");
        _nativePhonemes = phonemes;
        _root.RenderTransform = boundary.RenderTransform;
        Panel.SetZIndex(_root, Panel.GetZIndex(boundary));
        panel.Children.Insert(panel.Children.IndexOf(boundary) + 1, _root);
        _editable = FrozenPen(view.TryFindResource("Brush_PhonemeTiming_HighlightLine") as Brush ?? Brushes.LightSkyBlue, 1.5);
        _reference = FrozenPen(view.TryFindResource("Brush_PhonemeTiming_NormalLine") as Brush ?? Brushes.Gray, .7, true);
        _highlight = view.TryFindResource("Brush_PhonemeTiming_HighlightFill") as Brush ?? Brushes.Transparent;
        _tip.PlacementTarget = view;
        // This text is already formatted by TranslationManager.Tr. The global
        // Loaded/Open translator otherwise restores the first text cached for
        // the popup's reused TextBlock, even after Content has changed.
        WpfTranslationPatch.MarkUntranslatable(_tip);
        view.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        view.PreviewMouseMove += OnDragMouseMove;
        view.PreviewMouseLeftButtonUp += OnDragMouseUp;
        view.PreviewKeyDown += OnDragKeyDown;
        view.LostMouseCapture += OnDragLostMouseCapture;
        view.MouseLeave += (_, _) => {
            if (_drag == null && _phonemeDrag == null && _volumeDrag == null && _durationDrag == null && _transitionDrag == null)
            { _durationHover = null; _volumeHover = null; _transitionHover = null; ClearPhonemeHover(); SetHover(null); }
        };
        view.Unloaded += (_, _) => Suspend();
        view.DataContextChanged += (_, _) => Suspend();
        AttachVolumeContextMenu();
        AttachDurationTool(panel);
        AttachTransitionBand();
    }

    private static Pen FrozenPen(Brush brush, double width, bool dotted = false)
    {
        var pen = new Pen(brush, width);
        if (dotted) pen.DashStyle = DashStyles.Dot;
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static bool Enabled(PianorollView view) => (Settings.IntuitiveConsonantTiming || Settings.IndividualPhonemeVolume || Settings.IndependentDurationSelection || Settings.IndividualTransitionControl) &&
        view.DataContext is MusicalEditorViewModel vm && vm.EditorMode.Mode == EditModeME.Emotion &&
        vm.VSMSequence is { IsOpen: true } &&
        vm.ActiveTrack?.Type == VSMTrackType.Midi && vm.ActivePart != null;

    internal static void Refresh(PianorollView view)
    {
        if (!view.Dispatcher.CheckAccess())
        {
            view.Dispatcher.BeginInvoke(new Action(() => Refresh(view)));
            return;
        }
        try
        {
            if (!Enabled(view))
            {
                if (States.TryGetValue(view, out var old)) old.Suspend();
                return;
            }
            var state = States.GetValue(view, v => new ConsonantTimingOverlay(v));
            state.RequestRefresh();
        }
        catch (Exception exception) { Fail(view, exception); }
    }

    internal static bool Search(PianorollView view, Point point, out PianorollView.EmotionModeSearchResults result)
    {
        result = default;
        try
        {
            if (!Enabled(view) || !States.TryGetValue(view, out var state) || state._root.Visibility != Visibility.Visible)
                return false;
            if (state.DurationMode) return state.SearchDuration(point, out result);
            if (state.TransitionMode) { state.SearchTransition(point, out result); return true; }
            if (state._volumeDrag != null && state.SearchVolume(point, out result)) return true;
            state._volumeHover = null;
            if (Settings.IntuitiveConsonantTiming && state.SearchPhonemeBoundary(point, out result)) return true;
            if (state.SearchVolume(point, out result)) return true;
            var onset = state.Hit(point);
            if (state._drag == null) state.SetHover(onset, point);
            if (onset == null) return false;
            result = new PianorollView.EmotionModeSearchResults(onset.Visual,
                PianorollView.HitObjectTypeEnum.Velocity, onset.X, onset.Right, true);
            return true;
        }
        catch (Exception exception) { Fail(view, exception); return false; }
    }

    internal static void SourceChanged(WIVSMSequence sequence)
    {
        nint source = (nint)sequence;
        foreach (var pair in States)
        {
            var state = pair.Value;
            void Invalidate()
            {
                if (state._view.DataContext is not MusicalEditorViewModel { VSMSequence: { } current } || (nint)current != source) return;
                state._feedbackRevision = -1;
                state._renderedFeedbackRevision = -1;
                // Parameter-panel edits do not necessarily redraw the piano
                // phoneme canvas. Never leave its old tooltip string open.
                state.SetHover(null);
                state.ClearPhonemeHover();
                state.CloseVolumePanel();
                state.CancelVolumeDrag();
                state.InvalidateDurationContext();
                state.InvalidateTransitionContext();
                state.RequestRefresh();
            }
            if (state._view.Dispatcher.CheckAccess()) Invalidate();
            else state._view.Dispatcher.BeginInvoke(new Action(Invalidate));
        }
    }

    internal static void BackendReady()
    {
        foreach (var pair in States)
        {
            var state = pair.Value;
            void RefreshReady() { state._feedbackRevision = -1; state.RequestRefresh(); }
            if (state._view.Dispatcher.CheckAccess()) RefreshReady();
            else state._view.Dispatcher.BeginInvoke(new Action(RefreshReady));
        }
    }

    internal static bool HasInlineFeedback(PianorollView view, PianorollView.EmotionModeSearchResults result)
    {
        try
        {
            if (States.TryGetValue(view, out var volume) && (volume._volumeHover != null || volume._volumeDrag != null))
                view.Cursor = Cursors.SizeNS;
            if (States.TryGetValue(view, out var duration) && (duration._durationHover != null || duration._durationDrag != null))
                view.Cursor = Cursors.SizeWE;
            return Enabled(view) && result.HitObjectType == PianorollView.HitObjectTypeEnum.Velocity &&
                States.TryGetValue(view, out var state) && (state._drag != null || state._hover != null || state._phonemeDrag != null || state._phonemeHover != null || state._volumeDrag != null || state._volumeHover != null || state._durationDrag != null || state._durationHover != null || state._transitionHover != null || state._transitionDrag != null);
        }
        catch (Exception exception) { Fail(view, exception); return false; }
    }

    private void RequestRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _refreshPending = false;
            try { RefreshCore(); }
            catch (Exception exception) { Fail(_view, exception); }
        }));
    }

    private void RefreshCore()
    {
        if (_refreshing) return;
        if (!Enabled(_view) || !_view.IsLoaded) { Suspend(); return; }
        if (_view.DataContext is not MusicalEditorViewModel vm ||
            NoteCanvasField?.GetValue(_view) is not FastCanvas notes) { Suspend(); return; }
        if (_phonemeDrag is {} phonemeDrag && !PhonemeContextValid(phonemeDrag)) { CancelPhonemeDrag(); return; }
        if (_volumeDrag is { } volumeDrag && !VolumeContextValid(volumeDrag)) { CancelVolumeDrag(); return; }
        if (_drag != null && !DragContextValid())
        {
            Suspend(); // A changed target must not commit into the previous Part.
            return;
        }
        _refreshing = true;
        try
        {
            // Rebind hover to the rebuilt spans below without closing and
            // reopening the same tooltip during every viewport refresh.
            if (_hover != null) _hover.Span.Line.IsHighlight = false;
            _hover = null;
            ClearPhonemeHover();
            _onsets.Clear();
            _phonemeHandles.Clear();
            _gainSpans.Clear();
            BeginDurationRefresh(vm);
            BeginTransitionRefresh(vm);
            RestoreGlyphs();
            _root.Width = Math.Max(_view.ActualWidth, vm.SongWidth);
            _root.Height = Math.Max(_view.ActualHeight, vm.AllKeyHeight);
            var appearance = new Appearance(new Typeface(_view.FontFamily, _view.FontStyle,
                _view.FontWeight, _view.FontStretch), TextBrushField?.GetValue(_view) as Brush ?? _view.Foreground,
                VisualTreeHelper.GetDpi(_view).PixelsPerDip, CultureInfo.CurrentCulture.Name);
            if (appearance != _appearance) { _textCache.Clear(); _appearance = appearance; }
            var viewer = vm.PianorollViewer;
            double left = Math.Max(0, (viewer?.HorizontalOffset ?? 0) - 100);
            double right = (viewer?.HorizontalOffset ?? 0) + (viewer?.ViewportWidth ?? _view.ActualWidth) + 100;
            double top = (viewer?.VerticalOffset ?? 0) - vm.OneKeyHeight * 2;
            double bottom = (viewer?.VerticalOffset ?? 0) + (viewer?.ViewportHeight ?? _view.ActualHeight) + vm.OneKeyHeight * 2;
            var replacedGlyphs = new HashSet<(int Number, double Left, string Phoneme)>();
            int used = 0;
            foreach (var visual in notes.VirtualChildren.OfType<UIEmotionNote>())
            {
                var note = visual.Note;
                if (note == null) continue;
                double y = vm.CalcNoteNumberTopPosition(note.NoteNumber);
                if (y < top || y > bottom) continue;
                // Cull before crossing into native phoneme accessors.
                long first = note.AbsBeginPhonemeTick.Value;
                long last = note.AbsEndPhonemeTick.Value;
                double noteLeft = vm.CalcTickToViewPosition(new VSMAbsTick(
                    first == int.MinValue ? note.AbsPosTick.Value : Math.Min(first, note.AbsPosTick.Value)));
                double noteRight = vm.CalcTickToViewPosition(new VSMAbsTick(
                    last == int.MinValue ? note.AbsEndTick.Value : Math.Max(last, note.AbsEndTick.Value)));
                if (noteRight < left || noteLeft > right) continue;
                AddDurationSpan(visual, vm, y);
                var tokens = note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var positions = note.GetPhonemePositions();
                positions = PendingPositions(vm, note, positions);
                positions = PendingPhonemePositions(vm,note,positions);
                if (tokens.Length == 0 || positions.Count != tokens.Length + 1 || positions.Contains(int.MinValue)) continue;
                if (positions.Zip(positions.Skip(1), (a, b) => a > b).Any(value => value)) continue;
                if (used + tokens.Length + 1 > MaximumVisuals) { Suspend(); return; }
                long[] ticks = positions.Select(p => note.GetAbsPositionFromNoteBaseTick(p).Value).ToArray();
                int vowel = Array.FindIndex(tokens, note.IsVowel);
                bool editable =
                    note.Parent?.Equals(vm.ActivePart) == true && !note.IsMelismaNote;
                double bodyLeft = vm.CalcTickToViewPosition(note.AbsPosTick) + (visual.FramePen?.Thickness ?? 1);
                for (int i = 0; i < tokens.Length; i++)
                {
                    var span = GetVisual(used++);
                    double beginX = vm.CalcTickToViewPosition(new VSMAbsTick(ticks[i]));
                    double endX = vm.CalcTickToViewPosition(new VSMAbsTick(ticks[i + 1]));
                    var line = span.Line;
                    line.VM = vm;
                    line.Note = note;
                    line.NoteNumber = note.NoteNumber;
                    line.AbsPosTick = new VSMAbsTick(ticks[i]);
                    line.EndPosTick = new VSMAbsTick(ticks[i + 1]);
                    line.PhonemePositionIndex = i;
                    // The native note frame starts one pen width inside its
                    // musical tick. Join only the hover fill to that frame;
                    // SignedWidth and all acoustic boundary ticks stay exact.
                    line.Width = Math.Max(0, (i == 0 && editable ? Math.Max(endX, bodyLeft) : endX) - beginX);
                    line.SignedWidth = endX - beginX;
                    line.Height = vm.OneKeyHeight;
                    line.HasEndLine = !editable && i == tokens.Length - 1;
                    line.NormalLinePen = editable ? _editable : _reference;
                    line.HighlightLinePen = line.NormalLinePen;
                    line.DisabledLinePen = _reference;
                    line.HighlightFillBrush = _highlight;
                    line.BuddyPhonemes = span.Label;
                    line.IsHighlight = false;
                    line.Visibility = Visibility.Visible;
                    Canvas.SetLeft(line, beginX);
                    Canvas.SetTop(line, y);
                    line.InvalidateVisual();
                    var label = span.Label;
                    label.Phoneme = tokens[i];
                    if (!_textCache.TryGetValue(tokens[i], out var formatted))
                    {
                        if (_textCache.Count >= 512) _textCache.Clear();
                        formatted = new FormattedText(tokens[i], CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, appearance.Typeface, NoteDesign.lyricFontSize,
                            appearance.Brush, appearance.Dpi);
                        _textCache[tokens[i]] = formatted;
                    }
                    label.FormattedPhoneme = formatted;
                    label.Width = formatted.Width;
                    label.Height = TransitionMode ? Math.Max(6, vm.OneKeyHeight - TransitionBandHeight(vm) - 2) : vm.OneKeyHeight;
                    label.RelPosTick = new VSMRelTick(positions[i]);
                    label.BrushHighlight = _highlight;
                    label.Visibility = Visibility.Visible;
                    Canvas.SetLeft(label, (beginX + endX - formatted.Width) / 2);
                    Canvas.SetTop(label, y);
                    label.InvalidateVisual();
                    if(editable && Settings.IntuitiveConsonantTiming) _phonemeHandles.Add(new(visual,span,i,beginX,y,vm.OneKeyHeight,positions.ToArray()));
                    ConfigureGain(span, visual, i, beginX, endX, y, vm.OneKeyHeight, editable);
                    // Preserve native labels for notes whose timing is unavailable.
                    replacedGlyphs.Add((note.NoteNumber,
                        (note.AbsPosTick.Value + positions[i]) * vm.WidthPerTick, tokens[i]));
                    if (i == 0 && editable && vowel > 0 && ticks[vowel] > ticks[0] && vm.VSMSequence is { } sequence)
                    {
                        double milliseconds = (sequence.GetTimeFromTick(new VSMAbsTick(ticks[vowel])) -
                            sequence.GetTimeFromTick(new VSMAbsTick(ticks[0]))) * 1000;
                        _onsets.Add(new Onset(visual, span, beginX,
                            vm.CalcTickToViewPosition(new VSMAbsTick(ticks[vowel])), y, vm.OneKeyHeight, milliseconds));
                    }
                }
                if(editable && Settings.IntuitiveConsonantTiming)
                {
                    var terminal=GetVisual(used++);
                    ConfigureTerminalBoundary(terminal,visual,vm,ticks[^1],positions[^1],y);
                    terminal.Gain.Visibility = Visibility.Collapsed;
                    _phonemeHandles.Add(new(visual,terminal,tokens.Length,vm.CalcTickToViewPosition(new VSMAbsTick(ticks[^1])),y,vm.OneKeyHeight,positions.ToArray()));
                }
            }
            for (int i = used; i < _pool.Count; i++)
            {
                _pool[i].Line.Visibility = Visibility.Collapsed;
                _pool[i].Label.Visibility = Visibility.Collapsed;
                _pool[i].Gain.Visibility = Visibility.Collapsed;
            }
            EndDurationRefresh();
            RefreshTransitionBands(vm, notes.VirtualChildren.OfType<UIEmotionNote>().ToArray());
            if (used == 0 && _durationSpans.Count == 0 && !Settings.IndependentDurationSelection && !TransitionMode) { Suspend(); return; }
            foreach (var glyph in _nativePhonemes.VirtualChildren.OfType<UIEmotionPhoneme>())
            {
                if (!replacedGlyphs.Contains((glyph.Number, Canvas.GetLeft(glyph), glyph.Phoneme))) continue;
                _hiddenGlyphs[glyph] = glyph.Opacity;
                glyph.Opacity = 0;
            }
            _root.Visibility = Visibility.Visible;
            if (DurationMode) { if (_view.IsMouseOver) SearchDuration(Mouse.GetPosition(_view), out _); }
            else if (TransitionMode) { if (_view.IsMouseOver) SearchTransition(Mouse.GetPosition(_view), out _); }
            else if (_volumeDrag != null) DrawVolumeCandidate();
            else if(_phonemeDrag != null) DrawPhonemeCandidate();
            else if (_drag is { } drag)
            {
                SetHover(_onsets.FirstOrDefault(o => (nint)o.Visual.Note! == (nint)drag.Note));
                DrawCandidate();
            }
            else
            {
                var point = Mouse.GetPosition(_view);
                if (!_view.IsMouseOver) { ClearPhonemeHover(); SetHover(null); }
                else if ((!Settings.IntuitiveConsonantTiming || !SearchPhonemeBoundary(point,out _)) && !SearchVolume(point, out _)) SetHover(Hit(point),point);
            }
        }
        finally { _refreshing = false; }
    }

    private SpanVisual GetVisual(int index)
    {
        while (_pool.Count <= index)
        {
            var span = new SpanVisual(new UIPhonemeTimingBoundaryLine { IsHitTestVisible = false },
                new UIPhonemeTimingPhonemes { IsHitTestVisible = false }, new GainGlyph { IsHitTestVisible = false });
            _root.Children.Add(span.Line);
            _root.Children.Add(span.Label);
            _root.Children.Add(span.Gain);
            _pool.Add(span);
        }
        return _pool[index];
    }

    private Onset? Hit(Point point)
    {
        if (_root.Visibility != Visibility.Visible || !Settings.IntuitiveConsonantTiming || DurationMode) return null;
        return _onsets.Where(o => point.Y >= o.Y - 3 && point.Y <= o.Y + o.Height + 3 &&
                Math.Abs(point.X - o.X) <= 7)
            .OrderBy(o => Math.Abs(point.X - o.X)).FirstOrDefault();
    }

    private void SetHover(Onset? onset, Point point = default)
    {
        if (!ReferenceEquals(onset, _hover))
        {
            if (_hover != null) { _hover.Span.Line.IsHighlight = false; _hover.Span.Line.InvalidateVisual(); }
            _hover = onset;
            if (onset != null) { onset.Span.Line.IsHighlight = true; onset.Span.Line.InvalidateVisual(); }
        }
        if (onset == null) { _tip.IsOpen = false; return; }
        if (_drag != null) return;
        if (onset.Visual.Note is not { } note)
        {
            _tip.IsOpen = false;
            RequestRefresh();
            return;
        }
        int velocity = note.NoteVelocity;
        double milliseconds = onset.Milliseconds;
        long revision = ConsonantTimingRuntime.Revision;
        bool cached = _feedbackNote == (nint)note && _feedbackVelocity == velocity && _feedbackRevision == revision;
        if (cached && (_feedbackMessage != "VOCALOIDPatcher_ConsonantTiming_Refreshing" ||
            System.Diagnostics.Stopwatch.GetTimestamp() < _feedbackRetryAt))
        {
            if (_feedbackMessage != null) { ShowTip(TranslationManager.Tr(_feedbackMessage, velocity), point); return; }
            milliseconds = _feedbackMilliseconds;
        }
        else if (_view.DataContext is MusicalEditorViewModel { VSMSequence: { IsStaged: false } sequence } &&
            ConsonantTimingRuntime.Backend is { } backend)
        {
            _feedbackNote = (nint)note; _feedbackVelocity = velocity; _feedbackRevision = revision;
            try
            {
                long[] boundaries;
                var session = SampledTimingHost.Find(sequence);
                bool rendered = _renderedFeedbackRevision == revision && _renderedFeedbackPart == (nint)note.Parent! &&
                    note.Parent?.HasValidRenderedScore == true;
                if (rendered) boundaries = note.GetPhonemePositions().Select(v => (long)v).ToArray();
                else if (session != null)
                {
                    using var prepared = session.Prepare(note);
                    var plan = session.Document.Get(SampledTimingVsmBinding.Address(note));
                    boundaries = plan == null ? prepared.Query(velocity == 127 ? 1 : velocity / 128f, session.Revision, nativeWaitBudgetMs: 0).Boundaries
                        : prepared.Query(plan, session.Revision, nativeWaitBudgetMs: 0).Boundaries;
                }
                else boundaries = backend.Query(note, velocity, nativeWaitBudgetMs: 0).Boundaries;
                int vowel = Array.FindIndex(note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries), note.IsVowel);
                if (vowel <= 0 || vowel >= boundaries.Length - 1) throw new NotSupportedException("Missing current consonant timing.");
                long tick = note.AbsPosTick.Value;
                milliseconds = 1000 * (sequence.GetTimeFromTick(new VSMAbsTick(tick + boundaries[vowel])) -
                    sequence.GetTimeFromTick(new VSMAbsTick(tick + boundaries[0])));
                _feedbackMilliseconds = milliseconds; _feedbackMessage = null;
                // Parameter-panel commits need the same verified display
                // bridge as a commit from the upper timing handle.
                if (!rendered && session == null && !note.GetPhonemePositions().Select(v => (long)v).SequenceEqual(boundaries))
                {
                    _pending = new PendingTiming((nint)sequence, (nint)note.Parent!, (nint)note, velocity, boundaries);
                    RequestRefresh();
                }
            }
            catch (Exception e) when (e is TimeoutException or NotSupportedException or InvalidOperationException)
            {
                _feedbackMessage = e is NotSupportedException ? "VOCALOIDPatcher_ConsonantTiming_UnavailableValue" :
                    "VOCALOIDPatcher_ConsonantTiming_Refreshing";
                _feedbackRetryAt = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 30;
                ShowTip(TranslationManager.Tr(_feedbackMessage, velocity), point);
                return;
            }
        }
        else
        {
            ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Refreshing", velocity), point);
            return;
        }
        ShowTip(TranslationManager.Tr("VOCALOIDPatcher_ConsonantTiming_Value",
            milliseconds.ToString("0.#", CultureInfo.CurrentCulture), velocity), point);
    }

    private void ShowTip(string text, Point point)
    {
        _tip.Content = text;
        _tip.HorizontalOffset = point.X + 14;
        _tip.VerticalOffset = point.Y + 7;
        _tip.IsOpen = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // The mode button owns its normal WPF input. Song tools must not
            // interpret that click as an emotion gesture underneath it.
            if (e.OriginalSource is DependencyObject original && HitAcousticTool(original))
            { e.Handled = true; return; }
            if (e.OriginalSource is DependencyObject panelTarget && _transitionPanelTool != null &&
                (_transitionPanelTool == panelTarget || _transitionPanelTool.IsAncestorOf(panelTarget)))
            { e.Handled = true; OpenTransitionPanel(e.GetPosition(_view)); return; }
            if (Enabled(_view) && TransitionMode)
            { e.Handled = BeginTransitionDrag(e.GetPosition(_view), e.ClickCount); return; }
            if (Enabled(_view) && DurationMode)
            {
                if (BeginDurationDrag(e.GetPosition(_view), e.ClickCount)) e.Handled = true;
                return;
            }
            if (Enabled(_view) && Settings.IntuitiveConsonantTiming && TryBeginPhonemeDrag(e.GetPosition(_view),e.ClickCount)) { e.Handled=true; return; }
            if (Enabled(_view) && TryBeginVolumeDrag(e.GetPosition(_view))) { e.Handled=true; return; }
            if (Enabled(_view) && _drag == null && e.ClickCount == 1 &&
                (Keyboard.Modifiers & ModifierKeys.Control) == 0 && TryBeginDrag(e.GetPosition(_view)))
            {
                e.Handled = true;
                return;
            }
            if (!Enabled(_view) || _drag != null ||
                ((Keyboard.Modifiers & ModifierKeys.Control) == 0 && e.ClickCount != 2) ||
                Hit(e.GetPosition(_view)) is not { } onset || onset.Visual.Note is not { } note ||
                _view.DataContext is not MusicalEditorViewModel vm || vm.VSMSequence is not { } sequence ||
                sequence.IsStaged || _view.IsMouseCaptured)
                return;
            e.Handled = true;
            if (note.NoteVelocity == WIVSMNote.DefaultVelocity) return;
            using var transaction = new Transaction(sequence);
            transaction.Result = false;
            note.NoteVelocity = WIVSMNote.DefaultVelocity;
            transaction.Result = note.NoteVelocity == WIVSMNote.DefaultVelocity;
        }
        catch (Exception exception) { Fail(_view, exception); }
    }

    private void Suspend()
    {
        InvalidateDurationContext(); _durationTools.Visibility = Visibility.Collapsed;
        InvalidateTransitionContext();
        foreach (var glyph in _durationPool) glyph.Visibility = Visibility.Collapsed;
        _durationSpans.Clear();
        CloseVolumePanel(); CancelVolumeDrag(); _gainSpans.Clear(); _volumeHover = null;
        CancelPhonemeDrag();
        _phonemeHandles.Clear(); _phonemeHover=null; _phonemePending=null;
        CancelDrag();
        _pending = null;
        _root.Visibility = Visibility.Collapsed;
        SetHover(null);
        _onsets.Clear();
        RestoreGlyphs();
    }

    private void RestoreGlyphs()
    {
        foreach (var pair in _hiddenGlyphs) pair.Key.Opacity = pair.Value;
        _hiddenGlyphs.Clear();
    }

    private static void Fail(PianorollView view, Exception exception)
    {
        Settings.DisableFeature(Settings.IntuitiveConsonantTimingKey);
        Settings.DisableFeature(Settings.IndividualPhonemeVolumeKey);
        Settings.DisableFeature(Settings.IndependentDurationSelectionKey);
        Settings.DisableFeature(Settings.IndividualTransitionControlKey);
        try { if (States.TryGetValue(view, out var state)) state.Suspend(); }
        catch { }
        Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_ConsonantTiming_Failed", exception.GetType().Name));
    }
}
