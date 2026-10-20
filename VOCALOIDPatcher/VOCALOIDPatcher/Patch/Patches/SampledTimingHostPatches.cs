using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using VOCALOIDPatcher.DurationSelection;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

// Native VSM rejects score association when the first vowel is delayed past
// the musical onset. Supply only the cached, native-planner-verified geometry
// for controlled notes; never run a query from this accessor/drawing path.
public sealed class SampledTimingPhonemePositionsPatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingPhonemePositionsPatch);
    public override Type TargetClass => typeof(WIVSMNote);
    public override string TargetMethodName => nameof(WIVSMNote.GetPhonemePositions);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static bool Prefix(WIVSMNote __instance, ref List<int> __result)
    {
        try
        {
            if (SampledTimingHost.VerifiedPositions(__instance) is not { } positions) return true;
            __result = positions; return false;
        }
        catch (Exception e) { Debug.Print("Timing position projection: " + e.GetType().Name); return true; }
    }
}

public sealed class SampledTimingBeginPhonemePatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingBeginPhonemePatch);
    public override Type TargetClass => typeof(WIVSMNote);
    public override string TargetMethodName => "get_BeginPhonemeTick";
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static bool Prefix(WIVSMNote __instance, ref VSMRelTick __result)
    {
        try
        {
            if (SampledTimingHost.VerifiedPositions(__instance) is not { Count: > 0 } positions) return true;
            __result = new VSMRelTick(positions[0]); return false;
        }
        catch (Exception e) { Debug.Print("Timing begin projection: " + e.GetType().Name); return true; }
    }
}

public sealed class SampledTimingEndPhonemePatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingEndPhonemePatch);
    public override Type TargetClass => typeof(WIVSMNote);
    public override string TargetMethodName => "get_EndPhonemeTick";
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix]
    private static bool Prefix(WIVSMNote __instance, ref VSMRelTick __result)
    {
        try
        {
            if (SampledTimingHost.VerifiedPositions(__instance) is not { Count: > 0 } positions) return true;
            __result = new VSMRelTick(positions[^1]); return false;
        }
        catch (Exception e) { Debug.Print("Timing end projection: " + e.GetType().Name); return true; }
    }
}

public sealed class SampledTimingProjectLoadPatch : PatchBase
{
    internal sealed record LoadState(IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> Timing,
        PhonemeVolume.PhonemeVolumeArchive.Entry[] Volume, DurationSelectionArchive.Entry[] Duration, TransitionArchiveData Transitions);
    public override string PatchName => nameof(SampledTimingProjectLoadPatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "LoadProjectSequenceFile";
    public override Type[] ArgumentTypes => [typeof(string), typeof(WIVSMSequence).MakeByRefType()];
    [HarmonyPrefix]
    private static bool Prefix(string filePath, ref WIVSMSequence? vsmSequence,
        ref (Yamaha.VOCALOID.Sequence.LoadResult, bool) __result,
        out LoadState? __state)
    {
        __state = null;
        if (!filePath.EndsWith(".vpr", StringComparison.OrdinalIgnoreCase) && !filePath.EndsWith(".vpr.bak", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            __state = new(SampledTimingProjectArchive.Read(filePath), PhonemeVolume.PhonemeVolumeArchive.Read(filePath), DurationSelectionArchive.Read(filePath), TransitionProjectArchive.Read(filePath));
            return true;
        }
        catch (Exception e)
        {
            Debug.Print("Timing project load rejected: " + e.GetType().Name);
            vsmSequence = null; __result = (Yamaha.VOCALOID.Sequence.LoadResult.GeneralError, false); return false;
        }
    }
    // BVL establishes native note generations while restoring the same VPR.
    // Bind custom controls only after that lifecycle work has completed.
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ref WIVSMSequence? vsmSequence, LoadState? __state,
        ref (Yamaha.VOCALOID.Sequence.LoadResult, bool) __result)
    {
        if (vsmSequence == null || __result.Item1 != Yamaha.VOCALOID.Sequence.LoadResult.Success) return;
        try
        {
            SampledTimingHost.Load(vsmSequence, __state?.Timing, __state?.Transitions);
            PhonemeVolume.PhonemeVolumeHost.Load(vsmSequence, __state?.Volume ?? []);
            DurationSelectionHost.Load(vsmSequence, __state?.Duration ?? []);
        }
        catch (Exception e)
        {
            Debug.Print("Timing project restoration rejected: " + e.GetType().Name + ": " + e.Message);
            nint pointer = (nint)vsmSequence;
            try { vsmSequence.Close(); }
            finally { SampledTimingHost.Close(pointer); }
            vsmSequence = null;
            __result = (Yamaha.VOCALOID.Sequence.LoadResult.GeneralError, false);
        }
    }
}

public sealed class SampledTimingProjectSavePatch : PatchBase
{
    internal sealed record SaveState(string Path, string? Backup, PhonemeVolume.PhonemeVolumeArchive.Entry[] Volume, DurationSelectionArchive.Entry[] Duration);
    [ThreadStatic] private static HashSet<nint>? _saves;
    internal static bool OwnsSave(WIVSMSequence source) => _saves?.Contains((nint)source) == true;
    public override string PatchName => nameof(SampledTimingProjectSavePatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "SaveSequence";
    public override Type[] ArgumentTypes => [typeof(WIVSMSequence), typeof(string), typeof(string), typeof(string)];
    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence vsmSequence, string directoryPath, string projectName, string extension, out SaveState? __state)
    {
        __state = null;
        if (!extension.StartsWith(".vpr", StringComparison.OrdinalIgnoreCase)) return;
        var volume = PhonemeVolume.PhonemeVolumeHost.Snapshot(vsmSequence);
        var duration = DurationSelectionHost.Snapshot(vsmSequence);
        if (!SampledTimingHost.HasData(vsmSequence) && volume.Length == 0 && !PhonemeVolume.PhonemeVolumeHost.HasState(vsmSequence) &&
            !DurationSelectionHost.HasState(vsmSequence)) return;
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directoryPath, projectName + extension));
        string? backup = File.Exists(path) ? path + "." + Guid.NewGuid().ToString("N") + ".timing-save" : null;
        if (backup != null) File.Copy(path, backup, false);
        __state = new(path, backup, volume, duration);
        (_saves ??= new()).Add((nint)vsmSequence);
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(WIVSMSequence vsmSequence, string extension, ref bool __result, SaveState? __state)
    {
        if (__state == null) return;
        string temporary = __state.Path + "." + Guid.NewGuid().ToString("N") + ".extensions.tmp";
        try
        {
            if (!__result) throw new IOException("Project save did not complete.");
            File.Copy(__state.Path, temporary, false);
            SampledTimingHost.Save(vsmSequence, temporary, false);
            PhonemeVolume.PhonemeVolumeArchive.Write(temporary, __state.Volume);
            DurationSelectionArchive.Write(temporary, __state.Duration);
            File.Replace(temporary, __state.Path, null, true);
            if (extension.Equals(".vpr", StringComparison.OrdinalIgnoreCase))
            {
                SampledTimingHost.MarkSaved(vsmSequence); DurationSelectionHost.MarkSaved(vsmSequence);
                BreathVolume.BreathVolumeService.MarkExtensionSaved(vsmSequence);
            }
        }
        catch (Exception e)
        {
            __result = false; vsmSequence.IsModifiedOutsideOfEditHistory = true;
            if (__state.Backup != null) File.Copy(__state.Backup, __state.Path, true);
            else if (File.Exists(__state.Path)) File.Delete(__state.Path);
            Debug.Print("Timing project save rejected: " + e.GetType().Name);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, SaveState? __state, WIVSMSequence vsmSequence)
    {
        _saves?.Remove((nint)vsmSequence);
        try { if (__state?.Backup != null) File.Delete(__state.Backup); }
        catch (Exception e) { Debug.Print("Timing save cleanup: " + e.GetType().Name); }
        return __exception;
    }
}

// Install native entry observers before the first sequence can render. The UI
// flag controls only the tool; saved controls must still render when hidden.
public sealed class DurationSelectionStartupPatch : PatchBase
{
    public override string PatchName => nameof(DurationSelectionStartupPatch);
    public override Type TargetClass => typeof(WIVSMSequenceManager);
    public override string TargetMethodName => ".ctor";
    public override bool IsConstructor => true;
    public override Type[] ArgumentTypes => [typeof(IntPtr), typeof(bool)];
    [HarmonyPostfix]
    private static void Postfix()
    {
        try { NativeDurationSelection.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll")); }
        catch (Exception e) { Debug.Print("Duration startup adapter unavailable: " + e.GetType().Name); }
    }
}

public sealed class SampledTimingRenderStartPatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingRenderStartPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.StartAsyncRendering);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static bool Prefix(WIVSMSequence __instance)
    {
        try { SampledTimingHost.BeforeRendering(__instance); return true; }
        catch (Exception e) { return SampledTimingHost.Recover(__instance,e); }
    }
}

public sealed class SampledTimingRenderCompletionPatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingRenderCompletionPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeCompleteEvent";
    public override Type[] ArgumentTypes => [typeof(IntPtr), typeof(IntPtr), typeof(VSMRendererProgress)];
    [HarmonyPrefix]
    private static bool Prefix(IntPtr pMidiPart)
    {
        try { return SampledTimingHost.AcceptRenderCompletion(pMidiPart); }
        catch (Exception e) { Debug.Print("Timing completion fallback: " + e.GetType().Name); return true; }
    }
}

public sealed class SampledTimingRenderCancellationPatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingRenderCancellationPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeCancelEvent";
    public override Type[] ArgumentTypes => [typeof(IntPtr), typeof(IntPtr), typeof(VSMRenderCancelReason)];
    [HarmonyPrefix]
    private static void Prefix(IntPtr pMidiPart, VSMRenderCancelReason reason)
    {
        try { SampledTimingHost.ObserveRenderCancellation(pMidiPart, reason); }
        catch (Exception e) { Debug.Print("Timing cancellation diagnostic: " + e.GetType().Name); }
    }
}

public sealed class SampledTimingDirtyPatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingDirtyPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "get_IsDirty";
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, ref bool __result) => __result |= SampledTimingHost.IsDirty(__instance);
}

public sealed class SampledTimingDuplicateSequencePatch : PatchBase
{
    public override string PatchName => nameof(SampledTimingDuplicateSequencePatch);
    public override Type TargetClass => typeof(WIVSMSequenceManager);
    public override string TargetMethodName => "DuplicateSequence";
    public override Type[] ArgumentTypes => [typeof(WIVSMSequence), typeof(VSMSequenceData)];
    // BVL's copy callback recreates destination note generations.
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(WIVSMSequence vsmSequence, WIVSMSequence? __result) => SampledTimingHost.Copy(vsmSequence, __result);
}
