using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;
using Yamaha.VOCALOID.WOR;

namespace VOCALOIDPatcher.Patch.Patches;

public class VoiceBankVocaloChangerPatch : PatchBase
{
    public override string PatchName        => "VoiceBankVocaloChangerPatch";
    public override Type   TargetClass      => typeof(VoiceBank);
    public override string TargetMethodName => "get_IsAvailableForVoiceChanger";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPrefix]
    private static bool Prefix(VoiceBank __instance, ref bool __result)
    {
        if (!Settings.UnlockVocaloChanger)
            return true;

        if (VoiceBankHelper.IsAiVoiceBank(__instance))
        {
            __result = true;
            return false;
        }
        return true;
    }
}

public class VocaloChangerModelFallbackPatch : PatchBase
{
    public override string PatchName        => "VocaloChangerModelFallbackPatch";
    public override Type   TargetClass      => typeof(OfflineProcessor);
    public override string TargetMethodName => "CreateVoiceChangeAudioFile";
    public override Type[] ArgumentTypes    => new[]
    {
        typeof(VoiceChangeRenderer),
        typeof(WIVSMAudioPart),
        typeof(VoiceBank),
        typeof(int)
    };

    [HarmonyPrefix]
    private static bool Prefix(
        VoiceChangeRenderer renderer,
        WIVSMAudioPart part,
        VoiceBank voiceBank,
        int pitchShiftValue,
        ref OfflineProcessor.CreationResult __result)
    {
        if (!Settings.UnlockVocaloChanger)
            return true;

        if (renderer == null || part == null || voiceBank == null)
            return true;

        try
        {
            string expectedModel = $"{voiceBank.Path}/{voiceBank.GroupName}.vtbr";
            if (File.Exists(expectedModel))
                return true;

            string? fallbackModel = FindFallbackVtbrModel(voiceBank);
            if (string.IsNullOrEmpty(fallbackModel))
                return true;

            string originalWaveFilePath = part.GetOriginalWaveFilePath();
            if (string.IsNullOrEmpty(originalWaveFilePath))
            {
                __result = new OfflineProcessor.CreationResult(WORError.HaveNoUseRenderer, string.Empty);
                return false;
            }

            string temporaryWaveFilePath = FileManager.TemporaryWaveFilePath;
            float pitchShift = (float)pitchShiftValue * 100f;
            WORError error = renderer.RenderAudioFile(
                originalWaveFilePath,
                temporaryWaveFilePath,
                fallbackModel,
                voiceBank.NPIndex,
                pitchShift);

            __result = new OfflineProcessor.CreationResult(error, temporaryWaveFilePath);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string? FindFallbackVtbrModel(VoiceBank voiceBank)
    {
        try
        {
            string dir = voiceBank.Path;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            string byCompId = Path.Combine(dir, $"{voiceBank.CompID}.vtbr");
            if (File.Exists(byCompId))
                return byCompId;

            string byName = Path.Combine(dir, $"{voiceBank.Name}.vtbr");
            if (File.Exists(byName))
                return byName;

            return Directory.EnumerateFiles(dir, "*.vtbr").FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
