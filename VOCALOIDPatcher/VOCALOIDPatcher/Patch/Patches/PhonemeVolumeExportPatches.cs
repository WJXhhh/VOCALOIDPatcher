using System;
using System.Collections.Generic;
using HarmonyLib;
using VOCALOIDPatcher.PhonemeVolume;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

internal static class PhonemeVolumeExport
{
    internal static bool Prepare()
    {
        try
        {
            if (App.Shared?.Document?.Sequence?.VSMSequence is { } source)
            {
                try { ConsonantTiming.SampledTimingHost.EnsureForExport(source); }
                catch (Exception e)
                {
                    Debug.Print("Timing/duration export preparation: " + e.GetType().Name);
                    Debug.ShowMessageBox(TranslationManager.Tr("VOCALOIDPatcher_DurationSelection_ExportNotReady"));
                    return false;
                }
                PhonemeVolumeHost.EnsureForExport(source);
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.Print("Phoneme volume export preparation: " + e.GetType().Name);
            Debug.ShowMessageBox(TranslationManager.Tr("VOCALOIDPatcher_PhonemeVolume_ExportNotReady"));
            return false;
        }
    }
}

public sealed class PhonemeVolumeMasterExportPatch : PatchBase
{
    public override string PatchName => nameof(PhonemeVolumeMasterExportPatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "ExecuteAudioMixdownMaster";
    public override Type[] ArgumentTypes => [typeof(string), typeof(MixdownOption)];
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result) { __result = false; return PhonemeVolumeExport.Prepare(); }
}

public sealed class PhonemeVolumeTrackExportPatch : PatchBase
{
    public override string PatchName => nameof(PhonemeVolumeTrackExportPatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "ExecuteAudioMixdownTrackWithTrackInfo";
    public override Type[] ArgumentTypes => [typeof(List<AudioMixdownTrackInfo>), typeof(MixdownOption)];
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result) { __result = false; return PhonemeVolumeExport.Prepare(); }
}

public sealed class PhonemeVolumePartExportPatch : PatchBase
{
    public override string PatchName => nameof(PhonemeVolumePartExportPatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "ExecuteAudioMixdownPart";
    public override Type[] ArgumentTypes => [typeof(WIVSMPart), typeof(string), typeof(MixdownOption)];
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result) { __result = false; return PhonemeVolumeExport.Prepare(); }
}
