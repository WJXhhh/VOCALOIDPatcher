using System;
using HarmonyLib;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class ControllerRemovalScopePatch : PatchBase
{
    public override string PatchName => nameof(ControllerRemovalScopePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => nameof(WIVSMMidiPart.RemoveController);
    public override Type[] ArgumentTypes => [typeof(WIVSMMidiController)];

    [HarmonyPrefix]
    private static void Prefix(WIVSMMidiPart __instance, WIVSMMidiController controller, out nint __state)
    {
        __state = 0;
        try { __state = SampledTimingHost.BeforeControllerRemoval(__instance, controller); }
        catch (Exception error) { Debug.Print("Controller removal scope: " + error.GetType().Name); }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiController controller, bool __result, nint __state)
    {
        if (!__result && __state != 0) SampledTimingHost.FailedControllerRemoval(__state, controller);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(WIVSMMidiController controller, nint __state, Exception? __exception)
    {
        if (__exception != null && __state != 0) SampledTimingHost.FailedControllerRemoval(__state, controller);
        return __exception;
    }
}

public sealed class ControllerRemovalRollbackPatch : PatchBase
{
    public override string PatchName => nameof(ControllerRemovalRollbackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Rollback);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance) => SampledTimingHost.EndInputMutation(__instance);
}
