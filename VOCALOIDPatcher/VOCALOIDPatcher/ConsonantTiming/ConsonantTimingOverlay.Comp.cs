using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.VoiceComp;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private CompEditorPanel? _compPanel;
    private WIVSMNote? _compNote;
    private long _compNoteGeneration;
    private bool _compCollapsed, _compPicking;
    private nint _compHovered;
    private CompAction? _compPreferredAction;
    private double _compHeight = 272;
    private readonly CompMarkers _compMarkers = new() { IsHitTestVisible = false };
    private bool CompMode => Settings.VoiceCompEnabled && _acousticMode == AcousticEditMode.Comp;
    private void AttachCompPanel()
    {
        if (_compPanel != null) return;
        _compPanel = new CompEditorPanel { Visibility = Visibility.Collapsed };
        _compPanel.CollapseRequested += () => { _compCollapsed = true; _compPanel.Visibility = Visibility.Collapsed; };
        _compPanel.PickSourceRequested += () => { _compPicking = true; _compPanel.PickingSource(); };
        _view.PreviewKeyDown += (_, e) =>
        { if (e.Key == Key.Escape && _compPicking) { _compPicking = false; RequestRefresh(); e.Handled = true; } };
        _compPanel.HeightRequested += height => { _compHeight = height; RequestRefresh(); };
        _compPanel.GestureChanged += active =>
        { if (_view.DataContext is MusicalEditorViewModel vm) vm.EditorMode.IsIdleMouseOperation = !active; };
        _view.PreviewMouseMove += (_, e) =>
        {
            // Hit the already captured visible badges; do not traverse native
            // notes or request a refresh for each mouse movement.
            var point = e.GetPosition(_root);
            nint hovered = _compMarkers.Items.FirstOrDefault(item => item.Rect.Contains(point)).Id;
            if (hovered == _compHovered) return;
            _compHovered = hovered; RequestRefresh();
        };
        _view.MouseLeave += (_, _) => { if (_compHovered != 0) { _compHovered = 0; RequestRefresh(); } };
        _acousticToolsLayer.Children.Add(_compPanel);
        _root.Children.Add(_compMarkers);
        if (AccessTools.Field(typeof(PianorollView), "xContextMenu")?.GetValue(_view) is ContextMenu menu)
        {
            var group = new MenuItem { Header = CompEditorPanel.Text("Mode"), Visibility = Visibility.Collapsed };
            foreach (var action in Enum.GetValues<CompAction>())
            { var item = new MenuItem { Header = CompEditorPanel.Text(action.ToString()) }; item.Click += (_, _) => TryCompAction(action); group.Items.Add(item); }
            menu.Items.Add(group);
            _view.ContextMenuOpening += (_, _) =>
            {
                bool active = Enabled(_view) && Settings.VoiceCompEnabled && _view.DataContext is MusicalEditorViewModel { ActivePart.IsAi: false };
                group.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            };
        }
        // Capture only a weak reference in the process-wide notification list.
        var weak = new WeakReference<ConsonantTimingOverlay>(this);
        VoiceCompHost.Changed += () => { if (weak.TryGetTarget(out var state)) state.RequestRefresh(); };
    }
    private bool HitCompControl(DependencyObject target) => _compPanel?.IsAncestorOf(target) == true || target == _compPanel;
    private void RefreshComp(MusicalEditorViewModel vm)
    {
        AttachCompPanel();
        var marks = new List<(Rect, string, nint)>();
        if (Settings.VoiceCompEnabled && vm.ActivePart is { IsAi: false } activePart)
        {
            double left = vm.PianorollViewer?.HorizontalOffset ?? 0, right = left + (vm.PianorollViewer?.ViewportWidth ?? _view.ActualWidth);
            foreach (var note in activePart.Notes)
            {
                var plan = VoiceCompHost.Get(note); if (plan == null) continue;
                double x = vm.CalcTickToViewPosition(note.AbsEndTick); if (x < left || x > right) continue;
                string badge = plan.Bypass ? "○" : plan.Consonant != null ? "⋈" : plan.Body != null ? "⌑" : "↦";
                double badgeWidth = 14;
                double noteWidth = x - vm.CalcTickToViewPosition(note.AbsPosTick);
                if ((note.IsSelected || (nint)note == _compHovered) && noteWidth > 90)
                {
                    string name = CompEditorPanel.Text(plan.Bypass ? "Bypass" : plan.Consonant != null ? "PhonemeSplice" : plan.Body != null ? "LongSlice" : "IndependentSustain");
                    badge += " " + name; badgeWidth = Math.Min(noteWidth, 18 + name.Length * 12);
                }
                marks.Add((new Rect(x - badgeWidth, vm.CalcNoteNumberTopPosition(note.NoteNumber), badgeWidth, Math.Min(15, vm.OneKeyHeight)), badge, (nint)note));
                if (marks.Count >= MaximumVisuals) break;
            }
        }
        _compMarkers.Width = _root.Width; _compMarkers.Height = _root.Height; _compMarkers.Items = marks; _compMarkers.InvalidateVisual();
        if (!CompMode || vm.ActivePart is not { IsAi: false } part)
        { _compPanel!.Visibility = Visibility.Collapsed; _compPicking = false; return; }
        bool valid = _compNote != null && BreathVolumeService.IsNoteGenerationActive(_compNote.CppObjPtr, _compNoteGeneration) &&
            part.Notes.Any(n => (nint)n == (nint)_compNote);
        var selected = part.Notes.FirstOrDefault(n => n.IsSelected);
        if (!_compPicking && selected != null && (!valid || (nint)selected != (nint)_compNote!))
        { _compNote = selected; _compNoteGeneration = BreathVolumeService.NoteGeneration(selected); valid = true; _compCollapsed = false; }
        if (!valid) _compNote = null;
        if (_compCollapsed) { _compPanel!.Visibility = Visibility.Collapsed; return; }
        double width = Math.Max(0, (vm.PianorollViewer?.ViewportWidth ?? _view.ActualWidth) - 20);
        double viewport = vm.PianorollViewer?.ViewportHeight ?? _view.ActualHeight;
        _compPanel!.Width = width; _compPanel.Height = Math.Clamp(_compHeight, 200, Math.Max(200, Math.Min(360, viewport - 58)));
        Canvas.SetLeft(_compPanel, (vm.PianorollViewer?.HorizontalOffset ?? 0) + 10);
        Canvas.SetTop(_compPanel, (vm.PianorollViewer?.VerticalOffset ?? 0) + Math.Max(48, viewport - _compPanel.Height - 10));
        _compPanel.Show(_compNote);
        if (_compPreferredAction is { } action) { _compPanel.FocusAction(action); _compPreferredAction = null; }
    }
    private bool CompClick(Point point)
    {
        if (!CompMode || _view.DataContext is not MusicalEditorViewModel vm || vm.ActivePart is not { } part) return false;
        var visual = (NoteCanvasField?.GetValue(_view) as FastCanvas)?.VirtualChildren.OfType<UIEmotionNote>().FirstOrDefault(v =>
        {
            if (v.Note is not { } candidate) return false;
            double left = vm.CalcTickToViewPosition(candidate.AbsPosTick), right = vm.CalcTickToViewPosition(candidate.AbsEndTick);
            return point.X >= left - 4 && point.X <= right + 4 && point.Y >= vm.CalcNoteNumberTopPosition(candidate.NoteNumber) &&
                point.Y <= vm.CalcNoteNumberTopPosition(candidate.NoteNumber) + vm.OneKeyHeight;
        });
        if (visual?.Note is not { } note) return true;
        if (_compPicking)
        {
            if (_compNote != null && BreathVolumeService.IsNoteGenerationActive(_compNote.CppObjPtr, _compNoteGeneration)) _compPanel?.UseSource(note);
            _compPicking = false; return true;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) foreach (var other in part.Notes) other.IsSelected = false;
        note.IsSelected = true; _compNote = note; _compNoteGeneration = BreathVolumeService.NoteGeneration(note); _compCollapsed = false;
        RequestRefresh(); return true;
    }
    private void TryCompAction(CompAction action)
    {
        try { ApplyCompAction(action); }
        catch (Exception error) { _compPanel?.ShowError(error); Utils.Debug.Print("Voice-comp edit: " + error.GetType().Name); }
    }
    private void ApplyCompAction(CompAction action)
    {
        if (_view.DataContext is not MusicalEditorViewModel vm || vm.ActivePart is not { IsAi: false } part) return;
        var notes = part.Notes.Where(n => n.IsSelected).ToArray();
        if (notes.Length == 0 && _compNote != null && BreathVolumeService.IsNoteGenerationActive(_compNote.CppObjPtr, _compNoteGeneration) &&
            part.Notes.Any(n => (nint)n == (nint)_compNote)) notes = [_compNote];
        if (notes.Length == 0) return;
        VoiceCompHost.SetMany(notes, before => (before ?? new()).WithAction(action));
        _compNote = notes[0]; _compNoteGeneration = BreathVolumeService.NoteGeneration(notes[0]); _compCollapsed = false;
        _compPreferredAction = action;
        SetAcousticMode(AcousticEditMode.Comp); RequestRefresh();
    }
    private sealed class CompMarkers : FrameworkElement
    {
        internal IReadOnlyList<(Rect Rect, string Text, nint Id)> Items = [];
        private readonly Brush _background = CompEditorPanel.Brush("#203F54"), _foreground = CompEditorPanel.Brush("#D6EFFF");
        protected override void OnRender(DrawingContext dc)
        {
            foreach (var item in Items)
            {
                dc.DrawRoundedRectangle(_background, null, item.Rect, 3, 3);
                var text = new FormattedText(item.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Symbol"), 11, _foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, item.Rect.Width - 4), Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new Point(item.Rect.Left + 2, item.Rect.Top));
            }
        }
    }
}
