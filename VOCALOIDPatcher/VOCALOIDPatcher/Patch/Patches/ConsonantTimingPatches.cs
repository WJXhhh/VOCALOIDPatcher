using System;
using System.Reflection;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class ConsonantTimingDrawPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingDrawPatch);
    public override Type TargetClass => typeof(PianorollView);
    public override string TargetMethodName => "DrawPhonemeCanvas";
    public override Type[] ArgumentTypes => new[] { typeof(MusicalEditorViewModel) };

    [HarmonyPostfix]
    private static void Postfix(PianorollView __instance) => ConsonantTimingOverlay.Refresh(__instance);
}

public sealed class ConsonantTimingViewportPatch : PatchBase
{
    private static readonly FieldInfo? ViewModel = AccessTools.Field(typeof(MusicalEditorDivision), "_vm");
    public override string PatchName => nameof(ConsonantTimingViewportPatch);
    public override Type TargetClass => typeof(MusicalEditorDivision);
    public override string TargetMethodName => "UpdateViewport";

    [HarmonyPostfix]
    private static void Postfix(MusicalEditorDivision __instance)
    {
        if (ViewModel?.GetValue(__instance) is MusicalEditorViewModel { PianorollView: { } view })
            ConsonantTimingOverlay.Refresh(view);
    }
}

public sealed class ConsonantTimingSearchPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingSearchPatch);
    public override Type TargetClass => typeof(PianorollView);
    public override string TargetMethodName => "SearchEmotionObject";
    public override Type[] ArgumentTypes => new[] { typeof(Point) };

    [HarmonyPrefix]
    private static bool Prefix(PianorollView __instance, Point pt,
        ref PianorollView.EmotionModeSearchResults __result)
    {
        if (!ConsonantTimingOverlay.Search(__instance, pt, out var result)) return true;
        __result = result;
        return false;
    }
}

public sealed class ConsonantTimingHoverPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingHoverPatch);
    public override Type TargetClass => typeof(PianorollView);
    public override string TargetMethodName => "UpdateEmotionHitRectangle";
    public override Type[] ArgumentTypes => new[] { typeof(PianorollView.EmotionModeSearchResults) };

    [HarmonyPrefix]
    private static bool Prefix(PianorollView __instance, PianorollView.EmotionModeSearchResults searchResults)
    {
        if (!ConsonantTimingOverlay.HasInlineFeedback(__instance, searchResults)) return true;
        __instance.HideEmotionHitRectangle();
        return false;
    }
}

public sealed class ConsonantTimingRenderCompletedPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingRenderCompletedPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererCompleted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCompleteEventArgs) };

    [HarmonyPostfix]
    private static void Postfix(MusicalEditorViewModel __instance, RendererObserverCompleteEventArgs e)
    {
        if (e?.MidiPart != null) ConsonantTimingOverlay.RenderCompleted(__instance, e.MidiPart);
    }
}

public sealed class ConsonantTimingShutdownPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingShutdownPatch);
    public override Type TargetClass => typeof(App);
    public override string TargetMethodName => "TerminateModule";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix() => ConsonantTimingRuntime.Shutdown();
}

public sealed class ConsonantTimingCommitPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingCommitPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Commit);
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, out bool __state)
    {
        __state = __instance.IsStaged;
        if (__state) SampledTimingHost.BeforeSourceChange(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __state, bool __result)
    {
        if (__state && __result)
        {
            ConsonantTimingRuntime.SourceChanged(__instance);
            PhonemeVolume.PhonemeVolumeHost.SourceChanged(__instance);
        }
    }
}

public sealed class ConsonantTimingUndoPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingUndoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Undo);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance) => SampledTimingHost.BeforeSourceChange(__instance);
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        try { BreathVolume.CustomParameterHistoryCoordinator.CompleteNativeHistory(__instance, undo: true); }
        catch (Exception e) { SampledTimingHost.Recover(__instance, e); return; }
        ConsonantTimingRuntime.SourceChanged(__instance);
        PhonemeVolume.PhonemeVolumeHost.SourceChanged(__instance);
    }
}

public sealed class ConsonantTimingRedoPatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingRedoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Redo);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance) => SampledTimingHost.BeforeSourceChange(__instance);
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        try { BreathVolume.CustomParameterHistoryCoordinator.CompleteNativeHistory(__instance, undo: false); }
        catch (Exception e) { SampledTimingHost.Recover(__instance, e); return; }
        ConsonantTimingRuntime.SourceChanged(__instance);
        PhonemeVolume.PhonemeVolumeHost.SourceChanged(__instance);
    }
}

public sealed class ConsonantTimingClosePatch : PatchBase
{
    public override string PatchName => nameof(ConsonantTimingClosePatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Close);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, out nint __state)
    {
        __state = (nint)__instance;
        PhonemeVolume.PhonemeVolumeHost.Close(__instance);
        if (SampledTimingHost.HasData(__instance)) __instance.StopAsyncRendering();
        ConsonantTimingRuntime.BeforeClose(__instance);
    }
    [HarmonyPostfix]
    private static void Postfix(bool __result, nint __state)
    {
        if (__result) SampledTimingHost.Close(__state);
    }
}
