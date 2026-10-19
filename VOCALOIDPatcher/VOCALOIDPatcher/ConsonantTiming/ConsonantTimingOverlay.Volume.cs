using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.PhonemeVolume;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private sealed class GainGlyph : FrameworkElement
    {
        internal float Decibels;
        private static readonly Pen Normal = FrozenPen(Brushes.CadetBlue, 1);
        private static readonly Pen Edited = FrozenPen(Brushes.LightSkyBlue, 1.5);
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 5 || ActualHeight < 8) return;
            double y = Math.Clamp(ActualHeight - 4 - Decibels * .2, Math.Max(3, ActualHeight - 8), ActualHeight - 2);
            var pen = Decibels == 0 ? Normal : Edited;
            dc.DrawLine(pen, new(2, y), new(ActualWidth - 2, y));
            dc.DrawEllipse(pen.Brush, null, new(ActualWidth / 2, y), 2, 2);
        }
    }
    private sealed record GainSpan(UIEmotionNote Visual, SpanVisual Span, int Index, double X, double Right, double Y, double Height);
    private sealed class VolumeDrag
    {
        internal required GainSpan Handle;
        internal required MusicalEditorViewModel VM;
        internal required PhonemeVolumeKey Identity;
        internal required PhonemeVolumePlan Original;
        internal required Point Down;
        internal required long Revision;
        internal float Candidate;
        internal bool Moved;
    }
    private readonly List<GainSpan> _gainSpans = new();
    private GainSpan? _volumeHover;
    private VolumeDrag? _volumeDrag;
    private Popup? _volumePanel;
    private Action? _updateVolumePanelStatus;

    private void AttachVolumeContextMenu()
    {
        if (AccessTools.Field(typeof(PianorollView), "xContextMenu")?.GetValue(_view) is not ContextMenu menu) return;
        var item = new MenuItem { Header = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Name") + "…", Visibility = Visibility.Collapsed };
        GainSpan? target = null;
        Point anchor = default;
        _view.ContextMenuOpening += (_, _) =>
        {
            anchor = Mouse.GetPosition(_view);
            target = Enabled(_view) && Settings.IndividualPhonemeVolume ? _gainSpans.FirstOrDefault(h =>
                anchor.Y >= h.Y && anchor.Y <= h.Y + h.Height && anchor.X >= h.X && anchor.X <= h.Right) : null;
            item.Visibility = target != null ? Visibility.Visible : Visibility.Collapsed;
        };
        item.Click += (_, _) =>
        {
            try { if (target != null && Enabled(_view)) OpenVolumePanel(target, anchor); }
            catch (Exception e) { VolumeFailure(e); }
        };
        menu.Items.Add(item);
    }

    internal static void VolumeChanged()
    {
        foreach (var pair in States)
        {
            pair.Value._updateVolumePanelStatus?.Invoke();
            pair.Value.RequestRefresh();
        }
    }

    private void ConfigureGain(SpanVisual span, UIEmotionNote visual, int index, double x, double right, double y, double height, bool editable)
    {
        var glyph = span.Gain;
        if (DurationMode || TransitionMode || !Settings.IndividualPhonemeVolume || !editable || visual.Note?.Parent?.IsAi != false)
        { glyph.Visibility = Visibility.Collapsed; return; }
        glyph.Width = Math.Max(0, right - x); glyph.Height = height;
        Canvas.SetLeft(glyph, x); Canvas.SetTop(glyph, y);
        glyph.Decibels = PhonemeVolumeHost.Get(visual.Note).Values[index];
        glyph.Visibility = Visibility.Visible; glyph.InvalidateVisual();
        _gainSpans.Add(new(visual, span, index, x, right, y, height));
    }

    private GainSpan? HitVolume(Point point) => !Settings.IndividualPhonemeVolume ? null : _gainSpans.FirstOrDefault(h =>
        point.Y >= h.Y && point.Y <= h.Y + h.Height && point.X > h.X + 2 && point.X < h.Right - 2);

    private bool SearchVolume(Point point, out PianorollView.EmotionModeSearchResults result)
    {
        result = default;
        var handle = _volumeDrag?.Handle ?? HitVolume(point);
        _volumeHover = handle;
        if (handle?.Visual.Note is not { } note) return false;
        ClearPhonemeHover(); SetHover(null);
        float db = _volumeDrag?.Candidate ?? PhonemeVolumeHost.Get(note).Values[handle.Index];
        ShowTip(TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Tip", PhoneName(note, handle.Index), FormatGain(db)), point);
        _view.Cursor = Cursors.SizeNS;
        result = new(handle.Visual, PianorollView.HitObjectTypeEnum.Velocity, handle.X, handle.Right, false);
        return true;
    }

    private static string PhoneName(WIVSMNote note, int index) => note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries)[index];
    private void VolumeFailure(Exception error)
    {
        CancelVolumeDrag(); CloseVolumePanel();
        ShowTip(TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Status_Unavailable"), Mouse.GetPosition(_view));
        VOCALOIDPatcher.Utils.Debug.Print("Phoneme volume interaction: " + error.GetType().Name);
    }
    private static string FormatGain(float value) => value <= PhonemeVolumePlan.Minimum
        ? TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Mute") : value.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture) + " dB";

    private bool TryBeginVolumeDrag(Point point)
    {
        var handle = HitVolume(point);
        if (handle?.Visual.Note is not { } note || _view.DataContext is not MusicalEditorViewModel vm ||
            vm.VSMSequence is not { IsStaged: false } || _view.IsMouseCaptured) return false;
        CloseVolumePanel();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        { PhonemeVolumeHost.Set(note, handle.Index, 0); return true; }
        var plan = PhonemeVolumeHost.Get(note);
        _volumeDrag = new()
        {
            Handle = handle, VM = vm, Identity = new(note.CppObjPtr, BreathVolumeService.NoteGeneration(note)),
            Original = plan, Down = point, Candidate = plan.Values[handle.Index], Revision = ConsonantTimingRuntime.Revision
        };
        if (!_view.CaptureMouse()) { _volumeDrag = null; return true; }
        vm.EditorMode.IsIdleMouseOperation = false; _view.Cursor = Cursors.SizeNS;
        return true;
    }

    private bool VolumeContextValid(VolumeDrag drag) => Settings.IndividualPhonemeVolume && Enabled(_view) &&
        BreathVolumeService.IsNoteGenerationActive(drag.Identity.Handle, drag.Identity.Generation) &&
        _view.DataContext == drag.VM && drag.Revision == ConsonantTimingRuntime.Revision &&
        drag.VM.VSMSequence is { IsStaged: false } && drag.Handle.Visual.Note is { } note &&
        note.Phonemes == drag.Original.Topology && note.Parent?.Equals(drag.VM.ActivePart) == true;

    private void MoveVolumeDrag(MouseEventArgs e)
    {
        e.Handled = true;
        if (_volumeDrag is not { } drag) return;
        if (!VolumeContextValid(drag) || e.LeftButton != MouseButtonState.Pressed) { CancelVolumeDrag(); return; }
        var point = e.GetPosition(_view);
        double delta = drag.Down.Y - point.Y;
        if (!drag.Moved && Math.Abs(delta) < 3) return;
        drag.Moved = true;
        drag.Candidate = PhonemeVolumePlan.Normalize(drag.Original.Values[drag.Handle.Index] + delta *
            ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .05 : .25));
        DrawVolumeCandidate();
    }

    private void DrawVolumeCandidate()
    {
        if (_volumeDrag is not { } drag) return;
        foreach (var handle in _gainSpans.Where(h => h.Index == drag.Handle.Index && h.Visual.Note?.CppObjPtr == drag.Identity.Handle))
        { handle.Span.Gain.Decibels = drag.Candidate; handle.Span.Gain.InvalidateVisual(); }
        ShowTip(TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Tip", PhoneName(drag.Handle.Visual.Note!, drag.Handle.Index), FormatGain(drag.Candidate)), Mouse.GetPosition(_view));
    }

    private void EndVolumeDrag(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_volumeDrag is not { } drag) return;
        bool valid = VolumeContextValid(drag);
        var point = e.GetPosition(_view);
        if (valid && (drag.Moved || Math.Abs(point.Y - drag.Down.Y) >= 3))
        {
            drag.Moved = true;
            drag.Candidate = PhonemeVolumePlan.Normalize(drag.Original.Values[drag.Handle.Index] +
                (drag.Down.Y - point.Y) * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .05 : .25));
        }
        CancelVolumeDrag();
        if (!valid) return;
        if (drag.Moved) PhonemeVolumeHost.Set(drag.Handle.Visual.Note!, drag.Handle.Index, drag.Candidate);
        else OpenVolumePanel(drag.Handle, point);
    }

    private void CancelVolumeDrag()
    {
        if (_volumeDrag is not { } drag) return;
        _volumeDrag = null; _volumeHover = null; drag.VM.EditorMode.IsIdleMouseOperation = true;
        _tip.IsOpen = false; _view.Cursor = null;
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        RequestRefresh();
    }

    private void CloseVolumePanel()
    {
        if (_volumePanel != null) _volumePanel.IsOpen = false;
        _volumePanel = null;
        _updateVolumePanelStatus = null;
    }

    private void OpenVolumePanel(GainSpan handle, Point point)
    {
        CloseVolumePanel();
        var note = handle.Visual.Note!;
        var key = new PhonemeVolumeKey(note.CppObjPtr, BreathVolumeService.NoteGeneration(note));
        string topology = note.Phonemes;
        int selected = handle.Index;
        var panel = new StackPanel { Width = 290, Margin = new Thickness(12) };
        var title = new TextBlock { Text = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Name"), Foreground = Brushes.WhiteSmoke, FontWeight = FontWeights.SemiBold };
        var choices = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        var value = new TextBox { Width = 92, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 5) };
        var slider = new Slider { Minimum = PhonemeVolumePlan.Minimum, Maximum = PhonemeVolumePlan.Maximum, TickFrequency = .1, IsSnapToTickEnabled = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var reset = new Button { Content = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Reset"), Margin = new Thickness(0, 6, 8, 4), Padding = new Thickness(9, 3, 9, 3) };
        var mute = new Button { Content = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Mute"), Margin = new Thickness(0, 6, 0, 4), Padding = new Thickness(9, 3, 9, 3) };
        var hint = new TextBlock { Text = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_PanelHint"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, FontSize = 11 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 5, 0, 0) };
        panel.Children.Add(title); panel.Children.Add(choices); panel.Children.Add(slider); panel.Children.Add(value);
        buttons.Children.Add(reset); buttons.Children.Add(mute); panel.Children.Add(buttons); panel.Children.Add(hint); panel.Children.Add(status);
        var popup = new Popup
        {
            PlacementTarget = _view, Placement = PlacementMode.Relative, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 42)), BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = panel }
        };
        WpfTranslationPatch.MarkUntranslatable(panel);
        bool updating = false;
        bool Valid() => Settings.IndividualPhonemeVolume && Enabled(_view) &&
            BreathVolumeService.IsNoteGenerationActive(key.Handle, key.Generation) && note.Phonemes == topology &&
            _view.DataContext is MusicalEditorViewModel { VSMSequence.IsStaged: false } current && note.Parent?.Equals(current.ActivePart) == true;
        void Update()
        {
            if (!Valid()) { popup.IsOpen = false; return; }
            updating = true;
            float db = PhonemeVolumeHost.Get(note).Values[selected];
            title.Text = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Selected", PhoneName(note, selected), FormatGain(db));
            slider.Value = db; value.Text = db.ToString("0.#", CultureInfo.CurrentCulture);
            for (int i = 0; i < choices.Children.Count; i++)
            {
                ((Button)choices.Children[i]).FontWeight = i == selected ? FontWeights.Bold : FontWeights.Normal;
                ((Button)choices.Children[i]).BorderBrush = i == selected ? Brushes.LightSkyBlue : Brushes.Gray;
            }
            updating = false;
        }
        void Commit(double db)
        {
            if (!Valid()) { popup.IsOpen = false; return; }
            try { PhonemeVolumeHost.Set(note, selected, db); Update(); }
            catch (Exception e) { VolumeFailure(e); }
        }
        var phones = topology.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < phones.Length; i++)
        {
            int index = i;
            var button = new Button { Content = phones[i], MinWidth = 32, Margin = new Thickness(0, 0, 5, 4), Padding = new Thickness(7, 3, 7, 3) };
            button.Click += (_, _) => { selected = index; Update(); };
            choices.Children.Add(button);
        }
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            value.Text = slider.Value.ToString("0.#", CultureInfo.CurrentCulture);
            title.Text = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Selected", PhoneName(note, selected), FormatGain((float)slider.Value));
            status.Text = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Status_Preview");
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => Commit(slider.Value);
        slider.KeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) { Commit(slider.Value); e.Handled = true; } };
        value.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (double.TryParse(value.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double db) && double.IsFinite(db)) Commit(db);
            else Update();
            e.Handled = true;
        };
        reset.Click += (_, _) => Commit(0); mute.Click += (_, _) => Commit(PhonemeVolumePlan.Minimum);
        panel.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.Closed += (_, _) => { if (ReferenceEquals(_volumePanel, popup)) { _volumePanel = null; _updateVolumePanelStatus = null; } };
        // All anchors use the song canvas; clamp to the actual scroll viewport.
        var vm = (MusicalEditorViewModel)_view.DataContext;
        var viewer = vm.PianorollViewer;
        double left = viewer?.HorizontalOffset ?? 0, top = viewer?.VerticalOffset ?? 0;
        double width = viewer?.ViewportWidth ?? _view.ActualWidth, height = viewer?.ViewportHeight ?? _view.ActualHeight;
        ((Border)popup.Child).Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(120, height - 8) };
        popup.HorizontalOffset = Math.Clamp(point.X + 12, left, Math.Max(left, left + width - 320));
        popup.VerticalOffset = Math.Clamp(point.Y + 10, top, Math.Max(top, top + height - 260));
        _volumePanel = popup;
        _updateVolumePanelStatus = () =>
        {
            if (!Valid()) { popup.IsOpen = false; return; }
            status.Text = TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_Status_" +
                (Math.Abs(slider.Value - PhonemeVolumeHost.Get(note).Values[selected]) > .001 ? "Preview" : PhonemeVolumeHost.Status(note)));
        };
        Update(); _updateVolumePanelStatus(); popup.IsOpen = true;
        _view.Dispatcher.BeginInvoke(new Action(() => { if (popup.IsOpen) { value.Focus(); value.SelectAll(); } }));
    }
}
