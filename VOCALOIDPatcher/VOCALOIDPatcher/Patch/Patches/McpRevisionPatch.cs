using System;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Mcp;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class McpRevisionCommitPatch : PatchBase
{
    public override string PatchName => nameof(McpRevisionCommitPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Commit);
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __result)
    {
        if (__result && !ConsonantTiming.TimingCacheInvalidationScope.IsActive((nint)__instance) && Settings.McpEnabled)
            McpRevisionTracker.ChangedForSource(__instance);
    }
}

public sealed class McpRevisionUndoPatch : PatchBase
{
    public override string PatchName => nameof(McpRevisionUndoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Undo);

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        if (Settings.McpEnabled)
            McpRevisionTracker.ChangedForSource(__instance);
    }
}

public sealed class McpRevisionRedoPatch : PatchBase
{
    public override string PatchName => nameof(McpRevisionRedoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Redo);

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        if (Settings.McpEnabled)
            McpRevisionTracker.ChangedForSource(__instance);
    }
}
