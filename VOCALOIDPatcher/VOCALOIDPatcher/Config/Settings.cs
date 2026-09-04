using System.Collections.Generic;

namespace VOCALOIDPatcher.Config;

public static class Settings
{
    private static readonly HashSet<string> DisabledFeatures = new();

    public static void DisableFeature(string key)
    {
        if (!string.IsNullOrEmpty(key))
            DisabledFeatures.Add(key);
    }

    public static bool IsFeatureDisabled(string key)
        => DisabledFeatures.Count != 0 && DisabledFeatures.Contains(key);

    private static bool FeatureFlag(string key, bool defaultValue)
        => !IsFeatureDisabled(key) && Patcher.ConfigManager.Get(key, defaultValue);

    public static event System.Action? McpSettingsChanged;

    public static string McpEnabledKey => "McpEnabled";

    public static bool McpEnabled
    {
        get => Patcher.ConfigManager.Get(McpEnabledKey, false);
        set
        {
            Patcher.ConfigManager.Set(McpEnabledKey, value);
            McpSettingsChanged?.Invoke();
        }
    }

    public static string McpHttpEnabledKey => "McpHttpEnabled";

    public static bool McpHttpEnabled
    {
        get => Patcher.ConfigManager.Get(McpHttpEnabledKey, false);
        set
        {
            Patcher.ConfigManager.Set(McpHttpEnabledKey, value);
            McpSettingsChanged?.Invoke();
        }
    }

    public static string McpHttpPortKey => "McpHttpPort";

    public static int McpHttpPort
    {
        get => System.Math.Clamp(Patcher.ConfigManager.Get(McpHttpPortKey, 39266), 1024, 65535);
        set
        {
            Patcher.ConfigManager.Set(McpHttpPortKey, System.Math.Clamp(value, 1024, 65535));
            McpSettingsChanged?.Invoke();
        }
    }

    public static string McpAllowedDirectoriesKey => "McpAllowedDirectories";

    public static List<string> McpAllowedDirectories
    {
        get => Patcher.ConfigManager.Get(McpAllowedDirectoriesKey, new List<string>());
        set => Patcher.ConfigManager.Set(McpAllowedDirectoriesKey, value);
    }

    public static string McpConfirmWritesKey => "McpConfirmWrites";

    public static bool McpConfirmWrites
    {
        get => Patcher.ConfigManager.Get(McpConfirmWritesKey, true);
        set => Patcher.ConfigManager.Set(McpConfirmWritesKey, value);
    }

    public static string TranslateHardcodedStringsKey => "TranslateHardcodedStrings";

    public static bool TranslateHardcodedStrings
    {
        get => Patcher.ConfigManager.Get(TranslateHardcodedStringsKey, true);
        set => Patcher.ConfigManager.Set(TranslateHardcodedStringsKey, value);
    }

    public static string ShowOtherTracksNotesKey => "ShowOtherTracksNotes";

    public static bool ShowOtherTracksNotes
    {
        get => FeatureFlag(ShowOtherTracksNotesKey, false);
        set => Patcher.ConfigManager.Set(ShowOtherTracksNotesKey, value);
    }

    public static string ShowOtherTracksSkipMutedKey => "ShowOtherTracksSkipMuted";

    public static bool ShowOtherTracksSkipMuted
    {
        get => Patcher.ConfigManager.Get(ShowOtherTracksSkipMutedKey, false);
        set => Patcher.ConfigManager.Set(ShowOtherTracksSkipMutedKey, value);
    }

    public static string ShowCharacterArtKey => "ShowCharacterArt";

    public static bool ShowCharacterArt
    {
        get => FeatureFlag(ShowCharacterArtKey, false);
        set => Patcher.ConfigManager.Set(ShowCharacterArtKey, value);
    }

    public static string CharacterArtSizeKey => "CharacterArtSize";

    public static int CharacterArtSize
    {
        get => Patcher.ConfigManager.Get(CharacterArtSizeKey, 220);
        set => Patcher.ConfigManager.Set(CharacterArtSizeKey, value);
    }

    public static string CharacterArtOpacityKey => "CharacterArtOpacity";

    public static double CharacterArtOpacity
    {
        get => Patcher.ConfigManager.Get(CharacterArtOpacityKey, 0.9);
        set => Patcher.ConfigManager.Set(CharacterArtOpacityKey, value);
    }

    public static string CharacterArtHorizontalPositionKey => "CharacterArtHorizontalPosition";

    public static double CharacterArtHorizontalPosition
    {
        get => Patcher.ConfigManager.Get(CharacterArtHorizontalPositionKey, 100.0);
        set => Patcher.ConfigManager.Set(CharacterArtHorizontalPositionKey, value);
    }

    public static string CharacterArtVerticalPositionKey => "CharacterArtVerticalPosition";

    public static double CharacterArtVerticalPosition
    {
        get => Patcher.ConfigManager.Get(CharacterArtVerticalPositionKey, 100.0);
        set => Patcher.ConfigManager.Set(CharacterArtVerticalPositionKey, value);
    }

    public static string CharacterArtPathsKey => "CharacterArtPaths";

    public static Dictionary<string, string> CharacterArtPaths
    {
        get => Patcher.ConfigManager.Get(CharacterArtPathsKey, new Dictionary<string, string>());
        set => Patcher.ConfigManager.Set(CharacterArtPathsKey, value);
    }

    public static string? GetCharacterArtPath(string compId)
    {
        if (string.IsNullOrEmpty(compId))
            return null;
        return CharacterArtPaths.TryGetValue(compId, out var path) ? path : null;
    }

    public static void SetCharacterArtPath(string compId, string? path)
    {
        if (string.IsNullOrEmpty(compId))
            return;

        var map = CharacterArtPaths;
        if (string.IsNullOrEmpty(path))
            map.Remove(compId);
        else
            map[compId] = path!;
        CharacterArtPaths = map;
    }

    public static string ShowNotePitchKey => "ShowNotePitch";

    public static bool ShowNotePitch
    {
        get => FeatureFlag(ShowNotePitchKey, false);
        set => Patcher.ConfigManager.Set(ShowNotePitchKey, value);
    }

    public static string RoundedNotesKey => "RoundedNotes";

    public static bool RoundedNotes
    {
        get => FeatureFlag(RoundedNotesKey, false);
        set => Patcher.ConfigManager.Set(RoundedNotesKey, value);
    }

    public static string CenteredLyricsKey => "CenteredLyrics";

    public static bool CenteredLyrics
    {
        get => FeatureFlag(CenteredLyricsKey, false);
        set => Patcher.ConfigManager.Set(CenteredLyricsKey, value);
    }

    public static string AlwaysShowWaveformKey => "AlwaysShowWaveform";

    public static bool AlwaysShowWaveform
    {
        get => FeatureFlag(AlwaysShowWaveformKey, false);
        set => Patcher.ConfigManager.Set(AlwaysShowWaveformKey, value);
    }

    public static string SvEditorStyleKey => "SvEditorStyle";

    public static bool SvEditorStyle
    {
        get => FeatureFlag(SvEditorStyleKey, false);
        set => Patcher.ConfigManager.Set(SvEditorStyleKey, value);
    }

    public static string IndividualBreathVolumeKey => "IndividualBreathVolume";

    public static bool IndividualBreathVolume
    {
        get => FeatureFlag(IndividualBreathVolumeKey, true);
        set => Patcher.ConfigManager.Set(IndividualBreathVolumeKey, value);
    }

    public static string RegisterShiftKey => "RegisterShift";

    public static bool RegisterShift
    {
        get => FeatureFlag(RegisterShiftKey, true);
        set => Patcher.ConfigManager.Set(RegisterShiftKey, value);
    }

    public static string EvecEnabledKey => "EvecEnabled";

    public static bool EvecEnabled
    {
        get => FeatureFlag(EvecEnabledKey, true);
        set => Patcher.ConfigManager.Set(EvecEnabledKey, value);
    }

    public static string WaveformOpacityKey => "WaveformOpacity";

    public static double WaveformOpacity
    {
        get => Patcher.ConfigManager.Get(WaveformOpacityKey, 0.6);
        set => Patcher.ConfigManager.Set(WaveformOpacityKey, value);
    }

    public static string AutoSaveEnabledKey => "AutoSaveEnabled";

    public static bool AutoSaveEnabled
    {
        get => Patcher.ConfigManager.Get(AutoSaveEnabledKey, false);
        set => Patcher.ConfigManager.Set(AutoSaveEnabledKey, value);
    }

    public static string AutoConvertChineseLyricsToPinyinKey => "AutoConvertChineseLyricsToPinyin";

    public static bool AutoConvertChineseLyricsToPinyin
    {
        get => FeatureFlag(AutoConvertChineseLyricsToPinyinKey, true);
        set => Patcher.ConfigManager.Set(AutoConvertChineseLyricsToPinyinKey, value);
    }

    public static string ExtendedChinesePinyinKey => "ExtendedChinesePinyin";

    public static event System.Action<bool>? ExtendedChinesePinyinChanged;

    public static bool ExtendedChinesePinyin
    {
        get => FeatureFlag(ExtendedChinesePinyinKey, true);
        set
        {
            Patcher.ConfigManager.Set(ExtendedChinesePinyinKey, value);
            ExtendedChinesePinyinChanged?.Invoke(value);
        }
    }

    public static string AutoSaveIntervalMinutesKey => "AutoSaveIntervalMinutes";

    public static int AutoSaveIntervalMinutes
    {
        get => Patcher.ConfigManager.Get(AutoSaveIntervalMinutesKey, 5);
        set => Patcher.ConfigManager.Set(AutoSaveIntervalMinutesKey, value);
    }

    public static string FastProjectLoadKey => "FastProjectLoad";

    public static bool FastProjectLoad
    {
        get => FeatureFlag(FastProjectLoadKey, true);
        set => Patcher.ConfigManager.Set(FastProjectLoadKey, value);
    }

    public static string PreloadDseKey => "PreloadDse";

    public static bool PreloadDse
    {
        get => FeatureFlag(PreloadDseKey, true);
        set => Patcher.ConfigManager.Set(PreloadDseKey, value);
    }

    public static string DseLicensePatchKey => "DseLicensePatch";

    public static bool DseLicensePatch
    {
        get => FeatureFlag(DseLicensePatchKey, true);
        set => Patcher.ConfigManager.Set(DseLicensePatchKey, value);
    }

    public static string FreeAudioPcmCacheKey => "FreeAudioPcmCache";

    public static bool FreeAudioPcmCache
    {
        get => FeatureFlag(FreeAudioPcmCacheKey, true);
        set => Patcher.ConfigManager.Set(FreeAudioPcmCacheKey, value);
    }

    public static string TrimWorkingSetKey => "TrimWorkingSet";

    public static bool TrimWorkingSet
    {
        get => FeatureFlag(TrimWorkingSetKey, true);
        set => Patcher.ConfigManager.Set(TrimWorkingSetKey, value);
    }

    public static string OptimizeTrackRenderingKey => "OptimizeTrackRendering";

    public static bool OptimizeTrackRendering
    {
        get => FeatureFlag(OptimizeTrackRenderingKey, true);
        set => Patcher.ConfigManager.Set(OptimizeTrackRenderingKey, value);
    }

    public static string FastSelectionSweepKey => "FastSelectionSweep";

    public static bool FastSelectionSweep
    {
        get => FeatureFlag(FastSelectionSweepKey, true);
        set => Patcher.ConfigManager.Set(FastSelectionSweepKey, value);
    }

    public static string DeferParameterViewUpdateKey => "DeferParameterViewUpdate";

    public static bool DeferParameterViewUpdate
    {
        get => FeatureFlag(DeferParameterViewUpdateKey, true);
        set => Patcher.ConfigManager.Set(DeferParameterViewUpdateKey, value);
    }

    public static string DeferInspectorRefreshKey => "DeferInspectorRefresh";

    public static bool DeferInspectorRefresh
    {
        get => FeatureFlag(DeferInspectorRefreshKey, true);
        set => Patcher.ConfigManager.Set(DeferInspectorRefreshKey, value);
    }

    public static string CacheRenderedWavesKey => "CacheRenderedWaves";

    public static bool CacheRenderedWaves
    {
        get => FeatureFlag(CacheRenderedWavesKey, true);
        set => Patcher.ConfigManager.Set(CacheRenderedWavesKey, value);
    }

    public static string SkipUnchangedPartRedrawKey => "SkipUnchangedPartRedraw";

    public static bool SkipUnchangedPartRedraw
    {
        get => FeatureFlag(SkipUnchangedPartRedrawKey, true);
        set => Patcher.ConfigManager.Set(SkipUnchangedPartRedrawKey, value);
    }

    public static string SpectrumVisualizerKey => "SpectrumVisualizer";

    public static bool SpectrumVisualizer
    {
        get => Patcher.ConfigManager.Get(SpectrumVisualizerKey, false);
        set => Patcher.ConfigManager.Set(SpectrumVisualizerKey, value);
    }

    public static string SmoothPlayheadKey => "SmoothPlayhead";

    public static bool SmoothPlayhead
    {
        get => FeatureFlag(SmoothPlayheadKey, true);
        set => Patcher.ConfigManager.Set(SmoothPlayheadKey, value);
    }

    public static string AutoCalibratePlayheadLatencyKey => "AutoCalibratePlayheadLatency";

    public static bool AutoCalibratePlayheadLatency
    {
        get => Patcher.ConfigManager.Get(AutoCalibratePlayheadLatencyKey, true);
        set => Patcher.ConfigManager.Set(AutoCalibratePlayheadLatencyKey, value);
    }

    public static string ThrottleRendererPreviewKey => "ThrottleRendererPreview";

    public static bool ThrottleRendererPreview
    {
        get => FeatureFlag(ThrottleRendererPreviewKey, true);
        set => Patcher.ConfigManager.Set(ThrottleRendererPreviewKey, value);
    }

    public static string UnlockAllLanguagesKey => "UnlockAllLanguages";

    public static bool UnlockAllLanguages
    {
        get => FeatureFlag(UnlockAllLanguagesKey, true);
        set => Patcher.ConfigManager.Set(UnlockAllLanguagesKey, value);
    }

    public static string UnlockVocaloChangerKey => "UnlockVocaloChanger";

    public static bool UnlockVocaloChanger
    {
        get => FeatureFlag(UnlockVocaloChangerKey, true);
        set => Patcher.ConfigManager.Set(UnlockVocaloChangerKey, value);
    }

}
