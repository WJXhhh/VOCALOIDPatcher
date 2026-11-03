using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

internal sealed class CompEditorPanel : Border
{
    private readonly TextBlock _title = Label(), _status = Label(), _error = Label();
    private readonly CompWaveView _waves = new();
    private readonly Grid _body = new();
    private readonly StackPanel _inspector = new();
    private readonly StackPanel _fields = new();
    private readonly StackPanel _advanced = new(), _popupContent = new();
    private readonly Popup _precise = new() { StaysOpen = false, AllowsTransparency = true };
    private readonly ComboBox _source = new() { MinWidth = 100, Margin = new Thickness(3), FontSize = 12 };
    private readonly TextBox _lyric = Input(), _phones = Input(), _length = Input(), _offset = Input(), _vowel = Input(), _seam = Input(), _end = Input(), _fade = Input();
    private readonly TextBox _contextBefore = Input(), _contextAfter = Input(), _contextLength = Input();
    private readonly ToggleButton _bypass;
    private readonly Button _sourceListen, _preview, _phrase;
    private readonly ToggleButton[] _presets = new ToggleButton[3];
    private readonly Button _preciseButton;
    private WIVSMNote? _note;
    private long _noteGeneration;
    private VoiceCompPlan? _plan;
    private bool _sync, _compact, _layoutKnown;
    private SoundPlayer? _player;
    private string? _previewPath;
    private long _previewGeneration;
    internal event Action? CollapseRequested;
    internal event Action? PickSourceRequested;
    internal event Action<double>? HeightRequested;
    internal event Action<bool>? GestureChanged;
    internal WIVSMNote? Target => _note;
    internal static string Text(string name, params object[] args) => TranslationManager.Tr("VOCALOIDPatcher_VoiceComp_" + name, args);
    internal static SolidColorBrush Brush(string color)
    { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private static TextBlock Label(string? text = null) => new() { Text = text ?? "", Foreground = Brush("#D6E0EA"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2, 4, 2) };
    private static TextBox Input() => new() { MinWidth = 64, Height = 25, FontSize = 12, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(3),
        Background = Brush("#14191F"), Foreground = Brush("#D6EFFF"), BorderBrush = Brush("#46515D"), BorderThickness = new Thickness(1) };
    internal static Button Action(string name, Action action)
    {
        var button = new Button { Content = Text(name), Style = (Style)XamlReader.Parse(TimingToolBar.ActionStyle),
            Padding = new Thickness(8, 0, 8, 0), MinHeight = 28, MinWidth = 32, Margin = new Thickness(2, 0, 2, 0) };
        AutomationProperties.SetName(button, Text(name)); button.Click += (_, _) => action(); return button;
    }
    internal CompEditorPanel()
    {
        Background = Brush("#F91B2026"); BorderBrush = Brush("#46515D"); BorderThickness = new Thickness(1); CornerRadius = new(7);
        Padding = new Thickness(8, 4, 8, 6); UseLayoutRounding = true; SnapsToDevicePixels = true;
        MinHeight = 200; MaxHeight = 360; Height = 272; Focusable = true;
        WpfTranslationPatch.MarkUntranslatable(this);
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Child = root;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var collapse = Action("Collapse", () => CollapseRequested?.Invoke()); DockPanel.SetDock(collapse, Dock.Right); header.Children.Add(collapse);
        _preciseButton = Action("Precise", () => { _precise.PlacementTarget = _preciseButton; _precise.Placement = PlacementMode.Top; _precise.IsOpen = true; });
        DockPanel.SetDock(_preciseButton, Dock.Right); header.Children.Add(_preciseButton);
        DockPanel.SetDock(_status, Dock.Right); header.Children.Add(_status);
        _title.FontWeight = FontWeights.Medium; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextTrimming = TextTrimming.CharacterEllipsis; _status.MaxWidth = 150;
        header.Children.Add(_title); root.Children.Add(header);
        _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(new() { Width = new GridLength(216) });
        Grid.SetRow(_body, 1); root.Children.Add(_body); _body.Children.Add(_waves);
        Grid.SetColumn(_inspector, 1); _body.Children.Add(_inspector);
        _inspector.Background = Brush("#14191F"); _inspector.Margin = new Thickness(8, 0, 0, 0);
        _inspector.Children.Add(_fields);
        _length.ToolTip = Text("AutoLength");
        foreach (var numeric in new[] { _length, _offset, _vowel, _seam, _end, _fade, _contextLength }) numeric.FontFamily = new FontFamily("Consolas");
        _source.Style = (Style)XamlReader.Parse(AcousticControlStyle.ComboStyle);
        var sourceRow = new DockPanel(); sourceRow.Children.Add(Label(Text("Source"))); sourceRow.Children.Add(_source); _fields.Children.Add(sourceRow);
        Field("Lyric", _lyric); Field("Phonemes", _phones);
        var numbers = new Grid(); numbers.ColumnDefinitions.Add(new()); numbers.ColumnDefinitions.Add(new());
        var first = new StackPanel(); var second = new StackPanel(); Grid.SetColumn(second, 1); numbers.Children.Add(first); numbers.Children.Add(second);
        first.Children.Add(Label(Text("Length"))); first.Children.Add(_length); second.Children.Add(Label(Text("Offset"))); second.Children.Add(_offset); _fields.Children.Add(numbers);
        var small = new WrapPanel(); small.Children.Add(Label(Text("Vowel"))); _vowel.Width = 48; small.Children.Add(_vowel);
        small.Children.Add(Label(Text("Seam"))); _seam.Width = 48; small.Children.Add(_seam); _advanced.Children.Add(small);
        var endings = new WrapPanel(); endings.Children.Add(Label(Text("End"))); _end.Width = 48; endings.Children.Add(_end);
        endings.Children.Add(Label(Text("Fade"))); _fade.Width = 48; endings.Children.Add(_fade); _advanced.Children.Add(endings);
        var import = Action("PickSource", () => PickSourceRequested?.Invoke()); _advanced.Children.Add(import);
        foreach (var (key, input) in new[] { ("ContextBefore", _contextBefore), ("ContextAfter", _contextAfter), ("ContextLength", _contextLength) })
        {
            var row = new DockPanel(); var label = Label(Text(key)); label.Width = 92;
            row.Children.Add(label); row.Children.Add(input); _advanced.Children.Add(row);
        }
        var presets = new UniformGrid { Columns = 3, Margin = new Thickness(3, 2, 3, 0) };
        foreach (var trim in Enum.GetValues<CompTrim>())
        {
            var label = new TextBlock { Text = Text(trim.ToString()), FontSize = 11, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 62 };
            var content = new StackPanel();
            content.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(trim switch
            { CompTrim.KeepHead => "M1,10 L1,2 30,2 30,10 M4,6 L7,4 10,8 13,3 16,9 19,5 22,7 27,6",
                CompTrim.KeepTail => "M12,10 L12,2 41,2 41,10 M15,6 L18,4 21,8 24,3 27,9 30,5 33,7 38,6",
                _ => "M7,10 L7,2 35,2 35,10 M10,6 L13,4 16,8 19,3 22,9 25,5 28,7 32,6" }),
                Stroke = Brush("#78C7E8"), StrokeThickness = 1, Width = 43, Height = 12, Margin = new Thickness(0, 0, 0, 2) });
            content.Children.Add(label);
            var button = new ToggleButton { Content = content, MinHeight = 40, MinWidth = 32, Padding = new Thickness(2),
                ToolTip = Text("Crop") + " · " + Text(trim.ToString()),
                Style = (Style)XamlReader.Parse(TimingToolBar.SegmentStyle.Replace("RadioButton", "ToggleButton")) };
            AutomationProperties.SetName(button, Text(trim.ToString()));
            button.Click += (_, _) => { if (!_sync) UpdateSource(s => s with { Trim = trim }); };
            _presets[(int)trim] = button; presets.Children.Add(button);
        }
        _fields.Children.Add(presets);
        var footer = new DockPanel { Margin = new Thickness(0, 5, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        var controls = new WrapPanel(); footer.Children.Add(controls);
        _sourceListen = Action("Solo", () => _ = Preview(true)); _preview = Action("Listen", () => _ = Preview(false));
        _phrase = Action("Phrase", PlayPhrase);
        controls.Children.Add(_sourceListen); controls.Children.Add(_preview); controls.Children.Add(_phrase);
        _bypass = new ToggleButton { Content = Text("Bypass"), MinHeight = 28, Padding = new Thickness(8, 0, 8, 0),
            Style = (Style)XamlReader.Parse(TimingToolBar.SegmentStyle.Replace("RadioButton", "ToggleButton")) };
        _bypass.Click += (_, _) => { if (_plan != null && _note != null) Commit(_plan with { Bypass = _bypass.IsChecked == true }); };
        controls.Children.Add(_bypass); controls.Children.Add(Action("Reset", () => { if (_note != null) VoiceCompHost.Set(_note, null); }));
        _error.Foreground = Brush("#D1B37B"); _error.TextTrimming = TextTrimming.CharacterEllipsis; _error.MaxWidth = 240; controls.Children.Add(_error);
        var resize = new Thumb { Height = 5, Cursor = Cursors.SizeNS, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top,
            Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Thumb\"><Border Background=\"Transparent\"/></ControlTemplate>") };
        resize.DragDelta += (_, e) => HeightRequested?.Invoke(Math.Clamp(Height - e.VerticalChange, 200, 360)); root.Children.Add(resize);
        foreach (var input in new[] { _lyric, _phones, _length, _offset, _vowel, _seam, _end, _fade, _contextBefore, _contextAfter, _contextLength })
        {
            input.LostKeyboardFocus += (_, _) => ApplyFields();
            input.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { ApplyFields(); e.Handled = true; }
                else if (e.Key == Key.Escape) { SynchronizeFields(); e.Handled = true; }
            };
        }
        _source.SelectionChanged += (_, _) => { if (!_sync) { SynchronizeFields(); Draw(); } };
        _waves.OffsetCommitted += value => UpdateSource(s => s with { OffsetMs = value });
        _waves.LengthCommitted += value =>
        {
            if (Recipe != null) UpdateSource(s => s with { LengthMs = value });
            else if (_plan?.Isolation is { After: true } isolation)
                Commit(_plan with { Isolation = isolation with { SourceLengthMs = value } });
        };
        _waves.GestureChanged += active => GestureChanged?.Invoke(active);
        _waves.TrimCommitted += (begin, value) => UpdateSource(s => begin ? s with { TrimBeginMs = value } : s with { TrimEndMs = value });
        _waves.SourceSelected += index => _source.SelectedIndex = Math.Min(index, _source.Items.Count - 1);
        _waves.VowelCommitted += index => UpdateSource(s => s with { VowelIndex = index });
        _waves.SeamCommitted += value => { if (_plan != null) Commit(_plan with { SeamMs = value }); };
        _waves.EndCommitted += value => { if (_plan != null) Commit(_plan with { Isolation = (_plan.Isolation ?? new(true)) with { EndOffsetMs = value } }); };
        MouseDown += (_, e) => e.Handled = true; MouseUp += (_, e) => e.Handled = true; MouseMove += (_, e) => e.Handled = true;
        SizeChanged += (_, _) => Fit();
        Unloaded += (_, _) => { _previewGeneration++; StopPreview(); _precise.IsOpen = false; _waves.Cancel(); };
    }
    private void Field(string label, TextBox box)
    { var row = new DockPanel(); var caption = Label(Text(label)); caption.Width = 62; row.Children.Add(caption); row.Children.Add(box); _fields.Children.Add(row); }
    private void Fit()
    {
        _status.MaxWidth = ActualWidth < 600 ? 90 : 150;
        bool compact = ActualWidth < 840 || ActualHeight < 250; if (_layoutKnown && compact == _compact) return; _compact = compact; _layoutKnown = true;
        _body.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 216);
        if (_fields.Parent is Panel parent) parent.Children.Remove(_fields);
        if (_advanced.Parent is Panel advancedParent) advancedParent.Children.Remove(_advanced);
        _popupContent.Children.Clear();
        if (_precise.Child is Border oldFrame) oldFrame.Child = null;
        _precise.Child = new Border { Background = Brush("#1B2026"), BorderBrush = Brush("#46515D"), BorderThickness = new Thickness(1),
            CornerRadius = new(7), Padding = new Thickness(8), Width = 260, Child = _popupContent };
        if (compact)
        {
            _popupContent.Children.Add(_fields);
        }
        else
        {
            _precise.IsOpen = false; _inspector.Children.Add(_fields);
        }
        _popupContent.Children.Add(_advanced);
    }
    internal void Show(WIVSMNote? note)
    {
        if (Visibility != Visibility.Visible)
        { Visibility = Visibility.Visible; BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))); }
        if (_note != null && note != null && (nint)_note == (nint)note && _plan == VoiceCompHost.Get(note)) { Draw(); return; }
        _waves.Cancel(); _previewGeneration++; StopPreview(); _note = note; _plan = note == null ? null : VoiceCompHost.Get(note);
        _noteGeneration = note == null ? 0 : BreathVolume.BreathVolumeService.NoteGeneration(note);
        _title.Text = note == null ? Text("SelectNote") : Text("Target", note.Lyric, note.NoteNumber);
        _sync = true;
        try
        {
            _source.Items.Clear();
            if (_plan?.Consonant != null) { _source.Items.Add(Text("Consonant")); _source.Items.Add(Text("VowelSource")); }
            else _source.Items.Add(Text(_plan?.Body != null ? "Body" : "IndependentSource"));
            _source.SelectedIndex = 0; _bypass.IsChecked = _plan?.Bypass == true;
        }
        finally { _sync = false; }
        SynchronizeFields(); Draw();
    }
    private bool ValidTarget => _note != null && BreathVolume.BreathVolumeService.IsNoteGenerationActive(_note.CppObjPtr, _noteGeneration);
    internal void ShowError(Exception error) { _error.Text = Text("Invalid"); _error.ToolTip = error.Message; }
    private SourceRecipe? Recipe => _plan?.Consonant != null ? _source.SelectedIndex == 0 ? _plan.Consonant : _plan.Vowel : _plan?.Body;
    private void SynchronizeFields()
    {
        _sync = true;
        try
        {
            var recipe = Recipe; bool has = recipe != null;
            var target = ValidTarget ? _note : null;
            _lyric.Text = recipe?.Lyric ?? target?.Lyric ?? "";
            _phones.Text = recipe?.Phonemes ?? (recipe?.Lyric != null ? "" : target?.Phonemes ?? "");
            _length.Text = Number(recipe?.LengthMs ?? _plan?.Isolation?.SourceLengthMs ?? 0); _offset.Text = Number(recipe?.OffsetMs ?? 0);
            _length.ToolTip = Text(has ? "AutoLength" : "SustainAutoLength");
            _vowel.Text = (recipe?.VowelIndex ?? -1).ToString(CultureInfo.CurrentCulture); _seam.Text = Number(_plan?.SeamMs ?? 5);
            _end.Text = Number(_plan?.Isolation?.EndOffsetMs ?? 0); _fade.Text = Number(_plan?.Isolation?.FadeMs ?? 5);
            _contextBefore.Text = recipe?.Context?.Before ?? ""; _contextAfter.Text = recipe?.Context?.After ?? "";
            _contextLength.Text = Number(recipe?.Context?.NoteMs ?? 500);
            foreach (var input in new[] { _lyric, _phones, _length, _offset, _vowel, _contextBefore, _contextAfter, _contextLength }) input.IsEnabled = has;
            _length.IsEnabled = has || _plan?.Isolation?.After == true;
            for (int i = 0; i < _presets.Length; i++) { _presets[i].IsChecked = i == (int)(recipe?.Trim ?? CompTrim.KeepHead);
                _presets[i].IsEnabled = has && (_plan?.Consonant == null || _source.SelectedIndex == 1); }
            _bypass.IsEnabled = _plan != null;
        }
        finally { _sync = false; }
    }
    private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private void ApplyFields()
    {
        if (_sync || !ValidTarget || _note == null || _plan == null) return;
        try
        {
            double Parse(TextBox field) => double.Parse(field.Text, CultureInfo.CurrentCulture);
            var plan = _plan with { SeamMs = Parse(_seam) };
            if (Recipe is { } recipe)
            {
                var changed = recipe with { Lyric = recipe.Lyric == null && _lyric.Text == _note.Lyric ? null : _lyric.Text,
                    Phonemes = string.IsNullOrWhiteSpace(_phones.Text) || recipe.Phonemes == null && _phones.Text == _note.Phonemes ? null : _phones.Text,
                    LengthMs = Parse(_length), OffsetMs = Parse(_offset),
                    VowelIndex = int.Parse(_vowel.Text, CultureInfo.CurrentCulture),
                    Context = string.IsNullOrWhiteSpace(_contextBefore.Text) && string.IsNullOrWhiteSpace(_contextAfter.Text) ? null :
                        new(string.IsNullOrWhiteSpace(_contextBefore.Text) ? null : _contextBefore.Text,
                            string.IsNullOrWhiteSpace(_contextAfter.Text) ? null : _contextAfter.Text, Parse(_contextLength)) };
                plan = WithRecipe(plan, changed);
            }
            else if (plan.Isolation?.After == true)
                plan = plan with { Isolation = plan.Isolation with { SourceLengthMs = Parse(_length) } };
            double end = Parse(_end), fade = Parse(_fade);
            if (plan.Isolation != null || end != 0 || fade != 5)
                plan = plan with { Isolation = (plan.Isolation ?? new()) with { After = true, EndOffsetMs = end, FadeMs = fade } };
            Commit(plan);
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidDataException or ArgumentException)
        { _error.Text = Text("Invalid"); _error.ToolTip = error.Message; }
    }
    private VoiceCompPlan WithRecipe(VoiceCompPlan plan, SourceRecipe recipe) => plan.Consonant == null ? plan with { Body = recipe } :
        _source.SelectedIndex == 0 ? plan with { Consonant = recipe } : plan with { Vowel = recipe };
    private void UpdateSource(Func<SourceRecipe, SourceRecipe> change) { if (_plan != null && Recipe is { } recipe) Commit(WithRecipe(_plan, change(recipe))); }
    private void Commit(VoiceCompPlan plan)
    {
        if (!ValidTarget || _note == null) return;
        try { VoiceCompHost.Set(_note, plan); _plan = plan; _error.Text = ""; SynchronizeFields(); Draw(); }
        catch (Exception error) { _error.Text = Text("Invalid"); _error.ToolTip = error.Message; }
    }
    internal void UseSource(WIVSMNote donor)
    {
        if (!ValidTarget || _note == null || donor.Parent?.VoiceBankID != _note.Parent?.VoiceBankID) { _error.Text = Text("SameBank"); return; }
        UpdateSource(source => VoiceCompHost.SnapshotSource(donor, source));
    }
    internal void PickingSource() { _status.Text = Text("PickHint"); }
    internal void FocusAction(CompAction action)
    { if (action == CompAction.LongSlice && _plan?.Consonant != null) _source.SelectedIndex = 1; }
    private void Draw()
    {
        if (!ValidTarget || _note?.Parent is not { } part) { _waves.Update(null, null, null, 0); return; }
        var state = VoiceCompHost.View(part); string status = Text(state?.Status ?? "Pending");
        if (_status.Text != status)
        { _status.Text = status; _status.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(140))); }
        _status.Foreground = Brush(state?.Status == "Ready" ? "#9CC9B3" : "#D1B37B");
        _status.ToolTip = state?.Error; _waves.Update(_note, _plan, state, _source.SelectedIndex);
        int index = part.Notes.FindIndex(n => (nint)n == (nint)_note);
        if (state?.Views.TryGetValue(index, out var sources) == true && sources.Length > 0)
        {
            var audio = sources[Math.Clamp(_source.SelectedIndex, 0, sources.Length - 1)];
            _phones.ToolTip = Text("Phonemes") + ": " + audio.Notes.FirstOrDefault(n => n.Index == index)?.Phonemes;
            if (Recipe == null && audio.Notes.FirstOrDefault(n => n.Index == index) is { } isolated)
                _length.ToolTip = Text("SustainAutoLength") + "\n" + Text("GeneratedLength", Number((isolated.NoteEnd - isolated.NoteBegin) * 1000d / audio.Rate));
        }
        _sourceListen.IsEnabled = state?.Views.Count > 0; _preview.IsEnabled = _phrase.IsEnabled = state?.Wave != null;
    }
    private async Task Preview(bool sourceOnly)
    {
        if (!ValidTarget || _note?.Parent is not { } part || VoiceCompHost.View(part) is not { } state) return;
        try
        {
            int index = part.Notes.FindIndex(n => (nint)n == (nint)_note); string? path = sourceOnly ? state.Wave : part.WaveFilePath;
            long begin = 0, length = 0;
            if (sourceOnly && state.Views.TryGetValue(index, out var sources))
            {
                var source = sources[Math.Clamp(_source.SelectedIndex, 0, sources.Length - 1)]; path = source.Path;
                begin = Math.Max(0, source.Notes[0].Boundaries[0] - source.Rate / 10);
                length = Math.Min(source.Samples, source.Notes[^1].Boundaries[^1] + source.Rate / 10) - begin;
            }
            else if (path != null)
            {
                var info = CompWave.Inspect(path); var sequence = part.Sequence!;
                var spans = state.Spans.Where(s => s.NoteIndex == index).ToArray();
                if (spans.Length > 0)
                {
                    begin = Math.Max(0, spans.Min(s => s.Begin) - info.Rate / 10);
                    length = Math.Min(info.Frames, spans.Max(s => s.End) + info.Rate / 5) - begin;
                }
                else
                {
                    begin = Math.Max(0, (long)((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, _note.AbsPosTick) - .2) * info.Rate));
                    length = Math.Min(info.Frames, (long)((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, _note.AbsEndTick) + .2) * info.Rate)) - begin;
                }
            }
            if (path == null || length < 1) return;
            long generation = ++_previewGeneration; string preview = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "VoiceComp", "preview-" + Guid.NewGuid().ToString("N") + ".wav");
            var format = CompWave.Inspect(path);
            await Task.Run(() => CompWave.Mix(preview, new(format.Rate, length, [new(path, begin, 0, length)])));
            if (generation != _previewGeneration) { File.Delete(preview); return; }
            StopPreview(); _previewPath = preview; _player = new SoundPlayer(preview); _player.Play();
        }
        catch (Exception error) { _error.Text = Text("PreviewFailed"); _error.ToolTip = error.Message; }
    }
    private void StopPreview()
    {
        _player?.Stop(); _player?.Dispose(); _player = null;
        if (_previewPath != null) { try { File.Delete(_previewPath); } catch (IOException) { } _previewPath = null; }
    }
    private void PlayPhrase()
    {
        if (!ValidTarget || _note?.Sequence is not { } sequence) return;
        try
        {
            long start = Math.Max(0, sequence.GetTickFromTime(_note.Parent!.AbsPosTick, -sequence.PresendTimeSec).Value);
            App.AudioPlayer?.SetPlayerCurrentPosition(new VSMAbsTick(start), sequence); App.AudioPlayer?.StartPlay();
        }
        catch (Exception error) { _error.Text = Text("PreviewFailed"); _error.ToolTip = error.Message; }
    }
}

internal sealed class CompWaveView : FrameworkElement
{
    private bool _hasTarget;
    private CompSourceAudio[]? _sources;
    private CompSourceAudio[] _outputAudio = [];
    private long _outputBegin, _outputEnd, _noteEnd;
    private int _rate;
    private double _resultMsPerPixel = 1;
    private VoiceCompPlan? _plan;
    private VoiceCompHost.PartState? _state;
    private int _source;
    private Point? _down;
    private double _initial, _candidate;
    private int _gesture;
    private SlicePlacement? _dragSlice;
    private Rect _seamHit, _endHit;
    private readonly Rect[] _windows = new Rect[2];
    private readonly Rect[] _lanes = new Rect[2];
    private readonly Rect[] _lengthHits = new Rect[2];
    private readonly CompSourceNote?[] _sourceNotes = new CompSourceNote?[2];
    private double _msPerPixel = 1;
    internal event Action<double>? OffsetCommitted, SeamCommitted, EndCommitted, LengthCommitted;
    internal event Action<bool, double>? TrimCommitted;
    internal event Action<int>? SourceSelected;
    internal event Action<int>? VowelCommitted;
    internal event Action<bool>? GestureChanged;
    private static readonly Brush Blue = CompEditorPanel.Brush("#78C7E8"), Gold = CompEditorPanel.Brush("#D1B37B"), Muted = CompEditorPanel.Brush("#576573"), TextBrush = CompEditorPanel.Brush("#C7D2DE");
    private static readonly Brush Back = CompEditorPanel.Brush("#14191F"), BlueWindow = CompEditorPanel.Brush("#243A49"), GoldWindow = CompEditorPanel.Brush("#39352C");
    private static readonly Pen BluePen = FrozenPen(Blue), GoldPen = FrozenPen(Gold), MutedPen = FrozenPen(Muted);
    private static readonly Pen GuidePen = FrozenPen(CompEditorPanel.Brush("#596E83"));
    private static Pen FrozenPen(Brush brush) { var pen = new Pen(brush, 1); pen.Freeze(); return pen; }
    internal CompWaveView()
    {
        ClipToBounds = true; Focusable = true;
        MouseLeftButtonDown += (_, e) =>
        {
            if (_plan == null) return;
            Point point = e.GetPosition(this);
            int lengthLane = Array.FindIndex(_lengthHits, r => !r.IsEmpty && r.Contains(point));
            int sourceLane = Array.FindIndex(_lanes, r => !r.IsEmpty && r.Contains(point));
            if (lengthLane < 0 && _plan.HasSources && e.ClickCount == 2 && sourceLane >= 0 && _sources != null && sourceLane < _sources.Length)
            {
                var audio = _sources[sourceLane]; var note = audio.Notes.FirstOrDefault();
                if (note != null)
                {
                    long sample = (long)(Math.Clamp((point.X - 12) / Math.Max(1, ActualWidth - 24), 0, 1) * audio.Samples);
                    int index = Enumerable.Range(0, note.Boundaries.Length - 1).MinBy(i => Math.Abs(note.Boundaries[i] - sample));
                    Cancel(); _source = sourceLane; SourceSelected?.Invoke(sourceLane); VowelCommitted?.Invoke(index); e.Handled = true; return;
                }
            }
            int lane = Array.FindIndex(_windows, w => !w.IsEmpty && w.Contains(point));
            if (lengthLane >= 0)
            { _source = lengthLane; SourceSelected?.Invoke(lengthLane); _gesture = 6; }
            else if (_endHit.Contains(point)) _gesture = 3;
            else if (_seamHit.Contains(point)) _gesture = 2;
            else if (lane >= 0 && _plan.HasSources)
            {
                _source = lane; SourceSelected?.Invoke(lane);
                _gesture = Math.Abs(point.X - _windows[lane].Left) < 7 ? 4 : Math.Abs(point.X - _windows[lane].Right) < 7 ? 5 : 1;
            }
            else return;
            Focus(); _down = point;
            var recipe = _plan.Consonant != null ? _source == 0 ? _plan.Consonant : _plan.Vowel : _plan.Body;
            _initial = _gesture switch { 2 => _plan.SeamMs, 3 => _plan.Isolation?.EndOffsetMs ?? 0,
                4 => recipe?.TrimBeginMs ?? 0, 5 => recipe?.TrimEndMs ?? 0, 6 => SourceLength(_source), _ => recipe?.OffsetMs ?? 0 };
            if (_sources != null && _source < _sources.Length)
                _msPerPixel = _sources[_source].Samples * 1000d / _sources[_source].Rate / Math.Max(1, ActualWidth - 24);
            _dragSlice = SourceSlice(_source);
            _candidate = _initial;
            if (!CaptureMouse()) { Cancel(); return; }
            GestureChanged?.Invoke(true); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (_down == null) { UpdateTip(e.GetPosition(this)); return; }
            UpdateCandidate(e.GetPosition(this), Keyboard.Modifiers); e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_down == null) return; CompleteGesture(); e.Handled = true;
        };
        LostMouseCapture += (_, _) => Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancel(); e.Handled = true; } };
    }
    private void UpdateCandidate(Point point, ModifierKeys modifiers)
    {
        if (_down is not { } down) return;
        double delta = point.X - down.X;
        if (_gesture == 6 && modifiers.HasFlag(ModifierKeys.Shift)) delta /= 10;
        _candidate = _gesture switch { 2 => Math.Clamp(_initial + delta / 4, 0, 30), 3 => Math.Clamp(_initial + delta * _resultMsPerPixel, -1000, 1000),
            4 => Math.Clamp(_initial + delta * _msPerPixel, 0, 60000), 5 => Math.Clamp(_initial - delta * _msPerPixel, 0, 60000),
            // Source WAVs can contain a whole Part's silent padding. Scrub
            // duration at a note-relative speed instead of that padded scale.
            6 => Math.Clamp(Math.Round(_initial + delta * Math.Clamp(_initial / 120, 1, 100)), 10, 60000),
            _ => Math.Clamp(_initial + delta * _msPerPixel, -60000, 60000) };
        if (_gesture is 4 or 5 && modifiers.HasFlag(ModifierKeys.Shift) && _dragSlice is { } slice &&
            _sources is { } sources && _source < sources.Length)
        {
            var audio = sources[_source]; long origin = _gesture == 4 ? slice.SourceBegin : slice.SourceBegin + slice.Length;
            double direction = _gesture == 4 ? 1 : -1;
            double sample = origin + direction * (_candidate - _initial) * audio.Rate / 1000;
            long boundary = audio.Notes.SelectMany(n => n.Boundaries).MinBy(p => Math.Abs(p - sample));
            _candidate = Math.Clamp(_initial + direction * (boundary - origin) * 1000d / audio.Rate, 0, 60000);
        }
        InvalidateVisual();
    }
    private void CompleteGesture()
    {
        if (_down == null) return;
        int gesture = _gesture; double candidate = _candidate; bool moved = Math.Abs(candidate - _initial) > .01;
        Cancel();
        if (!moved) return;
        if (gesture == 2) SeamCommitted?.Invoke(candidate); else if (gesture == 3) EndCommitted?.Invoke(candidate);
        else if (gesture == 6) LengthCommitted?.Invoke(candidate);
        else if (gesture is 4 or 5) TrimCommitted?.Invoke(gesture == 4, candidate); else OffsetCommitted?.Invoke(candidate);
    }
    private double SourceLength(int lane)
    {
        var recipe = _plan?.Consonant != null ? lane == 0 ? _plan.Consonant : _plan.Vowel : _plan?.Body;
        double value = recipe?.LengthMs ?? _plan?.Isolation?.SourceLengthMs ?? 0;
        if (value > 0) return value;
        return _sources != null && lane < _sources.Length && _sourceNotes[lane] is { } note
            ? (note.NoteEnd - note.NoteBegin) * 1000d / _sources[lane].Rate : 0;
    }
    internal void Update(WIVSMNote? note, VoiceCompPlan? plan, VoiceCompHost.PartState? state, int source)
    {
        _hasTarget = note != null; _plan = plan; _state = state; _source = source; _sources = null;
        Array.Clear(_sourceNotes);
        _outputAudio = state?.Views.Values.SelectMany(v => v).DistinctBy(a => a.Path).ToArray() ?? [];
        _outputBegin = 0; _outputEnd = state?.Composition?.Samples ?? 1; _rate = state?.Composition?.SampleRate ?? 44100;
        if (note?.Parent is { } part && part.Sequence is { } sequence)
        {
            int index = part.Notes.FindIndex(n => (nint)n == (nint)note);
            state?.Views.TryGetValue(index, out _sources);
            if (_sources != null)
                for (int i = 0; i < Math.Min(2, _sources.Length); i++) _sourceNotes[i] = _sources[i].Notes.FirstOrDefault(n => n.Index == index);
            long At(VSMAbsTick tick) => (long)Math.Round((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, tick)) * _rate);
            _outputBegin = Math.Max(0, At(note.AbsPosTick) - _rate / 5);
            _noteEnd = At(note.AbsEndTick);
            _outputEnd = Math.Max(_outputBegin + 1, Math.Min(state?.Composition?.Samples ?? long.MaxValue,
                _noteEnd + (long)(_rate * Math.Max(.15, (plan?.Isolation?.EndOffsetMs ?? 0) / 1000 + .1))));
        }
        InvalidateVisual();
    }
    private void UpdateTip(Point point)
    {
        if (_lengthHits.Any(r => !r.IsEmpty && r.Contains(point)))
        { Cursor = Cursors.SizeWE; ToolTip = CompEditorPanel.Text("DragSourceLength"); return; }
        ClearValue(CursorProperty);
        int lane = Array.FindIndex(_lanes, w => !w.IsEmpty && w.Contains(point));
        if (lane < 0 || _sources == null || lane >= _sources.Length || ActualWidth <= 24) { ToolTip = null; return; }
        var audio = _sources[lane];
        long sample = (long)(Math.Clamp((point.X - 12) / (ActualWidth - 24), 0, 1) * audio.Samples);
        var slice = SourceSlice(lane);
        var span = audio.Notes.SelectMany(n => n.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((phone, i) => (Phone: phone, Begin: n.Boundaries[i], End: n.Boundaries[i + 1])))
            .FirstOrDefault(s => sample >= s.Begin && sample < s.End);
        string output = slice != null ? ((slice.OutputBegin + sample - slice.SourceBegin) * 1000d / audio.Rate).ToString("0.0", CultureInfo.CurrentCulture) : "—";
        ToolTip = CompEditorPanel.Text("Source") + " " + (sample * 1000d / audio.Rate).ToString("0.0", CultureInfo.CurrentCulture) +
            " ms → " + CompEditorPanel.Text("Result") + " " + output + " ms · " + span.Phone + "\n" + CompEditorPanel.Text("PickVowel");
    }
    internal void Cancel() { bool active = _down != null; _down = null; if (IsMouseCaptured) ReleaseMouseCapture(); if (active) GestureChanged?.Invoke(false); InvalidateVisual(); }
    private SlicePlacement? SourceSlice(int lane)
    {
        if (_sources == null || lane >= _sources.Length) return null;
        var role = _plan?.Consonant != null ? lane == 0 ? CompSourceRole.Consonant : CompSourceRole.Vowel :
            _plan?.Body != null ? CompSourceRole.Body : CompSourceRole.Connected;
        return _state?.Composition?.Slices.FirstOrDefault(s => s.Path == _sources[lane].Path && s.Role == role);
    }
    protected override void OnRender(DrawingContext dc)
    {
        Array.Fill(_lengthHits, Rect.Empty);
        dc.DrawRoundedRectangle(Back, null, new Rect(RenderSize), 4, 4);
        if (ActualWidth < 40 || ActualHeight < 40) return;
        if (!_hasTarget || _state == null)
        { Caption(dc, CompEditorPanel.Text("SelectNote"), 12, 16, TextBrush); return; }
        var sources = _sources;
        bool shortView = ActualHeight < 145;
        double resultCenter = shortView ? 26 : 34;
        double top = shortView ? 40 : 52, row = Math.Max(22, (ActualHeight - top - 8) / (_plan?.Consonant != null ? 2 : 1)), left = 12, width = ActualWidth - 24;
        Caption(dc, CompEditorPanel.Text("Result"), left, 5, TextBrush);
        var composition = _state.Composition;
        long outputBegin = _outputBegin, outputEnd = _outputEnd;
        _resultMsPerPixel = (outputEnd - outputBegin) * 1000d / _rate / width;
        double anchor = left;
        var consonant = composition?.Slices.FirstOrDefault(s => s.Role == CompSourceRole.Consonant && s.OutputBegin >= outputBegin && s.OutputBegin < outputEnd);
        if (consonant != null) anchor = left + (consonant.OutputEnd - consonant.FadeOut / 2d - outputBegin) * width / Math.Max(1, outputEnd - outputBegin);
        if (_state.ResultPeaks.Length > 0 && composition != null)
        {
            float maximum = Math.Max(.01f, _state.ResultPeaks.Max());
            for (int x = 0; x < (int)width; x += 2)
            {
                long sample = outputBegin + (long)(x / width * (outputEnd - outputBegin));
                int bin = (int)Math.Clamp(sample * _state.ResultPeaks.Length / composition.Samples, 0, _state.ResultPeaks.Length - 1);
                double amplitude = _state.ResultPeaks[bin] / maximum * 11;
                dc.DrawLine(BluePen, new(left + x, resultCenter - amplitude), new(left + x, resultCenter + amplitude));
            }
            // Draw both real source envelopes over an overlap; the mixed PCM
            // remains the waveform below them. No fabricated preview curves.
            foreach (var slice in composition.Slices.Where(s => s.OutputBegin < outputEnd && s.OutputEnd > outputBegin))
            {
                var source = _outputAudio.FirstOrDefault(a => a.Path == slice.Path); if (source == null) continue;
                float sourceMaximum = Math.Max(.01f, source.Peaks.Max());
                double x0 = Math.Max(0, (slice.OutputBegin - outputBegin) * width / (outputEnd - outputBegin));
                double x1 = Math.Min(width, (slice.OutputEnd - outputBegin) * width / (outputEnd - outputBegin));
                var envelope = new StreamGeometry();
                using (var geometry = envelope.Open())
                    for (double x = x0; x <= x1; x += 2)
                    {
                        long at = (long)(outputBegin + x * (outputEnd - outputBegin) / width - slice.OutputBegin);
                        long sample = slice.SourceBegin + at;
                        int bin = (int)Math.Clamp(sample * source.Peaks.Length / source.Samples, 0, source.Peaks.Length - 1);
                        double amplitude = source.Peaks[bin] / sourceMaximum * 10 * CompWave.Envelope(at, slice.Length, slice.FadeIn, slice.FadeOut);
                        var point = new Point(left + x, resultCenter + (slice.Role == CompSourceRole.Vowel ? amplitude : -amplitude));
                        if (x == x0) geometry.BeginFigure(point, false, false); else geometry.LineTo(point, true, false);
                    }
                envelope.Freeze(); dc.DrawGeometry(null, slice.Role == CompSourceRole.Vowel ? GoldPen : MutedPen, envelope);
            }
        }
        _seamHit = consonant == null ? Rect.Empty : new Rect(Math.Max(left, anchor - 12), resultCenter - 14, 24, 26);
        double endMs = _down != null && _gesture == 3 ? _candidate : _plan?.Isolation?.EndOffsetMs ?? 0;
        double endX = Math.Clamp(left + (_noteEnd + endMs * _rate / 1000 - outputBegin) * width / Math.Max(1, outputEnd - outputBegin), left, left + width);
        _endHit = new Rect(endX - 9, resultCenter - 14, 18, 26);
        var diamond = new StreamGeometry(); using (var context = diamond.Open())
        { context.BeginFigure(new(anchor, resultCenter - 12), true, true); context.LineTo(new(anchor + 4, resultCenter - 7), true, false); context.LineTo(new(anchor, resultCenter - 2), true, false); context.LineTo(new(anchor - 4, resultCenter - 7), true, false); }
        diamond.Freeze(); dc.DrawGeometry(Blue, null, diamond);
        if (consonant != null && anchor > 200 && anchor < ActualWidth - 90) Caption(dc, CompEditorPanel.Text("Entrance"), anchor + 9, 10, TextBrush);
        double seam = _down != null && _gesture == 2 ? _candidate : _plan?.SeamMs ?? 5;
        var curve = new StreamGeometry(); using (var context = curve.Open())
        { context.BeginFigure(new(anchor - 14 - seam, resultCenter - 7), false, false); context.BezierTo(new(anchor, resultCenter - 7), new(anchor, resultCenter + 7), new(anchor + 14 + seam, resultCenter + 7), true, false); }
        curve.Freeze(); dc.DrawGeometry(null, BluePen, curve);
        if (consonant != null)
        {
            var rise = new StreamGeometry(); using (var context = rise.Open())
            { context.BeginFigure(new(anchor - 14 - seam, resultCenter + 7), false, false); context.BezierTo(new(anchor, resultCenter + 7), new(anchor, resultCenter - 7), new(anchor + 14 + seam, resultCenter - 7), true, false); }
            rise.Freeze(); dc.DrawGeometry(null, GoldPen, rise);
        }
        dc.DrawRectangle(Gold, null, new Rect(endX - 2, resultCenter - 9, 5, 17));
        for (int i = 0; i < 2; i++)
        {
            double y = top + i * row; var audio = sources != null && i < sources.Length ? sources[i] : null;
            _windows[i] = Rect.Empty;
            _lanes[i] = audio == null ? Rect.Empty : new Rect(left, y + 10, width, Math.Max(1, row - 10));
            if (audio == null && i == 1 && _plan?.Consonant == null) continue;
            var color = i == 0 ? Blue : Gold;
            Caption(dc, CompEditorPanel.Text(_plan?.Consonant != null ? i == 0 ? "Consonant" : "VowelSource" : _plan?.Body != null ? "Body" : "IndependentSource"), left, y, TextBrush);
            if (audio == null) { Caption(dc, CompEditorPanel.Text(_state.Status), left + 110, y, Muted); continue; }
            double center = y + row * (row < 54 ? .68 : .56), height = Math.Max(3, row * (row < 54 ? .2 : .16));
            var slice = SourceSlice(i);
            double start = slice?.SourceBegin / (double)Math.Max(1, audio.Samples) ?? 0;
            double end = slice == null ? 1 : (slice.SourceBegin + slice.Length) / (double)Math.Max(1, audio.Samples);
            if (i == _source && _down == null) _msPerPixel = audio.Samples * 1000d / audio.Rate / width;
            if (_down != null && i == _source)
            {
                double shift = (_candidate - _initial) * audio.Rate / 1000 / audio.Samples;
                if (_gesture is 1 or 4) start += shift;
                if (_gesture == 1) end += shift; else if (_gesture == 5) end -= shift;
            }
            start = Math.Clamp(start, 0, 1); end = Math.Clamp(end, start, 1);
            _windows[i] = new Rect(left + start * width - 6, center - height - 6, Math.Max(12, (end - start) * width + 12), height * 2 + 12);
            dc.DrawRectangle(i == 0 ? BlueWindow : GoldWindow, null, new Rect(left + start * width, center - height - 3, (end - start) * width, height * 2 + 6));
            int bars = Math.Min(audio.Peaks.Length, Math.Max(1, (int)width / 2));
            float peak = Math.Max(.01f, audio.Peaks.Max());
            for (int b = 0; b < bars; b++)
            {
                double x = b / (double)Math.Max(1, bars - 1), amplitude = audio.Peaks[b * audio.Peaks.Length / bars] / peak * height;
                dc.DrawLine(x >= start && x <= end ? i == 0 ? BluePen : GoldPen : MutedPen, new(left + x * width, center - amplitude), new(left + x * width, center + amplitude));
            }
            dc.DrawRectangle(color, null, new Rect(left + start * width - 2, center - height - 3, 4, height * 2 + 6));
            dc.DrawRectangle(color, null, new Rect(left + end * width - 2, center - height - 3, 4, height * 2 + 6));
            if (i == _source && slice != null)
            {
                double outputX = Math.Clamp(left + (slice.OutputBegin - outputBegin) * width / Math.Max(1, outputEnd - outputBegin), left, left + width);
                dc.DrawLine(GuidePen, new(left + start * width, center - height - 5), new(outputX, top - 5));
            }
            if (row >= 54)
            {
                Caption(dc, "0", left, y + 15, Muted);
                Caption(dc, (audio.Samples * 1000d / audio.Rate).ToString("0", CultureInfo.CurrentCulture) + " ms", Math.Max(left, ActualWidth - 90), y + 15, Muted);
                var phones = audio.Notes.FirstOrDefault();
                if (phones != null)
                {
                    var tokens = phones.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (int phone = 0; phone < tokens.Length; phone++)
                    {
                        double x = left + phones.Boundaries[phone] * width / audio.Samples;
                        double next = left + phones.Boundaries[phone + 1] * width / audio.Samples;
                        dc.DrawLine(MutedPen, new(x, center - height), new(x, y + row - 13));
                        if (next - x > 18) Caption(dc, tokens[phone], x + 2, y + row - 13, color);
                    }
                }
            }
            if (_sourceNotes[i] is { } sourceNote && (_plan?.HasSources == true || _plan?.Isolation?.After == true))
            {
                double duration = _down != null && _gesture == 6 && i == _source ? _candidate : SourceLength(i);
                double startX = Math.Clamp(left + sourceNote.NoteBegin * width / audio.Samples, left, left + width);
                double lengthX = Math.Clamp(left + (sourceNote.NoteBegin + duration * audio.Rate / 1000) * width / audio.Samples, left, left + width);
                // A separate arrow tab above the material window changes the
                // synthesis duration; rectangular crop grips keep their role.
                double tabWidth = Math.Min(width, 126), tabHeight = Math.Min(22, Math.Max(18, row * .3));
                double tabLeft = Math.Clamp(lengthX - tabWidth / 2, Math.Min(left + 104, left + width - tabWidth), left + width - tabWidth);
                var tab = new Rect(tabLeft, y + 1, tabWidth, tabHeight);
                _lengthHits[i] = new Rect(tab.Left - 2, tab.Top - 3, tab.Width + 4, Math.Max(28, tab.Height + 6));
                dc.DrawLine(i == 0 ? BluePen : GoldPen, new(startX, tab.Bottom + 2), new(lengthX, tab.Bottom + 2));
                dc.DrawLine(i == 0 ? BluePen : GoldPen, new(lengthX, tab.Bottom), new(lengthX, tab.Bottom + 5));
                dc.DrawRoundedRectangle(i == 0 ? BlueWindow : GoldWindow, i == 0 ? BluePen : GoldPen, tab, 4, 4);
                Caption(dc, CompEditorPanel.Text("SourceLengthHandle", duration.ToString("0", CultureInfo.CurrentCulture)), tab.Left + 6, tab.Top + 2, color);
            }
        }
    }
    private void Caption(DrawingContext dc, string value, double x, double y, Brush brush)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, ActualWidth - x - 4), Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new(x, y));
    }
}
