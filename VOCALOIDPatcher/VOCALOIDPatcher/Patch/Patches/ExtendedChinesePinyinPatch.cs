using System;
using System.Collections.Generic;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Formats.LibreSvip;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public class ExtendedChinesePinyinSetLyricsPatch : PatchBase
{
    public override string PatchName        => "ExtendedChinesePinyinSetLyricsPatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "SetLyrics";
    public override Type[] ArgumentTypes    =>
        new[] { typeof(WIVSMNote), typeof(string), typeof(int), typeof(bool), typeof(bool) };

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMNote note,
        string lyrics,
        int langID,
        ref (bool IsSuccess, WIVSMNote? NextNote) __result)
    {
        if (!Settings.ExtendedChinesePinyin
            || langID != (int)VSMLanguageID.Chinese
            || __result.IsSuccess
            || !ChinesePinyinPhonemeConverter.TryConvertSequence(lyrics, out var syllables, out _))
        {
            return;
        }

        try
        {
            if (!ChinesePinyinSyllableApplicator.TrySetSyllables(note, syllables, langID, out var result))
                return;

            __result = result;
        }
        catch
        {
            // Keep the failure returned by VOCALOID's native G2PA path.
        }
    }
}

public class ExtendedChinesePinyinCandidatePatch : PatchBase
{
    public override string PatchName        => "ExtendedChinesePinyinCandidatePatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "CandidatePhonemes";
    public override Type[] ArgumentTypes    =>
        new[] { typeof(WIVSMNote), typeof(string), typeof(int), typeof(bool), typeof(bool) };

    [HarmonyPostfix]
    private static void Postfix(
        string lyrics,
        int langID,
        ref List<Syllables> __result)
    {
        if (!Settings.ExtendedChinesePinyin
            || langID != (int)VSMLanguageID.Chinese
            || __result is { Count: > 0 }
            || !ChinesePinyinPhonemeConverter.TryConvertSequence(lyrics, out var syllables, out _))
        {
            return;
        }

        try
        {
            __result = new List<Syllables>
            {
                ChinesePinyinSyllableApplicator.CreateCandidate(syllables, langID),
            };
        }
        catch
        {
            // Keep the empty result returned by VOCALOID's native G2PA path.
        }
    }
}
