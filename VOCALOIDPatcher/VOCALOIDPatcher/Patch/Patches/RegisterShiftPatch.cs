using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using VOCALOIDPatcher.RegisterShift;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class RegisterShiftProjectLoadPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftProjectLoadPatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "LoadProjectSequenceFile";
    public override Type[] ArgumentTypes => new[] { typeof(string), typeof(WIVSMSequence).MakeByRefType() };

    [HarmonyPrefix]
    private static void Prefix(string filePath, ref RegisterShiftProjectData? __state)
    {
        __state = null;
        if (!File.Exists(filePath) ||
            !(filePath.EndsWith(".vpr", StringComparison.OrdinalIgnoreCase) ||
              filePath.EndsWith(".vpr.bak", StringComparison.OrdinalIgnoreCase)))
            return;
        try { __state = RegisterShiftProjectArchive.Read(filePath); }
        catch (InvalidDataException) { }
        catch (Exception exception)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_RegisterShift_LoadFailed", exception.Message));
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence? vsmSequence, RegisterShiftProjectData? __state)
    {
        if (vsmSequence == null) return;
        try { RegisterShiftService.LoadProjectData(vsmSequence, __state); }
        catch (Exception exception)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_RegisterShift_LoadFailed", exception.Message));
        }
    }
}

public sealed class RegisterShiftProjectSavePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftProjectSavePatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "SaveSequence";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMSequence), typeof(string), typeof(string), typeof(string) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence vsmSequence, string directoryPath,
        string projectName, string extension, bool __result)
    {
        if (!__result || !extension.StartsWith(".vpr", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            RegisterShiftProjectArchive.Write(Path.Combine(directoryPath, projectName + extension),
                RegisterShiftService.BuildProjectData(vsmSequence));
        }
        catch (Exception exception)
        {
            vsmSequence.IsModifiedOutsideOfEditHistory = true;
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_RegisterShift_SaveFailed", exception.Message));
        }
    }
}

public sealed class RegisterShiftDuplicateNotePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftDuplicateNotePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "DuplicateNote";
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick), typeof(WIVSMNote) };
    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, WIVSMNote? __result)
        => RegisterShiftService.CopyNoteValue(note, __result);
}

public sealed class RegisterShiftDuplicatePartPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftDuplicatePartPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => "DuplicatePart";
    public override Type[] ArgumentTypes => new[] { typeof(VSMAbsTick), typeof(WIVSMMidiPart) };
    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart midiPart, WIVSMMidiPart? __result)
        => RegisterShiftService.CopyPartValues(midiPart, __result);
}

public sealed class RegisterShiftDuplicateTrackPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftDuplicateTrackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "DuplicateTrack";
    public override Type[] ArgumentTypes => new[]
        { typeof(ulong), typeof(WIVSMTrack), typeof(string).MakeByRefType() };
    [HarmonyPostfix]
    private static void Postfix(WIVSMTrack track, WIVSMTrack? __result)
        => RegisterShiftService.CopyTrackValues(track, __result);
}

public sealed class RegisterShiftDuplicateSequencePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftDuplicateSequencePatch);
    public override Type TargetClass => typeof(WIVSMSequenceManager);
    public override string TargetMethodName => "DuplicateSequence";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMSequence), typeof(VSMSequenceData) };
    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence vsmSequence, WIVSMSequence? __result)
        => RegisterShiftService.CopySequenceValues(vsmSequence, __result);
}

public sealed class RegisterShiftClipboardNotePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftClipboardNotePatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "PushNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };
    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, WIVSMNote? __result)
        => RegisterShiftService.CopyNoteValue(note, __result);
}

public sealed class RegisterShiftClipboardPartPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftClipboardPartPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "PushMidiPart";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };
    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart midiPart, WIVSMMidiPart? __result)
        => RegisterShiftService.CopyPartValues(midiPart, __result);
}

public sealed class RegisterShiftClipboardClearNotePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftClipboardClearNotePatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "ClearNote";
    [HarmonyPrefix]
    private static void Prefix(WIVSMClipboard __instance, out IntPtr[] __state)
        => __state = BreathVolume.BreathVolumeService.CaptureClipboardNoteHandles(
            __instance, includeNotes: true, includeMidiParts: false);
    [HarmonyPostfix]
    private static void Postfix(IntPtr[] __state) => RegisterShiftService.ReleaseNoteHandles(__state);
}

public sealed class RegisterShiftClipboardClearPartPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftClipboardClearPartPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "ClearMidiPart";
    [HarmonyPrefix]
    private static void Prefix(WIVSMClipboard __instance, out IntPtr[] __state)
        => __state = BreathVolume.BreathVolumeService.CaptureClipboardNoteHandles(
            __instance, includeNotes: false, includeMidiParts: true);
    [HarmonyPostfix]
    private static void Postfix(IntPtr[] __state) => RegisterShiftService.ReleaseNoteHandles(__state);
}

public sealed class RegisterShiftRemoveNotePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftRemoveNotePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "RemoveNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };
    [HarmonyPrefix]
    private static void Prefix(WIVSMNote note, out IntPtr __state)
        => __state = note?.CppObjPtr ?? IntPtr.Zero;
    [HarmonyPostfix]
    private static void Postfix(bool __result, IntPtr __state)
    {
        if (__result) RegisterShiftService.ReleaseNote(__state);
    }
}

public sealed class RegisterShiftJoinNotesPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftJoinNotesPatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "JoinNotes";
    public override Type[] ArgumentTypes => new[] { typeof(System.Collections.Generic.List<WIVSMNote>) };
    [HarmonyPrefix]
    private static void Prefix(WIVSMMidiPart __instance, out IntPtr[] __state)
        => __state = BreathVolume.BreathVolumeService.CapturePartNoteHandles(__instance);
    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, bool __result, IntPtr[] __state)
    {
        if (__result) RegisterShiftService.ReleaseMissingPartNotes(__instance, __state);
    }
}

public sealed class RegisterShiftG2paDeleteNotePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftG2paDeleteNotePatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "DeleteNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };
    [HarmonyPrefix]
    private static void Prefix(WIVSMNote note, out IntPtr __state)
        => __state = note?.CppObjPtr ?? IntPtr.Zero;
    [HarmonyPostfix]
    private static void Postfix(bool __result, IntPtr __state)
    {
        if (__result) RegisterShiftService.ReleaseNote(__state);
    }
}

public sealed class RegisterShiftRemovePartPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftRemovePartPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => "RemovePart";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };
    [HarmonyPrefix]
    private static void Prefix(WIVSMMidiPart midiPart, out BreathVolume.BreathNativeObjectHandles __state)
        => __state = BreathVolume.BreathVolumeService.CapturePartObjects(midiPart);
    [HarmonyPostfix]
    private static void Postfix(bool __result, BreathVolume.BreathNativeObjectHandles __state)
    {
        if (__result) RegisterShiftService.ReleasePart(__state);
    }
}

public sealed class RegisterShiftRemoveTrackPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftRemoveTrackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "RemoveTrack";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMTrack) };
    [HarmonyPrefix]
    private static void Prefix(WIVSMTrack track, out BreathVolume.BreathNativeObjectHandles __state)
        => __state = BreathVolume.BreathVolumeService.CaptureTrackObjects(track);
    [HarmonyPostfix]
    private static void Postfix(bool __result, BreathVolume.BreathNativeObjectHandles __state)
    {
        if (__result) RegisterShiftService.ReleasePart(__state);
    }
}

public sealed class RegisterShiftRendererStartPatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftRendererStartPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererStarted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverStartEventArgs) };
    [HarmonyPrefix]
    private static void Prefix(MusicalEditorViewModel __instance, RendererObserverStartEventArgs e)
    {
        if (__instance.VSMSequence != null && e?.MidiPart is { IsAi: false } part)
        {
            RegisterShiftService.PublishPart(__instance.VSMSequence, part);
            RegisterShiftService.LogNativeStatus("render-start", part);
        }
    }
}

public sealed class RegisterShiftRendererCompletePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftRendererCompletePatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "OnRendererCompleted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCompleteEventArgs) };
    [HarmonyPrefix]
    private static void Prefix(RendererObserverCompleteEventArgs e)
        => RegisterShiftService.LogNativeStatus("render-complete", e?.MidiPart);

    [HarmonyPostfix]
    private static void Postfix()
        => RegisterShiftService.RefreshUi();
}

public sealed class RegisterShiftSequenceClosePatch : PatchBase
{
    public override string PatchName => nameof(RegisterShiftSequenceClosePatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Close";
    [HarmonyPrefix]
    private static void Prefix() => NativeRegisterShift.Clear();
}
