using System;
using HarmonyLib;
using VOCALOIDPatcher.Rendering;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class RendererHealthRequestPatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthRequestPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.StartAsyncRendering);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(WIVSMSequence __instance)
    {
        try { RendererHealthMonitor.Request(__instance); }
        catch (Exception e) { Utils.Debug.Print("Renderer request diagnostic: " + e.GetType().Name); }
    }
    [HarmonyFinalizer]
    private static Exception? Finalizer(WIVSMSequence __instance, Exception? __exception)
    {
        if (__exception != null) RendererHealthMonitor.PreparationFailed(__instance, __exception);
        return __exception;
    }
}

public sealed class RendererHealthStopPatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthStopPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.StopAsyncRendering);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance)
    {
        try { RendererHealthMonitor.Stopping(__instance); }
        catch (Exception e) { Utils.Debug.Print("Renderer stop diagnostic: " + e.GetType().Name); }
    }
}

public sealed class RendererHealthDispatchPatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthDispatchPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.InvokeRendererObserver);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, out RendererHealthMonitor.DispatchScope? __state)
    {
        __state = null;
        try { __state = RendererHealthMonitor.BeginDispatch(__instance); }
        catch (Exception e) { Utils.Debug.Print("Renderer dispatch diagnostic: " + e.GetType().Name); }
    }
    [HarmonyFinalizer]
    private static Exception? Finalizer(WIVSMSequence __instance, Exception? __exception, RendererHealthMonitor.DispatchScope? __state)
    {
        if (__exception != null) RendererHealthMonitor.PreparationFailed(__instance, __exception);
        RendererHealthMonitor.EndDispatch(__state);
        return __exception;
    }
}

public sealed class RendererHealthStartedPatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthStartedPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeStartEvent";
    public override Type[] ArgumentTypes => [typeof(IntPtr), typeof(IntPtr)];
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(IntPtr pMidiPart) => RendererHealthMonitor.Observe("started", pMidiPart);
}

public sealed class RendererHealthProgressPatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthProgressPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeProgressEvent";
    public override Type[] ArgumentTypes => [typeof(IntPtr), typeof(IntPtr), typeof(VSMRendererProgress)];
    [HarmonyPrefix]
    private static void Prefix(IntPtr pMidiPart) => RendererHealthMonitor.Observe("progress", pMidiPart);
}

public sealed class RendererHealthCompletedPatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthCompletedPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeCompleteEvent";
    public override Type[] ArgumentTypes => [typeof(IntPtr), typeof(IntPtr), typeof(VSMRendererProgress)];
    // Capture counters before SampledTimingRenderCompletionPatch can retire a rejected snapshot.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(IntPtr pMidiPart) => RendererHealthMonitor.Observe("completed", pMidiPart);
}

public sealed class RendererHealthCanceledPatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthCanceledPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeCancelEvent";
    public override Type[] ArgumentTypes => [typeof(IntPtr), typeof(IntPtr), typeof(VSMRenderCancelReason)];
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(IntPtr pMidiPart, VSMRenderCancelReason reason) => RendererHealthMonitor.Observe("canceled", pMidiPart, (int)reason);
}

public sealed class RendererHealthClosePatch : PatchBase
{
    public override string PatchName => nameof(RendererHealthClosePatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Close);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance) => RendererHealthMonitor.Close((nint)__instance);
}
