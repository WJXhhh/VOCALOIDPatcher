using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace VOCALOIDPatcher.ConsonantTiming;

// Explicit colors and templates keep the editor's implicit WPF styles from
// producing light text on a system-white popup surface.
internal sealed class TransitionEditorPanel : Border
{
    internal TextBlock Title { get; } = Label();
    internal ComboBox Choices { get; } = new();
    internal TextBlock CoverageLabel { get; } = Label();
    internal CheckBox Coverage { get; }
    internal TextBox Phase { get; }
    internal TextBox Left { get; }
    internal TextBox Right { get; }
    internal TextBox Gain { get; }
    internal Button Apply { get; }
    internal Button Reset { get; }
    internal Button ResetExpression { get; }
    internal Button Mute { get; }
    internal Button Close { get; }
    internal TextBlock Status { get; } = Label();
    internal TextBlock Legacy { get; } = Label();
    private static SolidColorBrush Brush(string color)
    { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private static TextBlock Label(string text = "") => new()
    {
        Text = text, Foreground = Brush("#D6E0EA"), FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
    };
    internal TransitionEditorPanel(Func<string, string> text, string closeText)
    {
        Width = 332; Padding = new Thickness(12); CornerRadius = new CornerRadius(7);
        Background = Brush("#1B2026"); BorderBrush = Brush("#46515D"); BorderThickness = new Thickness(1);
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextElement.SetForeground(this, Brush("#D6E0EA"));
        TextElement.SetFontFamily(this, SystemFonts.MessageFontFamily);
        TextElement.SetFontSize(this, 12);
        var body = new StackPanel(); Child = body;
        var actionStyle = (Style)XamlReader.Parse(AcousticControlStyle.ActionStyle);
        Button Action(string key)
        {
            var button = new Button { Content = text(key), Style = actionStyle, MinHeight = 28,
                Padding = new Thickness(7, 0, 7, 0), FontSize = 12, Margin = new Thickness(2, 0, 2, 0) };
            AutomationProperties.SetName(button, text(key)); return button;
        }
        Close = new Button { Content = "×", Style = actionStyle, Width = 26, Height = 26, ToolTip = closeText };
        AutomationProperties.SetName(Close, closeText);
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(Close, Dock.Right); heading.Children.Add(Close);
        Title.Text = text("PanelTitle"); Title.FontWeight = FontWeights.SemiBold;
        Title.TextWrapping = TextWrapping.NoWrap; Title.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.Children.Add(Title); body.Children.Add(heading);
        Choices.Style = (Style)XamlReader.Parse(AcousticControlStyle.ComboStyle);
        Choices.MinHeight = 28; Choices.FontSize = 12;
        Choices.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(Choices);

        var fields = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        fields.ColumnDefinitions.Add(new()); fields.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); fields.ColumnDefinitions.Add(new());
        fields.RowDefinitions.Add(new() { Height = GridLength.Auto }); fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var inputStyle = (Style)XamlReader.Parse(InputStyle);
        TextBox Field(string key, int row, int column)
        {
            var group = new StackPanel { Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0) };
            var label = Label(text(key)); label.Margin = new Thickness(0, 0, 0, 4); group.Children.Add(label);
            var input = new TextBox { Style = inputStyle, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Height = 28, Padding = new Thickness(7, 4, 7, 4), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(input, text(key)); group.Children.Add(input);
            Grid.SetRow(group, row); Grid.SetColumn(group, column); fields.Children.Add(group); return input;
        }
        Phase = Field("Phase", 0, 0); Gain = Field("Volume", 0, 2);
        Left = Field("Left", 1, 0); Right = Field("Right", 1, 2); body.Children.Add(fields);

        CoverageLabel.Text = text("Coverage");
        Coverage = new CheckBox { Content = CoverageLabel, Style = (Style)XamlReader.Parse(CheckStyle),
            Margin = new Thickness(0, 0, 0, 10), ToolTip = text("CoverageHint") };
        AutomationProperties.SetName(Coverage, text("Coverage")); body.Children.Add(Coverage);
        var actions = new DockPanel { LastChildFill = false };
        Apply = Action("Apply"); Apply.MinWidth = 58;
        var primary = new Border { Background = Brush("#203F54"), BorderBrush = Brush("#437D9F"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = Apply, Margin = new Thickness(5, 0, 0, 0) };
        DockPanel.SetDock(primary, Dock.Right); actions.Children.Add(primary);
        ResetExpression = Action("ResetExpression"); ResetExpression.ToolTip = text("ResetExpressionHint");
        Reset = Action("Reset"); Mute = Action("Mute");
        actions.Children.Add(ResetExpression); actions.Children.Add(Reset); actions.Children.Add(Mute); body.Children.Add(actions);
        Status.Foreground = Brush("#A9C4D8"); Status.FontSize = 11; Status.Margin = new Thickness(0, 9, 0, 0);
        Status.MinHeight = 15; body.Children.Add(Status);
        Legacy.Text = text("LegacyOnset"); Legacy.FontSize = 11; Legacy.Margin = new Thickness(0, 5, 0, 0);
        Legacy.Foreground = Brush("#D9BA89"); Legacy.Visibility = Visibility.Collapsed; body.Children.Add(Legacy);
        Phase.ToolTip = text("PhaseHint");
    }
    private const string InputStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
          <Setter Property="Foreground" Value="#D6EFFF"/><Setter Property="Background" Value="#14191F"/><Setter Property="BorderBrush" Value="#46515D"/>
          <Setter Property="BorderThickness" Value="1"/><Setter Property="CaretBrush" Value="#D6EFFF"/><Setter Property="SelectionBrush" Value="#437D9F"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
            <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4">
              <ScrollViewer x:Name="PART_ContentHost"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#85CFFF"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """;
    private const string CheckStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Setter Property="Foreground" Value="#D6E0EA"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
            <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
              <Border x:Name="Box" Width="14" Height="14" VerticalAlignment="Center" Background="#14191F" BorderBrush="#627385" BorderThickness="1" CornerRadius="3">
                <Path x:Name="Tick" Visibility="Collapsed" Data="M2,6 L5,9 L11,2" Stroke="#D6EFFF" StrokeThickness="1.5"/>
              </Border><ContentPresenter Grid.Column="1" Margin="6,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True"><Setter TargetName="Tick" Property="Visibility" Value="Visible"/><Setter TargetName="Box" Property="Background" Value="#203F54"/></Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#85CFFF"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """;
}
