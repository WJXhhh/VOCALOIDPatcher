using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Translation;

namespace VOCALOIDPatcher.DurationSelection;

// Pure view: typing, toggling and unit conversion never write to the document.
internal sealed class DurationSelectionEditor : StackPanel
{
    internal readonly TextBox Percent = new() { Width = 88, Margin = new Thickness(0, 0, 6, 0) };
    internal readonly TextBox Milliseconds = new() { Width = 88, Margin = new Thickness(0, 0, 6, 0) };
    internal readonly CheckBox Expanded = new() { Foreground = Brushes.Goldenrod, Margin = new Thickness(0, 10, 0, 6) };
    internal readonly TextBlock Status = new() { Foreground = Brushes.LightSkyBlue, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    internal readonly Button Apply = new() { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 8, 0) };
    internal readonly Button Reset = new() { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _info = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private bool _updating;
    private double _candidate, _actualMilliseconds;
    internal event Action<double, DurationCandidatePolicy>? ApplyRequested;
    internal event Action? CancelRequested;
    private static string Tr(string key, params object[] args) => TranslationManager.Tr("VOCALOIDPatcher_DurationSelection_" + key, args);

    internal DurationSelectionEditor()
    {
        Width = 290; Margin = new Thickness(12);
        Children.Add(new TextBlock { Text = Tr("Name"), Foreground = Brushes.WhiteSmoke, FontWeight = FontWeights.SemiBold });
        Children.Add(_info);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, field) in new[] { (Tr("Percent"), Percent), (Tr("Milliseconds"), Milliseconds) })
        {
            var column = new StackPanel(); column.Children.Add(new TextBlock { Text = label, Foreground = Brushes.WhiteSmoke });
            column.Children.Add(field); row.Children.Add(column);
        }
        Children.Add(row); Expanded.Content = Tr("Expanded"); Children.Add(Expanded);
        Children.Add(new TextBlock { Text = Tr("PanelHint"), Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        Children.Add(Status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Apply.Content = Tr("Apply"); Reset.Content = Tr("Reset"); buttons.Children.Add(Apply); buttons.Children.Add(Reset); Children.Add(buttons);
        Percent.TextChanged += (_, _) => Parse(Percent, Milliseconds, 100);
        Milliseconds.TextChanged += (_, _) => Parse(Milliseconds, Percent, _actualMilliseconds);
        Expanded.Checked += (_, _) => Preview(); Expanded.Unchecked += (_, _) => Preview();
        Apply.Click += (_, _) => Submit(); Reset.Click += (_, _) => ApplyRequested?.Invoke(1, DurationCandidatePolicy.Native);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelRequested?.Invoke(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
        };
        WpfTranslationPatch.MarkUntranslatable(this);
    }
    internal void Show(DurationSelectionPlan plan, double actualMilliseconds)
    {
        if (!double.IsFinite(actualMilliseconds) || actualMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(actualMilliseconds));
        _updating = true;
        try
        {
            _actualMilliseconds = actualMilliseconds; _candidate = plan.Ratio;
            Percent.Text = (_candidate * 100).ToString("0.######", CultureInfo.CurrentCulture);
            Milliseconds.Text = (_candidate * actualMilliseconds).ToString("0.######", CultureInfo.CurrentCulture);
            Expanded.IsChecked = plan.Policy == DurationCandidatePolicy.ExpandedExperimental;
            _info.Text = Tr("Actual", actualMilliseconds.ToString("0.#", CultureInfo.CurrentCulture), plan.Topology);
            Status.Text = Tr("Submitted");
        }
        finally { _updating = false; }
    }
    private void Preview() { if (!_updating) Status.Text = Tr("Preview"); }
    private void Parse(TextBox source, TextBox other, double unit)
    {
        if (_updating) return;
        if (unit <= 0 || !double.TryParse(source.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) || !double.IsFinite(number) || number <= 0)
        { _candidate = double.NaN; Status.Text = Tr("Invalid"); return; }
        _candidate = number / unit; _updating = true;
        try { other.Text = (source == Percent ? _candidate * _actualMilliseconds : _candidate * 100).ToString("0.######", CultureInfo.CurrentCulture); }
        finally { _updating = false; }
        Preview();
    }
    private void Submit()
    {
        if (!double.IsFinite(_candidate) || _candidate is < .001 or > 32) { Status.Text = Tr("Invalid"); return; }
        ApplyRequested?.Invoke(_candidate, Expanded.IsChecked == true ? DurationCandidatePolicy.ExpandedExperimental : DurationCandidatePolicy.Native);
    }
}
