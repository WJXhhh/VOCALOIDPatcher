using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.DurationSelection;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private sealed class DurationGlyph : FrameworkElement
    {
        internal float Ratio = 1;
        internal DurationCandidatePolicy Policy;
        internal bool Failed;
        private static readonly Pen Rail = FrozenPen(Brushes.SlateGray, 1);
        private static readonly Pen Native = FrozenPen(Brushes.LightSkyBlue, 1.5);
        private static readonly Pen Experimental = FrozenPen(Brushes.Goldenrod, 1.5);
        private static readonly Pen Error = FrozenPen(Brushes.IndianRed, 1.5);
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 8 || ActualHeight < 6) return;
            double y = ActualHeight - 4, width = ActualWidth - 6;
            var pen = Failed ? Error : Policy == DurationCandidatePolicy.Native ? Native : Experimental;
            dc.DrawLine(Rail, new(3, y), new(3 + width, y));
            dc.DrawLine(Rail, new(3 + width / 2, y - 3), new(3 + width / 2, y + 2));
            double x = 3 + width * Math.Clamp((Math.Log2(Ratio) + 2) / 4, 0, 1);
            dc.DrawEllipse(pen.Brush, null, new(x, y), 2.5, 2.5);
            if (Failed)
            {
                dc.DrawLine(pen, new(ActualWidth - 7, y - 4), new(ActualWidth - 1, y + 2));
                dc.DrawLine(pen, new(ActualWidth - 7, y + 2), new(ActualWidth - 1, y - 4));
            }
        }
    }
    private sealed record DurationSpan(UIEmotionNote Visual, DurationGlyph Glyph, double X, double Right, double Y, double Height);
    private sealed class DurationContext(WIVSMNote note, MusicalEditorViewModel vm)
    {
        internal readonly WIVSMNote Note = note;
        internal readonly MusicalEditorViewModel VM = vm;
        internal readonly DurationNoteKey Key = new(note.CppObjPtr, BreathVolumeService.NoteGeneration(note));
        internal readonly TimingNoteAddress Address = SampledTimingVsmBinding.Address(note);
        internal long Revision = SampledTimingHost.ForDuration(note.Sequence!).Revision;
        internal readonly double Milliseconds = 1000 * (note.Sequence!.GetTimeFromTick(note.AbsEndTick) - note.Sequence.GetTimeFromTick(note.AbsPosTick));
    }
    private sealed class DurationDrag(DurationSpan span, DurationContext context, Point down, DurationSelectionPlan original)
    {
        internal readonly DurationSpan Span = span;
        internal readonly DurationContext Context = context;
        internal readonly Point Down = down;
        internal readonly DurationSelectionPlan Original = original;
        internal float Candidate = original.Ratio;
        internal bool Moved;
    }
    private readonly List<DurationGlyph> _durationPool = new();
    private readonly List<DurationSpan> _durationSpans = new();
    private readonly Canvas _durationTools = new() { Visibility = Visibility.Collapsed };
    private ToggleButton? _durationTool;
    private ToggleButton? _phonemeTool, _transitionTool;
    private readonly WrapPanel _editTools = new() { Orientation = Orientation.Horizontal };
    private enum AcousticEditMode { Phonemes, Transitions, Duration }
    private AcousticEditMode _acousticMode;
    private bool _changingAcousticMode;
    private DurationSpan? _durationHover;
    private DurationDrag? _durationDrag;
    private Popup? _durationPanel;
    private int _durationCommitId;
    private bool _durationApplying;
    private bool DurationMode => Settings.IndependentDurationSelection && _acousticMode == AcousticEditMode.Duration;
    private bool TransitionMode => Settings.IndividualTransitionControl && _acousticMode == AcousticEditMode.Transitions;
    private static string DurationText(string suffix, params object[] args) => TranslationManager.Tr("VOCALOIDPatcher_DurationSelection_" + suffix, args);

    private void AttachDurationTool(Grid panel)
    {
        _durationTool = new ToggleButton { Content = DurationText("Tool"), Padding = new Thickness(10, 3, 10, 3),
            ToolTip = DurationText("ToolHint"), Focusable = false };
        _phonemeTool = new ToggleButton { Content = TransitionText("Phonemes"), Padding = new Thickness(10, 3, 10, 3), IsChecked = true, Focusable = false };
        _transitionTool = new ToggleButton { Content = TransitionText("Tool"), ToolTip = TransitionText("Hint"), Padding = new Thickness(10, 3, 10, 3), Focusable = false };
        _editTools.Children.Add(_phonemeTool); _editTools.Children.Add(_transitionTool); _editTools.Children.Add(_durationTool);
        _durationTools.Children.Add(_editTools);
        Panel.SetZIndex(_durationTools, 100);
        panel.Children.Add(_durationTools);
        _durationTool.Checked += (_, _) => SetAcousticMode(AcousticEditMode.Duration);
        _durationTool.Unchecked += (_, _) => { if (!_changingAcousticMode) SetAcousticMode(AcousticEditMode.Phonemes); };
        _phonemeTool.Checked += (_, _) => SetAcousticMode(AcousticEditMode.Phonemes);
        _transitionTool.Checked += (_, _) => SetAcousticMode(AcousticEditMode.Transitions);
        _transitionTool.Unchecked += (_, _) => { if (!_changingAcousticMode) SetAcousticMode(AcousticEditMode.Phonemes); };
        if (AccessTools.Field(typeof(PianorollView), "xContextMenu")?.GetValue(_view) is not ContextMenu menu) return;
        var item = new MenuItem { Header = DurationText("Name") + "…", Visibility = Visibility.Collapsed };
        DurationSpan? target = null;
        Point anchor = default;
        _view.ContextMenuOpening += (_, _) =>
        {
            anchor = Mouse.GetPosition(_view);
            target = Enabled(_view) && Settings.IndependentDurationSelection ? HitDuration(anchor) : null;
            item.Visibility = target != null ? Visibility.Visible : Visibility.Collapsed;
        };
        item.Click += (_, _) => { if (target != null) OpenDurationPanel(target, anchor); };
        menu.Items.Add(item);
    }

    private void SetAcousticMode(AcousticEditMode mode)
    {
        if (_changingAcousticMode) return;
        _changingAcousticMode = true;
        try
        {
            _acousticMode = mode;
            if (_durationTool != null) _durationTool.IsChecked = mode == AcousticEditMode.Duration;
            if (_transitionTool != null) _transitionTool.IsChecked = mode == AcousticEditMode.Transitions;
            if (_phonemeTool != null) _phonemeTool.IsChecked = mode == AcousticEditMode.Phonemes;
            InvalidateDurationContext(); InvalidateTransitionContext(); CancelVolumeDrag(); CloseVolumePanel();
            CancelPhonemeDrag(); CancelDrag(); _durationHover = null; _tip.IsOpen = false;
            ClearPhonemeHover(); SetHover(null); RequestRefresh();
        }
        finally { _changingAcousticMode = false; }
    }
    private bool HitAcousticTool(DependencyObject target)
    {
        foreach (var (button, mode) in new[] { (_phonemeTool, AcousticEditMode.Phonemes), (_transitionTool, AcousticEditMode.Transitions), (_durationTool, AcousticEditMode.Duration) })
            if (button != null && (button == target || button.IsAncestorOf(target)))
            { SetAcousticMode(mode == _acousticMode ? AcousticEditMode.Phonemes : mode); return true; }
        return false;
    }

    private void BeginDurationRefresh(MusicalEditorViewModel vm)
    {
        _durationSpans.Clear(); _durationHover = null;
        _durationTools.Width = Math.Max(vm.SongWidth, _view.ActualWidth);
        _durationTools.Height = Math.Max(vm.AllKeyHeight, _view.ActualHeight);
        _durationTools.Visibility = (Settings.IndependentDurationSelection || Settings.IndividualTransitionControl) && vm.ActivePart?.IsAi == false ? Visibility.Visible : Visibility.Collapsed;
        if (_durationTool != null)
        {
            _durationTool.Visibility = Settings.IndependentDurationSelection ? Visibility.Visible : Visibility.Collapsed;
            if (_transitionTool != null) _transitionTool.Visibility = Settings.IndividualTransitionControl ? Visibility.Visible : Visibility.Collapsed;
            if ((!Settings.IndividualTransitionControl && _acousticMode == AcousticEditMode.Transitions) ||
                (!Settings.IndependentDurationSelection && _acousticMode == AcousticEditMode.Duration)) SetAcousticMode(AcousticEditMode.Phonemes);
            Canvas.SetLeft(_editTools, (vm.PianorollViewer?.HorizontalOffset ?? 0) + 10);
            Canvas.SetTop(_editTools, (vm.PianorollViewer?.VerticalOffset ?? 0) + 8);
            _editTools.Width = Math.Max(100, (vm.PianorollViewer?.ViewportWidth ?? _view.ActualWidth) - 20);
        }
    }
    private void AddDurationSpan(UIEmotionNote visual, MusicalEditorViewModel vm, double y)
    {
        if (!Settings.IndependentDurationSelection || visual.Note is not { Parent.IsAi: false } note ||
            !note.Parent.Equals(vm.ActivePart) || note.IsMelismaNote || string.IsNullOrWhiteSpace(note.Phonemes) || note.Phonemes.Length > 60 ||
            _durationSpans.Count >= MaximumVisuals) return;
        int index = _durationSpans.Count;
        if (index == _durationPool.Count)
        {
            var added = new DurationGlyph { IsHitTestVisible = false };
            _durationPool.Add(added); _root.Children.Add(added);
        }
        var glyph = _durationPool[index];
        var plan = DurationSelectionHost.Get(note);
        double x = vm.CalcTickToViewPosition(note.AbsPosTick), right = vm.CalcTickToViewPosition(note.AbsEndTick);
        glyph.Ratio = _durationDrag is { } drag && drag.Context.Key.Handle == note.CppObjPtr ? drag.Candidate : plan.Ratio;
        glyph.Policy = plan.Policy; glyph.Failed = DurationSelectionHost.Error(note.Sequence!) != null;
        glyph.Width = Math.Max(0, right - x); glyph.Height = vm.OneKeyHeight;
        glyph.Visibility = DurationMode ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(glyph, x); Canvas.SetTop(glyph, y); glyph.InvalidateVisual();
        _durationSpans.Add(new(visual, glyph, x, right, y, vm.OneKeyHeight));
    }
    private void EndDurationRefresh()
    {
        for (int i = _durationSpans.Count; i < _durationPool.Count; i++) _durationPool[i].Visibility = Visibility.Collapsed;
        if (_durationDrag is { } drag && (!DurationValid(drag.Context) ||
            !_durationSpans.Any(s => s.Visual.Note?.CppObjPtr == drag.Context.Key.Handle))) CancelDurationDrag();
    }
    private DurationSpan? HitDuration(Point point) => _durationSpans.FirstOrDefault(s =>
        point.X >= s.X && point.X <= s.Right && point.Y >= s.Y && point.Y <= s.Y + s.Height);
    private bool SearchDuration(Point point, out PianorollView.EmotionModeSearchResults result)
    {
        result = default;
        if (!DurationMode) return false;
        var span = _durationDrag?.Span ?? HitDuration(point); _durationHover = span;
        if (span?.Visual.Note is not { } note) { _tip.IsOpen = false; return false; }
        var plan = DurationSelectionHost.Get(note);
        double ms = 1000 * (note.Sequence!.GetTimeFromTick(note.AbsEndTick) - note.Sequence.GetTimeFromTick(note.AbsPosTick));
        ShowDurationTip(_durationDrag?.Candidate ?? plan.Ratio, ms, plan.Policy, point);
        if (_durationDrag == null && DurationSelectionHost.Error(note.Sequence!) is { } error) ShowTip(DurationText("Failed", error), point);
        _view.Cursor = Cursors.SizeWE;
        result = new(span.Visual, PianorollView.HitObjectTypeEnum.Velocity, span.X, span.Right, false); return true;
    }
    private void ShowDurationTip(float ratio, double ms, DurationCandidatePolicy policy, Point point) => ShowTip(
        DurationText("Tip", (ratio * 100).ToString("0.###", CultureInfo.CurrentCulture),
            (ms * ratio).ToString("0.#", CultureInfo.CurrentCulture), ms.ToString("0.#", CultureInfo.CurrentCulture),
            DurationText(policy == DurationCandidatePolicy.Native ? "Native" : "Expanded")), point);
    private bool DurationValid(DurationContext context) => Settings.IndependentDurationSelection && Enabled(_view) &&
        ReferenceEquals(context.VM, _view.DataContext) && context.VM.VSMSequence is { IsOpen: true, IsStaged: false } source &&
        context.Note.Parent?.Equals(context.VM.ActivePart) == true &&
        BreathVolumeService.IsNoteGenerationActive(context.Key.Handle, context.Key.Generation) &&
        SampledTimingHost.Find(source)?.Revision == context.Revision && SampledTimingVsmBinding.Address(context.Note) == context.Address;

    private bool BeginDurationDrag(Point point, int clicks)
    {
        if (!DurationMode || HitDuration(point) is not { } span) return false;
        if (_view.DataContext is not MusicalEditorViewModel vm || vm.VSMSequence?.IsStaged != false || _view.IsMouseCaptured) return true;
        CloseDurationPanel();
        _durationCommitId++;
        try
        {
            var context = new DurationContext(span.Visual.Note!, vm);
            if (clicks > 1) { QueueDurationCommit(context, new(context.Note.Phonemes), point); return true; }
            _durationDrag = new(span, context, point, DurationSelectionHost.Get(context.Note));
            if (!_view.CaptureMouse()) { _durationDrag = null; return true; }
            vm.EditorMode.IsIdleMouseOperation = false; _view.Cursor = Cursors.SizeWE;
        }
        catch (Exception error) { DurationFailure(error, point); }
        return true;
    }
    private void MoveDurationDrag(MouseEventArgs e)
    {
        if (_durationDrag is not { } drag) return;
        e.Handled = true;
        if (e.LeftButton != MouseButtonState.Pressed || !DurationValid(drag.Context)) { CancelDurationDrag(); return; }
        Point point = e.GetPosition(_view);
        if (!drag.Moved && Math.Abs(point.X - drag.Down.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        drag.Moved = true;
        // Relative logarithmic movement preserves a saved off-scale value until
        // an actual drag. The rail is a ratio scale, never a musical boundary.
        drag.Candidate = (float)Math.Clamp(drag.Original.Ratio * Math.Pow(2, 4 * (point.X - drag.Down.X) / Math.Max(48, drag.Span.Right - drag.Span.X)), .25, 4);
        drag.Span.Glyph.Ratio = drag.Candidate; drag.Span.Glyph.InvalidateVisual();
        ShowDurationTip(drag.Candidate, drag.Context.Milliseconds, drag.Original.Policy, point);
    }
    private void EndDurationDrag(MouseButtonEventArgs e)
    {
        if (_durationDrag is not { } drag) return;
        e.Handled = true; bool valid = DurationValid(drag.Context); Point point = e.GetPosition(_view);
        if (drag.Moved)
            drag.Candidate = (float)Math.Clamp(drag.Original.Ratio * Math.Pow(2, 4 * (point.X - drag.Down.X) / Math.Max(48, drag.Span.Right - drag.Span.X)), .25, 4);
        CancelDurationDrag();
        if (!valid) return;
        if (drag.Moved) QueueDurationCommit(drag.Context, new(drag.Original.Topology, drag.Candidate, drag.Original.Policy), point);
        else OpenDurationPanel(drag.Span, point);
    }
    private void CancelDurationDrag()
    {
        if (_durationDrag is not { } drag) return;
        _durationDrag = null; _durationHover = null; drag.Context.VM.EditorMode.IsIdleMouseOperation = true;
        _tip.IsOpen = false; _view.Cursor = null;
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        RequestRefresh();
    }
    private void QueueDurationCommit(DurationContext context, DurationSelectionPlan plan, Point point, Action? success = null, Action<Exception>? failed = null)
    {
        int request = ++_durationCommitId;
        ShowTip(DurationText("Planning"), point);
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (request != _durationCommitId || !DurationValid(context)) return;
            try
            {
                _durationApplying = true;
                DurationSelectionHost.Set(context.Note, plan);
                context.Revision = SampledTimingHost.Find(context.Note.Sequence!)!.Revision;
                _tip.IsOpen = false; success?.Invoke(); RequestRefresh();
            }
            catch (Exception error) { DurationFailure(error, point); failed?.Invoke(error); }
            finally { _durationApplying = false; }
        }));
    }
    private void DurationFailure(Exception error, Point point)
    {
        CancelDurationDrag();
        ShowTip(DurationText("Failed", error.Message), point);
        Utils.Debug.Print("Independent duration edit rejected: " + error.GetType().Name);
    }
    private void InvalidateDurationContext()
    {
        if (_durationApplying) return;
        _durationCommitId++; CloseDurationPanel(); CancelDurationDrag(); _durationHover = null;
    }
    private void CloseDurationPanel() { if (_durationPanel != null) _durationPanel.IsOpen = false; _durationPanel = null; _updateDurationPanelStatus = null; }

    private Action? _updateDurationPanelStatus;
    internal static void DurationChanged()
    {
        foreach (var pair in States)
        {
            var state = pair.Value;
            void Update() { state._updateDurationPanelStatus?.Invoke(); state.RequestRefresh(); }
            if (state._view.Dispatcher.CheckAccess()) Update();
            else state._view.Dispatcher.BeginInvoke(new Action(Update));
        }
    }
    private void OpenDurationPanel(DurationSpan span, Point point)
    {
        CloseDurationPanel(); CloseVolumePanel();
        if (_view.DataContext is not MusicalEditorViewModel vm || span.Visual.Note is not { } note) return;
        try
        {
            var context = new DurationContext(note, vm);
            if (!DurationValid(context)) return;
            var content = new DurationSelectionEditor();
            var popup = new Popup { PlacementTarget = _view, Placement = PlacementMode.Relative, StaysOpen = false, AllowsTransparency = true };
            var viewer = vm.PianorollViewer;
            double left = viewer?.HorizontalOffset ?? 0, top = viewer?.VerticalOffset ?? 0;
            double width = viewer?.ViewportWidth ?? _view.ActualWidth, height = viewer?.ViewportHeight ?? _view.ActualHeight;
            popup.Child = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 42)), BorderBrush = Brushes.SlateGray,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = new ScrollViewer { Content = content,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = Math.Max(100, height - 8) } };
            popup.HorizontalOffset = Math.Clamp(point.X + 12, left, Math.Max(left, left + width - 320));
            popup.VerticalOffset = Math.Clamp(point.Y + 10, top, Math.Max(top, top + height - 330));
            void Update()
            {
                if (!DurationValid(context)) { popup.IsOpen = false; return; }
                content.Show(DurationSelectionHost.Get(note), context.Milliseconds);
                _updateDurationPanelStatus?.Invoke();
            }
            content.ApplyRequested += (ratio, policy) =>
            {
                try
                {
                    content.Status.Text = DurationText("Planning");
                    QueueDurationCommit(context, new(note.Phonemes, ratio, policy), point, Update,
                        error => content.Status.Text = DurationText("Failed", error.Message));
                }
                catch (Exception error) { content.Status.Text = DurationText("Failed", error.Message); }
            };
            content.CancelRequested += () => { _durationCommitId++; popup.IsOpen = false; };
            popup.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_durationPanel, popup)) return;
                _durationCommitId++; _durationPanel = null; _updateDurationPanelStatus = null;
            };
            _updateDurationPanelStatus = () =>
            {
                if (!DurationValid(context)) { if (!_durationApplying) popup.IsOpen = false; return; }
                if (DurationSelectionHost.Error(note.Sequence!) is { } error) content.Status.Text = DurationText("Failed", error);
            };
            _durationPanel = popup; Update(); popup.IsOpen = true;
            _view.Dispatcher.BeginInvoke(new Action(() => { if (popup.IsOpen) { content.Percent.Focus(); content.Percent.SelectAll(); } }));
        }
        catch (Exception error) { DurationFailure(error, point); }
    }
}
