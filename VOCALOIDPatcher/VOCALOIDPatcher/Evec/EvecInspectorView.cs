using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Evec;

internal sealed class EvecInspectorView : UserControl
{
    private static readonly Brush HeaderForeground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
    private static readonly Brush LabelForeground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush PanelBackground = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x2A));
    private static readonly Brush BorderBrushColor = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x3C));

    private readonly ComboBox _colorCombo;
    private readonly ComboBox _attackCombo;
    private readonly ComboBox _releaseCombo;
    private readonly UniformGrid _extensionButtonPanel;
    private readonly ToggleButton[] _extensionButtons;
    private readonly TextBlock _titleText;
    private readonly TextBlock _colorLabel;
    private readonly TextBlock _attackLabel;
    private readonly TextBlock _extensionLabel;
    private readonly TextBlock _releaseLabel;

    private bool _isUpdatingUi;
    private bool _syncPending;
    private VoiceBank? _currentVoiceBank;
    private EvecVoicebankCapabilities _currentCapabilities = EvecVoicebankCapabilities.None;

    public EvecInspectorView()
    {
        Background = PanelBackground;
        BorderBrush = BorderBrushColor;
        BorderThickness = new Thickness(1);
        Margin = new Thickness(8, 6, 8, 6);
        Padding = new Thickness(8, 6, 8, 8);

        var root = new StackPanel();

        // Title row
        _titleText = new TextBlock
        {
            Text = "EVEC",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Foreground = HeaderForeground,
            Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(_titleText);

        // Voice Color row
        _colorLabel = CreateLabel("VOCALOIDPatcher_Evec_VoiceColor");
        _colorCombo = CreateComboBox();
        _colorCombo.SelectionChanged += OnColorSelectionChanged;
        root.Children.Add(CreateRow(_colorLabel, _colorCombo));

        // Voice Release row
        _releaseLabel = CreateLabel("VOCALOIDPatcher_Evec_Release");
        _releaseCombo = CreateComboBox();
        _releaseCombo.SelectionChanged += OnReleaseSelectionChanged;
        root.Children.Add(CreateRow(_releaseLabel, _releaseCombo));

        // CTop recording character (Mild/Accent).
        _attackLabel = CreateLabel("VOCALOIDPatcher_Evec_Attack");
        _attackCombo = CreateComboBox();
        _attackCombo.SelectionChanged += OnAttackSelectionChanged;
        root.Children.Add(CreateRow(_attackLabel, _attackCombo));

        // Independent Piapro top-consonant repeat count (0-3).
        _extensionLabel = CreateLabel("VOCALOIDPatcher_Evec_ConsonantExtension");
        _extensionButtonPanel = new UniformGrid
        {
            Columns = EvecConstants.MaxConsonantExtension - EvecConstants.MinConsonantExtension + 1,
            Rows = 1,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _extensionButtons = new ToggleButton[_extensionButtonPanel.Columns];
        for (int value = EvecConstants.MinConsonantExtension;
             value <= EvecConstants.MaxConsonantExtension;
             value++)
        {
            var button = new ToggleButton
            {
                Tag = value,
                FontSize = 10.5,
                Focusable = false,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(value == EvecConstants.MinConsonantExtension ? 0 : 1, 0, 0, 0)
            };
            button.Click += OnExtensionButtonClick;
            _extensionButtons[value - EvecConstants.MinConsonantExtension] = button;
            _extensionButtonPanel.Children.Add(button);
        }
        root.Children.Add(CreateRow(_extensionLabel, _extensionButtonPanel));

        Content = root;
        Visibility = Visibility.Collapsed;

        TranslationManager.LanguageChanged += OnLanguageChanged;
        EvecService.Changed += OnEvecServiceChanged;
    }

    private void OnEvecServiceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)OnEvecServiceChanged);
            return;
        }

        if (_isUpdatingUi || Visibility != Visibility.Visible)
            return;

        if (_syncPending)
            return;

        _syncPending = true;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            _syncPending = false;
            if (_isUpdatingUi || Visibility != Visibility.Visible)
                return;

            var vm = (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
            var selectedNotes = vm?.ActiveTrack?.SelectedNotes;
            if (selectedNotes == null || selectedNotes.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            SyncSelection(selectedNotes);
        }), DispatcherPriority.DataBind);
    }

    private void OnLanguageChanged(object? sender, string lang)
    {
        UpdateLabels();
        PopulateCombos();
    }

    private void UpdateLabels()
    {
        _titleText.Text = TranslationManager.Tr("VOCALOIDPatcher_Evec_Title");
        _colorLabel.Text = TranslationManager.Tr("VOCALOIDPatcher_Evec_VoiceColor");
        _attackLabel.Text = TranslationManager.Tr("VOCALOIDPatcher_Evec_Attack");
        _extensionLabel.Text = TranslationManager.Tr("VOCALOIDPatcher_Evec_ConsonantExtension");
        _releaseLabel.Text = TranslationManager.Tr("VOCALOIDPatcher_Evec_Release");
        UpdateExtensionButtonLabels();
    }

    private static TextBlock CreateLabel(string key)
    {
        return new TextBlock
        {
            Text = TranslationManager.Tr(key),
            Foreground = LabelForeground,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 105
        };
    }

    private static ComboBox CreateComboBox()
    {
        return new ComboBox
        {
            Height = 22,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static Grid CreateRow(TextBlock label, FrameworkElement editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(label, 0);
        Grid.SetColumn(editor, 1);

        grid.Children.Add(label);
        grid.Children.Add(editor);
        return grid;
    }

    internal void UpdateView(MusicalEditorViewModel? vm)
    {
        if (!EvecService.IsEnabled || vm == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var activeTrack = vm.ActiveTrack;
        if (activeTrack == null || activeTrack.Type != VSMTrackType.Midi)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var selectedNotes = activeTrack.SelectedNotes;
        if (selectedNotes == null || selectedNotes.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var activePart = vm.ActivePart;
        var voiceBank = activePart?.VoiceBank();
        if (voiceBank == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var capabilities = EvecVoicebankDetector.GetCapabilities(voiceBank);
        if (!capabilities.IsSupported)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        UpdateLabels();

        if (!ReferenceEquals(_currentVoiceBank, voiceBank) || !ReferenceEquals(_currentCapabilities, capabilities))
        {
            _currentVoiceBank = voiceBank;
            _currentCapabilities = capabilities;
            PopulateCombos();
        }

        SyncSelection(selectedNotes);
    }

    private void PopulateCombos()
    {
        _isUpdatingUi = true;
        try
        {
            // 1. Color combo
            _colorCombo.Items.Clear();
            foreach (var opt in _currentCapabilities.Colors)
            {
                var text = TranslationManager.Tr(opt.DisplayKey);
                if (!string.IsNullOrEmpty(opt.Suffix))
                    text += $" ({opt.Suffix})";
                _colorCombo.Items.Add(new ComboBoxItemWrapper(opt.Id, text));
            }
            _colorCombo.IsEnabled = _currentCapabilities.HasColors;

            // 2. Release combo
            _releaseCombo.Items.Clear();
            foreach (var opt in _currentCapabilities.Releases)
            {
                var text = TranslationManager.Tr(opt.DisplayKey);
                if (!string.IsNullOrEmpty(opt.Suffix))
                    text += $" ({opt.Suffix})";
                _releaseCombo.Items.Add(new ComboBoxItemWrapper(opt.Id, text));
            }
            _releaseCombo.IsEnabled = _currentCapabilities.HasReleases;

            // 3. CTop recording character combo
            _attackCombo.Items.Clear();
            foreach (var opt in _currentCapabilities.Attacks)
            {
                var text = TranslationManager.Tr(opt.DisplayKey);
                if (!string.IsNullOrEmpty(opt.Suffix))
                    text += $" ({opt.Suffix})";
                _attackCombo.Items.Add(new ComboBoxItemWrapper(opt.Id, text));
            }
            _attackCombo.IsEnabled = _currentCapabilities.HasAttacks;

            // 4. Piapro exposes the independent repeat count as four fixed
            // toggle buttons (off, x1, x2, x3), not a drop-down. Keep all
            // four visible so a voicebank restriction reads as unavailable
            // instead of looking like another option changed the value.
            UpdateExtensionButtons(EvecConstants.MaxConsonantExtension, null);
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void SyncSelection(List<WIVSMNote> selectedNotes)
    {
        _isUpdatingUi = true;
        try
        {
            var states = selectedNotes.Select(EvecService.GetState).ToList();
            var state = states[0];

            int? colorId = CommonValue(states, item => item.VoiceColorId);
            if (colorId.HasValue && (!_currentCapabilities.HasColors ||
                                     !_currentCapabilities.Colors.Any(c => c.Id == colorId.Value)))
                colorId = EvecConstants.VoiceColorNone;

            int? attackId = CommonValue(states, item => item.AttackId);
            if (attackId.HasValue && (!_currentCapabilities.HasAttacks ||
                                      !_currentCapabilities.Attacks.Any(a => a.Id == attackId.Value)))
                attackId = EvecConstants.AttackNone;

            int? releaseId = CommonValue(states, item => item.ReleaseId);
            if (releaseId.HasValue && (!_currentCapabilities.HasReleases ||
                                       !_currentCapabilities.Releases.Any(r => r.Id == releaseId.Value)))
                releaseId = EvecConstants.ReleaseNone;

            int? extension = CommonValue(states, item => item.ConsonantExtension);
            int maximumExtension = selectedNotes
                .Select(note => _currentCapabilities.MaximumSelectableConsonantExtension(note.Phonemes))
                .DefaultIfEmpty(EvecConstants.MinConsonantExtension)
                .Min();

            SelectComboItem(_colorCombo, colorId);
            SelectComboItem(_attackCombo, attackId);
            SelectComboItem(_releaseCombo, releaseId);
            UpdateExtensionButtons(maximumExtension, extension);
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private static int? CommonValue(
        IReadOnlyList<EvecNoteState> states,
        Func<EvecNoteState, int> selector)
    {
        if (states.Count == 0)
            return null;

        int value = selector(states[0]);
        return states.Skip(1).All(item => selector(item) == value) ? value : null;
    }

    private void UpdateExtensionButtonLabels()
    {
        foreach (var button in _extensionButtons)
        {
            int value = (int)button.Tag;
            button.Content = value == EvecConstants.MinConsonantExtension
                ? TranslationManager.Tr("VOCALOIDPatcher_Evec_Extension_None")
                : $"×{value}";
        }
    }

    private void UpdateExtensionButtons(int maximumExtension, int? selectedValue)
    {
        maximumExtension = Math.Clamp(
            maximumExtension,
            EvecConstants.MinConsonantExtension,
            EvecConstants.MaxConsonantExtension);
        UpdateExtensionButtonLabels();

        foreach (var button in _extensionButtons)
        {
            int value = (int)button.Tag;
            button.IsEnabled = _currentCapabilities.HasConsonantExtension && value <= maximumExtension;
            button.IsChecked = selectedValue.HasValue && selectedValue.Value == value;
        }
    }

    private static void SelectComboItem(ComboBox combo, int? targetId)
    {
        if (!targetId.HasValue)
        {
            combo.SelectedIndex = -1;
            return;
        }

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItemWrapper wrapper && wrapper.Id == targetId.Value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    private void OnExtensionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || sender is not ToggleButton { Tag: int extension } button)
            return;

        _isUpdatingUi = true;
        try
        {
            foreach (var candidate in _extensionButtons)
                candidate.IsChecked = ReferenceEquals(candidate, button);
        }
        finally
        {
            _isUpdatingUi = false;
        }

        var notes = GetSelectedNotes();
        if (notes.Count > 0)
            EvecService.UpdateConsonantExtension(notes, extension);
    }

    private void OnColorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi || _colorCombo.SelectedItem is not ComboBoxItemWrapper wrapper) return;
        var notes = GetSelectedNotes();
        if (notes.Count > 0)
            EvecService.UpdateVoiceColor(notes, wrapper.Id);
    }

    private void OnAttackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi || _attackCombo.SelectedItem is not ComboBoxItemWrapper wrapper) return;
        var notes = GetSelectedNotes();
        if (notes.Count > 0)
            EvecService.UpdateAttack(notes, wrapper.Id);
    }

    private void OnReleaseSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi || _releaseCombo.SelectedItem is not ComboBoxItemWrapper wrapper) return;
        var notes = GetSelectedNotes();
        if (notes.Count > 0)
            EvecService.UpdateRelease(notes, wrapper.Id);
    }

    private static List<WIVSMNote> GetSelectedNotes()
    {
        var vm = (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
        return vm?.ActiveTrack?.SelectedNotes ?? new List<WIVSMNote>();
    }

    private sealed class ComboBoxItemWrapper
    {
        public int Id { get; }
        public string Text { get; }

        public ComboBoxItemWrapper(int id, string text)
        {
            Id = id;
            Text = text;
        }

        public override string ToString() => Text;
    }
}
