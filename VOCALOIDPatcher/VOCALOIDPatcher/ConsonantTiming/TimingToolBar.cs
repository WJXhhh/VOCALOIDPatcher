using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VOCALOIDPatcher.ConsonantTiming;

internal enum AcousticEditMode { Phonemes, Transitions, Comp }

// This control owns only view state; selecting a tool never edits the score.
internal sealed class TimingToolBar : Border
{
    private readonly RadioButton _phonemes;
    private readonly RadioButton _transitions;
    private readonly RadioButton _comp;
    private readonly TextBlock _compLabel;
    private readonly TextBlock _caption;
    private readonly TextBlock _phonemeLabel;
    private readonly TextBlock _transitionLabel;
    private readonly TextBlock _preciseLabel;
    private readonly Border _action;
    private readonly string _preciseHint;
    private bool _synchronizing;
    private double _fitWidth = double.NaN;

    internal Button PreciseButton { get; }
    internal event Action<AcousticEditMode>? ModeRequested;
    internal event Action<VoiceComp.CompAction>? CompActionRequested;
    private readonly StackPanel _compActions = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly System.Collections.Generic.List<TextBlock> _compActionLabels = new();
    private readonly Button _compOverflow = new() { Visibility = Visibility.Collapsed };
    private AcousticEditMode _mode;

    internal TimingToolBar(string caption, string phonemes, string transitions, string precise,
        string phonemeHint, string transitionHint, string preciseHint)
    {
        _preciseHint = preciseHint;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = Brush("#F21B2026");
        BorderBrush = Brush("#46515D");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(4);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        FontOptions();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        Child = row;
        _caption = new TextBlock
        {
            Text = caption, Foreground = Brush("#A3AFBC"), FontSize = 10,
            Margin = new Thickness(7, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_caption);
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        var segmentFrame = new Border
        {
            Background = Brush("#14191F"), CornerRadius = new CornerRadius(4), Child = segments
        };
        row.Children.Add(segmentFrame);
        var style = (Style)XamlReader.Parse(SegmentStyle);
        _phonemes = Tool(phonemes, phonemeHint, "M3,3 L3,13 M13,3 L13,13 M3,8 L13,8 M8,5 L8,11", style, out _phonemeLabel);
        _transitions = Tool(transitions, transitionHint, "M1,12 L5,12 C8,12 8,4 11,4 L15,4 M5,3 L5,6 M11,10 L11,13", style, out _transitionLabel);
        segments.Children.Add(_phonemes);
        segments.Children.Add(_transitions);
        _comp = Tool(VoiceComp.CompEditorPanel.Text("Mode"), VoiceComp.CompEditorPanel.Text("Hint"),
            "M1,4 L7,4 L7,12 L1,12 Z M9,4 L15,4 L15,12 L9,12 Z M5,8 L11,8", style, out _compLabel);
        segments.Children.Add(_comp);
        _phonemes.Checked += (_, _) => { if (!_synchronizing) ModeRequested?.Invoke(AcousticEditMode.Phonemes); };
        _transitions.Checked += (_, _) => { if (!_synchronizing) ModeRequested?.Invoke(AcousticEditMode.Transitions); };
        _comp.Checked += (_, _) => { if (!_synchronizing) ModeRequested?.Invoke(AcousticEditMode.Comp); };

        PreciseButton = new Button
        {
            Content = Content(precise, "M2,4 L14,4 M2,12 L14,12 M6,1 L6,7 M11,9 L11,15", out _preciseLabel),
            ToolTip = preciseHint, Style = (Style)XamlReader.Parse(ActionStyle),
            Focusable = true, MinHeight = 28, Padding = new Thickness(8, 0, 8, 0)
        };
        AutomationProperties.SetName(PreciseButton, preciseHint);
        _action = new Border
        {
            BorderBrush = Brush("#46515D"), BorderThickness = new Thickness(1, 0, 0, 0),
            Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 0, 0),
            Child = PreciseButton, Visibility = Visibility.Collapsed
        };
        row.Children.Add(_action);
        foreach (var action in Enum.GetValues<VoiceComp.CompAction>())
        {
            string icon = action switch { VoiceComp.CompAction.LongSlice => "M2,1 L2,13 L14,13 M5,5 L13,5 M5,9 L11,9",
                VoiceComp.CompAction.PhonemeSplice => "M1,4 L6,4 L6,12 L1,12 Z M10,4 L15,4 L15,12 L10,12 Z M6,8 L10,8",
                _ => "M1,12 L5,12 L5,4 L11,4 M9,2 L13,4 L9,6 M14,8 L14,14" };
            var button = new Button { Content = Content(VoiceComp.CompEditorPanel.Text(action.ToString()), icon, out var label), Style = (Style)XamlReader.Parse(ActionStyle),
                MinHeight = 28, MinWidth = 32, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(2, 0, 0, 0) };
            _compActionLabels.Add(label); button.ToolTip = VoiceComp.CompEditorPanel.Text(action.ToString());
            button.Click += (_, _) => CompActionRequested?.Invoke(action);
            _compActions.Children.Add(button);
        }
        row.Children.Add(_compActions);
        _compOverflow.Content = "…"; _compOverflow.Style = (Style)XamlReader.Parse(ActionStyle); _compOverflow.MinWidth = 32; _compOverflow.MinHeight = 28;
        _compOverflow.ToolTip = VoiceComp.CompEditorPanel.Text("Mode");
        var menu = new ContextMenu();
        foreach (var action in Enum.GetValues<VoiceComp.CompAction>())
        { var item = new MenuItem { Header = VoiceComp.CompEditorPanel.Text(action.ToString()) }; item.Click += (_, _) => CompActionRequested?.Invoke(action); menu.Items.Add(item); }
        _compOverflow.Click += (_, _) => { menu.PlacementTarget = _compOverflow; menu.IsOpen = true; }; row.Children.Add(_compOverflow);
        // ButtonBase consumes its own gestures. Consume the frame's empty space
        // here too, before native pianoroll behaviors receive bubbling events.
        MouseDown += (_, e) => e.Handled = true;
        MouseUp += (_, e) => e.Handled = true;
        MouseMove += (_, e) => e.Handled = true;
        Select(false);
    }

    private void FontOptions()
    {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
    }

    internal void Select(bool transitions) => Select(transitions ? AcousticEditMode.Transitions : AcousticEditMode.Phonemes);
    internal void Select(AcousticEditMode mode)
    {
        _synchronizing = true;
        try
        {
            _mode = mode;
            _phonemes.IsChecked = mode == AcousticEditMode.Phonemes;
            _transitions.IsChecked = mode == AcousticEditMode.Transitions;
            _comp.IsChecked = mode == AcousticEditMode.Comp;
            _compActions.Visibility = mode == AcousticEditMode.Comp ? Visibility.Visible : Visibility.Collapsed;
            _compOverflow.Visibility = Visibility.Collapsed;
            _action.Visibility = mode == AcousticEditMode.Transitions ? Visibility.Visible : Visibility.Collapsed;
            _fitWidth = double.NaN;
        }
        finally { _synchronizing = false; }
    }

    internal void SetAvailability(bool phonemes, bool transitions, bool comp = true)
    {
        var phonemeVisibility = phonemes ? Visibility.Visible : Visibility.Collapsed;
        var transitionVisibility = transitions ? Visibility.Visible : Visibility.Collapsed;
        _comp.Visibility = comp ? Visibility.Visible : Visibility.Collapsed;
        if (_phonemes.Visibility == phonemeVisibility && _transitions.Visibility == transitionVisibility) return;
        _phonemes.Visibility = phonemeVisibility;
        _transitions.Visibility = transitionVisibility;
        _fitWidth = double.NaN;
    }

    internal void DescribeTarget(string? target, string? error = null) => PreciseButton.ToolTip =
        error ?? (target == null ? _preciseHint : target + "\n" + _preciseHint);

    internal void Fit(double availableWidth)
    {
        if (_fitWidth == availableWidth) return;
        _fitWidth = availableWidth;
        // Collapse decoration before labels; hit targets stay at least 32 DIP wide.
        _caption.Visibility = _preciseLabel.Visibility = _phonemeLabel.Visibility = _transitionLabel.Visibility = _compLabel.Visibility = Visibility.Visible;
        _compActions.Visibility = _mode == AcousticEditMode.Comp ? Visibility.Visible : Visibility.Collapsed;
        _compOverflow.Visibility = Visibility.Collapsed;
        foreach (var label in _compActionLabels) label.Visibility = Visibility.Visible;
        double NaturalWidth()
        {
            Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Child.DesiredSize.Width + Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right;
        }
        if (NaturalWidth() > availableWidth) _caption.Visibility = Visibility.Collapsed;
        if (NaturalWidth() > availableWidth) _preciseLabel.Visibility = Visibility.Collapsed;
        if (NaturalWidth() > availableWidth) _phonemeLabel.Visibility = _transitionLabel.Visibility = _compLabel.Visibility = Visibility.Collapsed;
        if (NaturalWidth() > availableWidth) foreach (var label in _compActionLabels) label.Visibility = Visibility.Collapsed;
        if (NaturalWidth() > availableWidth && _mode == AcousticEditMode.Comp)
        { _compActions.Visibility = Visibility.Collapsed; _compOverflow.Visibility = Visibility.Visible; }
        MaxWidth = Math.Max(0, availableWidth);
    }

    internal bool Owns(DependencyObject target) => target == this || IsAncestorOf(target);

    private static RadioButton Tool(string name, string hint, string geometry, Style style, out TextBlock label)
    {
        var tool = new RadioButton
        {
            Content = Content(name, geometry, out label), ToolTip = hint, Style = style,
            MinWidth = 32, MinHeight = 28, Padding = new Thickness(9, 0, 9, 0), Focusable = true
        };
        AutomationProperties.SetName(tool, name);
        ToolTipService.SetInitialShowDelay(tool, 350);
        return tool;
    }

    private static StackPanel Content(string name, string geometry, out TextBlock label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Path
        {
            Data = Geometry.Parse(geometry), Width = 16, Height = 16, Stretch = Stretch.None,
            StrokeThickness = 1.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        icon.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1)
        });
        row.Children.Add(icon);
        label = new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        return row;
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    internal const string SegmentStyle = AcousticControlStyle.SegmentStyle;
    internal const string ActionStyle = AcousticControlStyle.ActionStyle;
}
