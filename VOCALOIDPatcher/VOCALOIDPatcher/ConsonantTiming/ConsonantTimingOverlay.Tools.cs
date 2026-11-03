using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID.MusicalEditor;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private readonly Canvas _acousticToolsLayer = new() { Visibility = Visibility.Collapsed };
    private TimingToolBar? _editTools;
    private AcousticEditMode _acousticMode;
    private bool _changingAcousticMode;
    private bool TransitionMode => Settings.IndividualTransitionControl && _acousticMode == AcousticEditMode.Transitions;

    private void AttachAcousticToolBar(Grid panel)
    {
        _editTools = new TimingToolBar(
            TranslationManager.Tr("VOCALOIDPatcher_TimingTools_Name"), TransitionText("Phonemes"), TransitionText("Tool"),
            TranslationManager.Tr("VOCALOIDPatcher_TimingTools_Precise"),
            TranslationManager.Tr("VOCALOIDPatcher_TimingTools_PhonemeHint"), TransitionText("Hint"),
            TranslationManager.Tr("VOCALOIDPatcher_TimingTools_PreciseHint"));
        WpfTranslationPatch.MarkUntranslatable(_editTools);
        _editTools.ModeRequested += mode =>
        {
            try { SetAcousticMode(mode); }
            catch (Exception error) { Fail(_view, error); }
        };
        _editTools.CompActionRequested += action => TryCompAction(action);
        _editTools.PreciseButton.Click += (_, _) =>
        {
            try
            {
                var button = _editTools.PreciseButton;
                OpenTransitionPanel(button.TranslatePoint(new Point(0, button.ActualHeight), _view));
            }
            catch (Exception error) { TransitionFailure(error); }
        };
        _acousticToolsLayer.Children.Add(_editTools);
        Panel.SetZIndex(_acousticToolsLayer, 100);
        panel.Children.Add(_acousticToolsLayer);
    }

    private void SetAcousticMode(AcousticEditMode mode)
    {
        if (_changingAcousticMode || mode == _acousticMode) return;
        _changingAcousticMode = true;
        try
        {
            _acousticMode = mode;
            _editTools?.Select(mode);
            InvalidateDurationContext(); InvalidateTransitionContext(); CancelVolumeDrag(); CloseVolumePanel();
            CancelPhonemeDrag(); CancelDrag(); _durationHover = null; _tip.IsOpen = false;
            ClearPhonemeHover(); SetHover(null); RequestRefresh();
        }
        finally { _changingAcousticMode = false; }
    }
    private bool HitAcousticTool(DependencyObject target) => _editTools?.Owns(target) == true;

    private void RefreshAcousticTools(MusicalEditorViewModel vm)
    {
        _acousticToolsLayer.Width = Math.Max(vm.SongWidth, _view.ActualWidth);
        _acousticToolsLayer.Height = Math.Max(vm.AllKeyHeight, _view.ActualHeight);
        bool phonemes = Settings.IntuitiveConsonantTiming || Settings.IndividualPhonemeVolume;
        _acousticToolsLayer.Visibility = (phonemes || Settings.IndividualTransitionControl || Settings.VoiceCompEnabled) && vm.ActivePart?.IsAi == false ? Visibility.Visible : Visibility.Collapsed;
        if (_editTools != null)
        {
            _editTools.SetAvailability(phonemes, Settings.IndividualTransitionControl, Settings.VoiceCompEnabled);
            if (!Settings.IndividualTransitionControl && _acousticMode == AcousticEditMode.Transitions) SetAcousticMode(AcousticEditMode.Phonemes);
            else if (!phonemes && Settings.IndividualTransitionControl && _acousticMode == AcousticEditMode.Phonemes) SetAcousticMode(AcousticEditMode.Transitions);
            Canvas.SetLeft(_editTools, (vm.PianorollViewer?.HorizontalOffset ?? 0) + 10);
            Canvas.SetTop(_editTools, (vm.PianorollViewer?.VerticalOffset ?? 0) + 8);
            _editTools.Fit(Math.Max(0, (vm.PianorollViewer?.ViewportWidth ?? _view.ActualWidth) - 20));
        }
    }
}
