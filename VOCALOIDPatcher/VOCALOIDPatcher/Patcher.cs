using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Windows.Controls;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Jobs;
using VOCALOIDPatcher.Mcp;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.RegisterShift;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.UI;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;

namespace VOCALOIDPatcher;

public static class Patcher
{
    public static readonly bool DebugMode = KeyState.IsKeyDown(0xA0);

    public static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VOCALOIDPatcher");

    public static readonly string ConfigFile =
        Path.Combine(ConfigDir, "config.json");

    public static string DataDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VOCALOIDPatcher");

    public static ConfigManager ConfigManager = null!;

    private static Harmony _harmony = null!;

    public static bool VstPluginMode;

    public static string Version => "2.3.1";

#pragma warning disable CA2255
    [ModuleInitializer]
    public static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            if (args.Name.StartsWith("VOCALOID6"))
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetName().Name?.StartsWith("VOCALOID6") == true)
                        return assembly;

            return null;
        };

        try
        {
            PatcherInit();
        }
        catch (Exception e)
        {
            Debug.ShowErrorMessage(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_InitFailed"), e);
        }
    }

    private static void PatcherInit()
    {
        var patcherInitStarted = Stopwatch.GetTimestamp();
        if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);

        StartupProfiler.InitializeLog();

        VstPluginMode = DetectVstPluginMode();

        if (VstPluginMode)
            DataDir = Path.Combine(new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOCALOID6", "Editor",
                "VOCALOIDPatcher"
            });

        ConfigManager = new ConfigManager(ConfigFile);
        DsePreloader.Start();
        _harmony = new Harmony("VOCALOIDPatcher");

        ConsoleHelper.InitConsole();

        TranslationManager.Initialize();

        Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_Started"));
        Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_Version", Version));
        Debug.Print("https://github.com/IzumiiKonata/VOCALOIDPatcher");

        var version = typeof(App).Assembly.GetName().Version;
        Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_EditorVersion", version));
        Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_TranslationInitialized"));

        if (VstPluginMode)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_VstMode"));
            VstPluginPatch.ApplyPatches(_harmony);
        }

        var patchInstallStarted = Stopwatch.GetTimestamp();
        StartupProfiler.Install(_harmony);
        FirstTrackProfiler.Install(_harmony);
        ApplyPatches();
        StartupProfiler.LogMilestone(
            "All Harmony patches installed",
            Stopwatch.GetElapsedTime(patchInstallStarted).TotalMilliseconds);

        WpfTranslationPatch.InstallGlobalHandlers();

        if (!VstPluginMode)
        {
            var postInjectStarted = Stopwatch.GetTimestamp();
            PostInject();
            StartupProfiler.LogMilestone(
                "Patcher UI and services injected",
                Stopwatch.GetElapsedTime(postInjectStarted).TotalMilliseconds);
        }

        UpdateChecker.CheckAsync();
        StartupProfiler.LogMilestone(
            "Patcher initialization complete",
            Stopwatch.GetElapsedTime(patcherInitStarted).TotalMilliseconds);
    }

    public static void PostInject()
    {
        AddPatcherMenuItem();
        JobMenu.Install();
        Formats.LibreSvip.SvipFormatMenu.Install();
        Formats.LibreSvip.SvipFormatDragDrop.Install();
        AutoSaveService.UpdateFromSettings();
        WorkingSetTrimmer.Install();
        SpectrumWidget.Install();
        McpBridgeService.Install();
    }

    private static bool DetectVstPluginMode()
    {
        try
        {
            ReflectionUtils.GetMainWindow();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private sealed class FeatureGroup
    {
        public readonly string? FeatureKey;
        public readonly PatchBase[] Patches;

        public FeatureGroup(string? featureKey, params PatchBase[] patches)
        {
            FeatureKey = featureKey;
            Patches = patches;
        }
    }

    private static void ApplyPatches()
    {
        var groups = new List<FeatureGroup>
        {
            new(null,
                new UnhandledExceptionDialogPatch(),
                new AppLanguagePatch(),
                new WpfTranslationPatch(),
                new ResourceManagerPatch(),
                new AudioEffectWindowTranslatePatch(),
                new SwingMenuPatch(),
                new TouchpadHorizontalScrollPatch(),
                new TrackTouchpadHorizontalScrollPatch(),
                new CursorNoteNameTranslationFixPatch(),
                new FastCanvasViewportRectPatch(),
                new FastCanvasViewportRangePatch(),
                new AudioBufferReleaseSafetyPatch(),
                new SpectrumAudioCapturePatch(),
                new CommonUserSettingsJsonOptionsPatch(),
                new UniqueUserSettingsJsonOptionsPatch(),
                new NewsImageCachePatch(),
                new ShortcutModifierCachePatch(),
                new ShortcutKeyMapCachePatch(),
                new ShortcutJsonModifierCachePatch(),
                new McpRevisionCommitPatch(),
                new McpRevisionUndoPatch(),
                new McpRevisionRedoPatch(),
                new ObserveRendererStartPatch(),
                new ObserveRendererBlockPatch(),
                new ObserveRendererProgressPatch(),
                new ObserveRendererCompletePatch(),
                new ObserveRendererCancelPatch(),
                new ObserveMusicalEditorRendererStartPatch(),
                new ObserveMusicalEditorRendererBlockPatch(),
                new ObserveMusicalEditorRendererCompletePatch(),
                new ObserveAudioPlayerRendererBlockPatch(),
                new ObserveAudioPlayerRendererCompletePatch(),
                new ObserveAudioBufferReleasePatch(),
                new ObserveTransactionEndPatch(),
                new ObserveSequenceCommitPatch(),
                new ObserveSequenceRollbackPatch(),
                new ObserveCandidatePhonemesPatch(),
                new ObserveSetSyllablesPatch(),
                new ObserveResetPhonemesPatch(),
                new ObserveSetLyricsAndResetPatch(),
                new ObserveSetLyricsAndResetWithLanguagePatch(),
                new ObserveBreathBypassPatch(),
                new ObserveBreathModePatch(),
                new ObserveBreathTypePatch(),
                new ObserveBreathExhalationPatch(),
                new RenderedWaveCacheRenderStartedPatch(),
                new WaveformBaselineInvalidatePatch(),
                new WaveformSnapshotPatch(),
                new WaveformSnapshotRenderStartedPatch(),
                new WaveformSnapshotRenderCompletedPatch(),
                new WaveformSnapshotRenderCanceledPatch(),
                new WaveformSnapshotZoomPatch(),
                new TrackSelectedFillBrushPatch(),
                new TrackSelectedStrokeBrushPatch(),
                new TrackUnselectedFillBrushPatch(),
                new TrackUnselectedStrokeBrushPatch(),
                new TrackOverlayFillBrushPatch()),

            new(Settings.ShowOtherTracksNotesKey,
                new ShowOtherTracksNotesPatch(),
                new TrackMuteRefreshPatch(),
                new TrackSoloRefreshPatch()),

            new(Settings.ShowNotePitchKey, new ShowNotePitchPatch()),
            new(Settings.RoundedNotesKey, new RoundedNotePatch()),

            new(Settings.CenteredLyricsKey,
                new CenteredLyricPatch(),
                new CenteredLyricPlainPatch()),

            new(Settings.AlwaysShowWaveformKey,
                new AlwaysShowWaveformPatch(),
                new WaveformRenderPatch()),

            new(Settings.SvEditorStyleKey,
                new NoteRowRemapPatch(),
                new ScoreFrameCaptureListPatch(),
                new ScoreFrameCaptureFilePatch(),
                new ScoreFrameCaptureCombinedPatch()
            ),

            new(Settings.IndividualBreathVolumeKey,
                new BreathVolumeParameterHeaderConstructorPatch(),
                new BreathVolumeParameterComboPatch(),
                new BreathVolumeParameterNamePatch(),
                new BreathVolumeParameterShortNamePatch(),
                new BreathVolumeHeaderValuePatch(),
                new BreathVolumeHeaderInputPatch(),
                new BreathVolumeParameterViewConstructorPatch(),
                new BreathVolumeParameterViewUpdatePatch(),
                new BreathVolumeOutsideActivePartLayerPatch(),
                new BreathVolumeMinimumPatch(),
                new BreathVolumeMaximumPatch(),
                new BreathVolumeDefaultPatch(),
                new BreathVolumeRemoveNativeParameterPatch(),
                new BreathVolumeV2CompatibilityPatch(),
                new BreathVolumeWavePathPatch(),
                new BreathVolumeRendererStartPatch(),
                new BreathVolumeRendererBlockPatch(),
                new BreathVolumeRendererCancelPatch(),
                new BreathVolumeRendererCompletePatch(),
                new BreathVolumeProjectLoadPatch(),
                new BreathVolumeProjectOpenCompletePatch(),
                new BreathVolumeProjectSavePatch(),
                new BreathVolumeDuplicateNotePatch(),
                new BreathVolumeDuplicatePartPatch(),
                new BreathVolumeDuplicateTrackPatch(),
                new BreathVolumeDuplicateSequencePatch(),
                new BreathVolumeClipboardNotePatch(),
                new BreathVolumeClipboardPartPatch(),
                new BreathVolumeClipboardClearNotePatch(),
                new BreathVolumeClipboardClearPartPatch(),
                new BreathVolumeRemoveNotePatch(),
                new BreathVolumeG2paDeleteNotePatch(),
                new BreathVolumeJoinNotesPatch(),
                new BreathVolumeRemovePartPatch(),
                new BreathVolumeRemoveTrackPatch(),
                new BreathVolumeSequenceClosePatch()),

            new(null,
                new BreathVolumeCommitHistoryPatch(),
                new BreathVolumeCanUndoPatch(),
                new BreathVolumeCanRedoPatch(),
                new BreathVolumeDirtyPatch(),
                new BreathVolumeUndoPatch(),
                new BreathVolumeRedoPatch(),
                new CustomParameterEditUndoCommandPatch(),
                new CustomParameterEditRedoCommandPatch()),

            new(Settings.RegisterShiftKey,
                new RegisterShiftProjectLoadPatch(),
                new RegisterShiftSequenceStartRenderingPatch(),
                new RegisterShiftProjectSavePatch(),
                new RegisterShiftDuplicateNotePatch(),
                new RegisterShiftDuplicatePartPatch(),
                new RegisterShiftDuplicateTrackPatch(),
                new RegisterShiftDuplicateSequencePatch(),
                new RegisterShiftClipboardNotePatch(),
                new RegisterShiftClipboardPartPatch(),
                new RegisterShiftClipboardClearNotePatch(),
                new RegisterShiftClipboardClearPartPatch(),
                new RegisterShiftRemoveNotePatch(),
                new RegisterShiftG2paDeleteNotePatch(),
                new RegisterShiftJoinNotesPatch(),
                new RegisterShiftRemovePartPatch(),
                new RegisterShiftRemoveTrackPatch(),
                new RegisterShiftRendererStartPatch(),
                new RegisterShiftRendererCompletePatch(),
                new RegisterShiftSequenceClosePatch()),

            new(Settings.EvecEnabledKey,
                new EvecProjectLoadPatch(),
                new EvecProjectSavePatch(),
                new EvecSequenceUndoPatch(),
                new EvecSequenceRedoPatch(),
                new EvecSequenceTempoCommitPatch(),
                new EvecSequenceTempoRollbackPatch(),
                new EvecInsertTempoPatch(),
                new EvecDuplicateTempoPatch(),
                new EvecRemoveTempoPatch(),
                new EvecMoveTempoPatch(),
                new EvecTempoValuePatch(),
                new EvecGlobalTempoPatch(),
                new EvecGlobalTempoEnabledPatch(),
                new EvecAraTempoEnabledPatch(),
                new EvecDuplicateNotePatch(),
                new EvecDuplicatePartPatch(),
                new EvecDividePartPatch(),
                new EvecJoinPartsPatch(),
                new EvecRemovePartPatch(),
                new EvecRemoveTrackPatch(),
                new EvecSequenceClosePatch(),
                new EvecDuplicateTrackPatch(),
                new EvecDuplicateSequencePatch(),
                new EvecDivideNotePatch(),
                new EvecJoinNotesPatch(),
                new EvecNoteDurationPatch(),
                new EvecNoteResizeLeftPatch(),
                new EvecMoveNoteTimingPatch(),
                new EvecMoveMidiPartTimingPatch(),
                new EvecRawPhonemeWritePatch(),
                new EvecLyricMoveLeftPatch(),
                new EvecLyricMoveRightPatch(),
                new EvecClipboardNotePatch(),
                new EvecClipboardPartPatch(),
                new EvecClipboardClearNotePatch(),
                new EvecClipboardClearPartPatch(),
                new EvecClipboardPropertyPatch(),
                new EvecClipboardPartPropertyPatch(),
                new EvecRemoveNotePatch(),
                new EvecSetLyricsPatch(),
                new EvecNoteRenderBadgePatch(),
                new EvecPianorollContextMenuPatch(),
                new EvecNoteInspectorLoadedPatch(),
                new EvecNoteInspectorUpdateViewsPatch(),
                new EvecNoteInspectorUpdateLetterPatch(),
                new EvecNoteInspectorProtectChangedPatch(),
                new EvecLetterPhoneticLockPatch(),
                new EvecPhoneticSymbolTextBoxPatch(),
                new EvecPhonemeFloatingTextBoxPatch(),
                new EvecG2paSetPhonemesPatch(),
                new EvecG2paResetPhonemesNotePatch(),
                new EvecAutomaticVoiceBankChangePatch(),
                new EvecG2paResetPhonemesPartPatch(),
                new EvecVoiceBankChangePatch()),

            new(Settings.ShowCharacterArtKey, new CharacterArtPatch()),

            new(Settings.FastProjectLoadKey,
                new NewProjectSetupScopePatch(),
                new NewProjectActivePartDeferPatch(),
                new LazyLyricRenderPatch(),
                new DeferAudioBufferLoadPatch(),
                new CancelDeferredAudioBufferLoadPatch(),
                new ExcludeRemovedDeferredAudioBufferPatch(),
                new ExcludeReplacedDeferredAudioBufferPatch(),
                new VisiblePianorollNotesPatch(),
                new VisiblePianorollLyricsPatch(),
                new PianorollVirtualizationViewportPatch(),
                new DeferLoadAnalyticsPatch(),
                new LeanLyricTextPatch()),

            new(Settings.SkipUnchangedPartRedrawKey,
                new SkipUnchangedPartRedrawPatch(),
                new PianorollSelectionPenCachePatch()
            ),

            new(Settings.ThrottleRendererPreviewKey, new RendererPreviewThrottlePatch()),

            new(Settings.ExtendedChinesePinyinKey,
                new ExtendedChinesePinyinSetLyricsPatch(),
                new VocaloidSpecialPhonemeAiSetLyricsPatch(),
                new ExtendedChinesePinyinSetSyllablesContextPatch(),
                new ExtendedChinesePinyinCandidatePatch(),
                new ExtendedChinesePinyinFloatingInputRepairPatch(),
                new ExtendedChinesePinyinRangeResetPatch(),
                new ExtendedChinesePinyinPartResetPatch(),
                new SegmentedPhonemeSequenceCommitPatch(),
                new SegmentedPhonemeSequenceStartRenderingPatch(),
                new SegmentedPhonemeSequenceUndoPatch(),
                new SegmentedPhonemeSequenceRedoPatch(),
                new SegmentedPhonemeWaveFilePathPatch(),
                new SegmentedPhonemeValidRenderedWavePatch(),
                new SegmentedPhonemeScoreFilePathPatch(),
                new SegmentedPhonemeValidRenderedScorePatch(),
                new SegmentedPhonemeRendererProgressPatch()),

            new(Settings.FastSelectionSweepKey,
                new TimeSigSelectionTrackPatch(),
                new TempoSelectionTrackPatch(),
                new BreakPointSelectionTrackPatch(),
                new FastSelectionSweepPatch()),

            new(Settings.DeferParameterViewUpdateKey,
                new ParameterViewDeferPatch(),
                new ParameterVisibleRangePatch()
            ),

            new(Settings.DeferInspectorRefreshKey,
                new InspectorRefreshDeferPatch()
            ),

            new(Settings.SmoothPlayheadKey,
                new SmoothPlayheadBeginPatch(),
                new SmoothPlayheadEndPatch(),
                new SmoothPlayheadSeekPatch()),

            new(Settings.FreeAudioPcmCacheKey,
                new AudioPcmReleasePatch(),
                new AudioThumbFromCachePatch()),

            new(Settings.TrimWorkingSetKey,
                new WorkingSetTrimPatch(),
                new RenderCompleteTrimPatch()),

            new(Settings.OptimizeTrackRenderingKey,
                new TrackNoteInsertPatch(),
                new TrackNoteRemovePatch(),
                new TrackNoteDurationChangedPatch(),
                new TrackNoteMovedPatch(),
                new TrackNoteNumberChangedPatch(),
                new TrackPartDurationChangedPatch(),
                new UIMidiPartNotesRenderPatch(),
                new AudioTrackHeightResizePatch(),
                new MidiTrackSelectionRefreshPatch(),
                new AudioTrackSelectionRefreshPatch(),
                new MidiPartSelectionPenCachePatch(),
                new AudioPartSelectionPenCachePatch(),
                new TrackRulerGridLinePatch(),
                new MusicalRulerGridLinePatch()),

            new(Settings.CacheRenderedWavesKey,
                new RenderedWaveCachePatch(),
                new RenderedWaveCacheClearPatch()),

            new(Settings.UnlockAllLanguagesKey,
                new VoiceBankLangIDsPatch(),
                new VoiceBankLangIDSizePatch(),
                new VoiceBankLangIDByIndexPatch(),
                new VoiceBankContainsLangIDPatch(),
                new VoiceBankContainsAllLangIDsPatch()),

            new(Settings.UnlockVocaloChangerKey,
                new VoiceBankVocaloChangerPatch(),
                new VocaloChangerModelFallbackPatch()),

            new(null,
                new DatabaseManagerCreatePatch(),
                new VoiceBankConstructorPatch()),
        };

        var disabled = new List<string>();

        foreach (var group in groups)
        {
            foreach (var patch in group.Patches)
            {
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_ApplyingPatch", patch.PatchName));

                if (patch.Apply(_harmony, group.FeatureKey) || group.FeatureKey == null)
                    continue;

                if (!Settings.IsFeatureDisabled(group.FeatureKey))
                {
                    Settings.DisableFeature(group.FeatureKey);
                    disabled.Add(group.FeatureKey);
                    Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Patcher_FeatureDisabled", group.FeatureKey));
                }
            }
        }

        if (disabled.Count > 0)
            ReportDisabledFeatures(disabled);

        if (Settings.IndividualBreathVolume)
            BreathVolumeService.InitializeDiagnostics();
        if (Settings.RegisterShift)
            NativeRegisterShift.Initialize();

        try
        {
            if (Yamaha.VOCALOID.App.DatabaseManager != null)
            {
                NativeVoiceBankHook.Initialize(Yamaha.VOCALOID.App.DatabaseManager);
            }
        }
        catch
        {
            // Defensive
        }
    }

    private const string NotifiedDisabledFeaturesKey = "NotifiedDisabledFeatures";

    private static void ReportDisabledFeatures(List<string> features)
    {
        var notified = ConfigManager.Get(NotifiedDisabledFeaturesKey, new List<string>());

        var newly = features.FindAll(f => !notified.Contains(f));
        if (newly.Count == 0)
            return;

        var list = string.Join(Environment.NewLine,
            newly.Select(key => "• " + TranslationManager.Tr($"VOCALOIDPatcher_{key}_Header")));

        Debug.ShowMessageBox(TranslationManager.Tr("VOCALOIDPatcher_Patch_FeaturesDisabled", list));

        notified.AddRange(newly);
        ConfigManager.Set(NotifiedDisabledFeaturesKey, notified);
    }

    private static readonly MenuItem PatcherMenuItem = new()
    {
        Header = "VOCALOID Patcher",
        Name = "VOCALOIDPatcherMenuItem"
    };

    private static void AddPatcherMenuItem()
    {
        try
        {
            var menu = ReflectionUtils.GetMainMenu();

            WpfTranslationPatch.MarkUntranslatable(PatcherMenuItem);
            PatcherMenuItem.Click += (_, _) => SettingsWindow.ShowSingleton();

            menu.Items.Insert(menu.Items.Count - 1, PatcherMenuItem);

            TranslationManager.LanguageChanged += (_, _) => RefreshPatcherMenuHeader();
            UpdateChecker.UpdateAvailable += RefreshPatcherMenuHeader;
            RefreshPatcherMenuHeader();
        }
        catch (Exception e)
        {
            Debug.ShowErrorMessage(e.Message + e.StackTrace);
        }
    }

    private static void RefreshPatcherMenuHeader()
    {
        void Apply()
        {
            var header = "VOCALOID Patcher";
            if (UpdateChecker.HasUpdate)
                header += TranslationManager.Tr("VOCALOIDPatcher_Update_MenuSuffix");

            PatcherMenuItem.Header = header;
        }

        if (PatcherMenuItem.Dispatcher.CheckAccess())
            Apply();
        else
            PatcherMenuItem.Dispatcher.Invoke(Apply);
    }
}
