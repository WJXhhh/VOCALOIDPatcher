using System;
using System.IO;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

/// <summary>
/// Render-lifecycle hooks for the EVEC render copy. They mirror the extended
/// Chinese pinyin renderer: project changes schedule a debounced rebuild, part
/// getters expose the published artifact, and removal/close cancel safely.
/// </summary>
public sealed class EvecRenderSequenceCommitPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderSequenceCommitPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Commit);
    public override Type[] ArgumentTypes    => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, out bool __state)
    {
        try
        {
            __state = __instance.IsStaged;
        }
        catch
        {
            __state = true;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __state, ref bool __result)
    {
        if (__result && __state)
            EvecRenderCoordinator.SequenceChanged(__instance, "commit");
    }
}

public sealed class EvecRenderSequenceUndoPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderSequenceUndoPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Undo);
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        EvecRenderCoordinator.SequenceChanged(__instance, "undo");
    }
}

public sealed class EvecRenderSequenceRedoPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderSequenceRedoPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Redo);
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        EvecRenderCoordinator.SequenceChanged(__instance, "redo");
    }
}

public sealed class EvecRenderSequenceStartRenderingPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderSequenceStartRenderingPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.StartAsyncRendering);
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        EvecRenderCoordinator.SequenceRenderingStarted(__instance);
    }
}

public sealed class EvecRenderSequenceClosePatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderSequenceClosePatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Close);
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance)
    {
        EvecRenderCoordinator.ReleaseSequence(__instance);
    }
}

public sealed class EvecRenderRemovePartPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderRemovePartPatch);
    public override Type TargetClass        => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => nameof(WIVSMMidiTrack.RemovePart);
    public override Type[] ArgumentTypes    => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart midiPart, bool __result)
    {
        if (__result)
            EvecRenderCoordinator.ReleasePart(midiPart);
    }
}

public sealed class EvecRenderRemoveTrackPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderRemoveTrackPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.RemoveTrack);
    public override Type[] ArgumentTypes    => new[] { typeof(WIVSMTrack) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMTrack? track, bool __result)
    {
        if (!__result || track is not WIVSMMidiTrack midiTrack)
            return;

        try
        {
            foreach (WIVSMMidiPart part in midiTrack.MidiParts)
                EvecRenderCoordinator.ReleasePart(part);
        }
        catch
        {
            // Native track removal must survive optional override cleanup.
        }
    }
}

public sealed class EvecRenderWaveFilePathPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderWaveFilePathPatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_WaveFilePath";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref string __result)
    {
        if (!Settings.EvecEnabled || ChineseRendererOwns(__instance))
            return;

        if (EvecRenderCoordinator.TryGetOverridePath(__instance, out string path))
            __result = path;
    }

    private static bool ChineseRendererOwns(WIVSMMidiPart part)
        => Settings.ExtendedChinesePinyin
           && SegmentedPhonemeRenderCoordinator.TryGetOverridePath(part, out _);
}

public sealed class EvecRenderValidRenderedWavePatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderValidRenderedWavePatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_HasValidRenderedWave";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref bool __result)
    {
        if (__result
            || !Settings.EvecEnabled
            || (Settings.ExtendedChinesePinyin && SegmentedPhonemeRenderCoordinator.TryGetOverridePath(__instance, out _)))
        {
            return;
        }

        if (EvecRenderCoordinator.TryGetOverridePath(__instance, out string path) && File.Exists(path))
            __result = true;
    }
}

public sealed class EvecRenderScoreFilePathPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderScoreFilePathPatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_ScoreFilePath";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref string __result)
    {
        if (!Settings.EvecEnabled || ChineseRendererOwns(__instance))
            return;

        if (EvecRenderCoordinator.TryGetOverrideScorePath(__instance, out string path))
            __result = path;
    }

    private static bool ChineseRendererOwns(WIVSMMidiPart part)
        => Settings.ExtendedChinesePinyin
           && SegmentedPhonemeRenderCoordinator.TryGetOverrideScorePath(part, out _);
}

public sealed class EvecRenderValidRenderedScorePatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderValidRenderedScorePatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_HasValidRenderedScore";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref bool __result)
    {
        if (__result
            || !Settings.EvecEnabled
            || (Settings.ExtendedChinesePinyin && SegmentedPhonemeRenderCoordinator.TryGetOverrideScorePath(__instance, out _)))
        {
            return;
        }

        if (EvecRenderCoordinator.TryGetOverrideScorePath(__instance, out string path) && File.Exists(path))
            __result = true;
    }
}

public sealed class EvecRenderProgressPatch : PatchBase
{
    public override string PatchName        => nameof(EvecRenderProgressPatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_RendererProgress";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref VSMRendererProgress __result)
    {
        if (!Settings.EvecEnabled
            || (Settings.ExtendedChinesePinyin && SegmentedPhonemeRenderCoordinator.TryGetOverridePath(__instance, out _)))
        {
            return;
        }

        if (EvecRenderCoordinator.TryGetOverridePath(__instance, out string path) && File.Exists(path))
            __result = VSMRendererProgressExtension.FullProgress;
    }
}
