using System;
using HarmonyLib;
using VOCALOIDPatcher.VoiceComp;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;
using Yamaha.VOCALOID.VAE;

namespace VOCALOIDPatcher.Patch.Patches;

internal static class VoiceCompPatchBoundary
{
    internal static void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { Utils.Debug.Print("Voice-comp lifecycle: " + error.GetType().Name); }
    }
}

public sealed class VoiceCompCommitPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompCommitPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Commit);
    public override Type[] ArgumentTypes => [typeof(bool)];
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __result)
    {
        if (__result && !ConsonantTiming.TimingCacheInvalidationScope.IsActive((nint)__instance))
            VoiceCompPatchBoundary.Run(() => VoiceCompHost.NativeChanged(__instance));
    }
}
public sealed class VoiceCompStartPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompStartPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.StartAsyncRendering);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance) => VoiceCompPatchBoundary.Run(() => Utils.NativeRenderSession.BeginForeground(__instance));
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance) => VoiceCompPatchBoundary.Run(() => VoiceCompHost.NativeChanged(__instance));
}
public sealed class VoiceCompStopPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompStopPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.StopAsyncRendering);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance) => Utils.NativeRenderSession.EndForeground(__instance);
}
public sealed class VoiceCompUndoPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompUndoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Undo);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance) => VoiceCompPatchBoundary.Run(() => VoiceCompHost.NativeChanged(__instance));
}
public sealed class VoiceCompRedoPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompRedoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Redo);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance) => VoiceCompPatchBoundary.Run(() => VoiceCompHost.NativeChanged(__instance));
}
public sealed class VoiceCompClosePatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompClosePatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Close);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance)
    { Utils.NativeRenderSession.EndForeground(__instance); VoiceCompPatchBoundary.Run(() => VoiceCompHost.Close(__instance)); }
}
public sealed class VoiceCompDirtyPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompDirtyPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "get_IsDirty";
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, ref bool __result)
    { try { __result |= VoiceCompHost.IsDirty(__instance); } catch (Exception error) { Utils.Debug.Print("Voice-comp dirty state: " + error.GetType().Name); } }
}
public sealed class VoiceCompBlockPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompBlockPatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "OnRendererBlockRendered";
    public override Type[] ArgumentTypes => [typeof(object), typeof(RendererObserverBlockRenderingEventArgs)];
    [HarmonyPrefix]
    private static bool Prefix(RendererObserverBlockRenderingEventArgs e) => e?.MidiPart == null || VoiceCompHost.View(e.MidiPart) == null;
}
public sealed class VoiceCompValidWavePatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompValidWavePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_HasValidRenderedWave";
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref bool __result)
    { if (Config.Settings.VoiceCompEnabled && VoiceCompHost.View(__instance) is { Dirty: false, Wave: not null }) __result = true; }
}

public sealed class VoiceCompPreviewBlockPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompPreviewBlockPatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.MusicalEditor.MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererBlockRendered";
    public override Type[] ArgumentTypes => [typeof(object), typeof(RendererObserverBlockRenderingEventArgs)];
    [HarmonyPrefix]
    private static bool Prefix(RendererObserverBlockRenderingEventArgs e)
    {
        try { return e?.MidiPart == null || !VoiceCompHost.Owns(e.MidiPart); }
        catch (Exception error) { Utils.Debug.Print("Voice-comp preview: " + error.GetType().Name); return e?.MidiPart == null || VoiceCompHost.View(e.MidiPart) == null; }
    }
}

public sealed class VoiceCompManagerDisposalPatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompManagerDisposalPatch);
    public override Type TargetClass => typeof(WIVSMSequenceManager);
    public override string TargetMethodName => nameof(WIVSMSequenceManager.Dispose);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequenceManager __instance)
    {
        if (App.SequenceManager is { } owner && (nint)owner == (nint)__instance)
            VoiceCompPatchBoundary.Run(VoiceCompHost.DrainForManagerDisposal);
    }
}

public sealed class VoiceCompPlacementRangePatch : PatchBase
{
    public override string PatchName => nameof(VoiceCompPlacementRangePatch);
    public override Type TargetClass => typeof(AudioPlacementBuilder);
    public override string TargetMethodName => nameof(AudioPlacementBuilder.CreateTimeRange);
    public override Type[] ArgumentTypes => [typeof(WIVSMSequence), typeof(WIVSMPart), typeof(PlacementStorageType), typeof(Yamaha.VOCALOID.VAE.VEAudioBufferList)];
    [HarmonyPostfix]
    private static void Postfix(WIVSMPart part, PlacementStorageType storageType, ref Yamaha.VOCALOID.VAE.VETimeRange __result)
    {
        if (Config.Settings.VoiceCompEnabled && storageType == PlacementStorageType.File && part is WIVSMMidiPart midi &&
            VoiceCompHost.View(midi)?.Composition is { } composition)
            __result.Len = Math.Max(__result.Len, composition.Samples / (double)composition.SampleRate);
    }
}
