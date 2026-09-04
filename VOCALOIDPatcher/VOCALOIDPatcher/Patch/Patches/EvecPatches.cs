using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.Properties;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class EvecProjectLoadPatch : PatchBase
{
    public override string PatchName => nameof(EvecProjectLoadPatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "LoadProjectSequenceFile";
    public override Type[] ArgumentTypes => new[] { typeof(string), typeof(WIVSMSequence).MakeByRefType() };

    [HarmonyPrefix]
    private static void Prefix(string filePath, out EvecProjectLoadState __state)
    {
        EvecProjectData? data = null;
        if (!File.Exists(filePath) ||
            !(filePath.EndsWith(".vpr", StringComparison.OrdinalIgnoreCase) ||
              filePath.EndsWith(".vpr.bak", StringComparison.OrdinalIgnoreCase)))
        {
            __state = EvecService.BeginProjectLoad(null);
            return;
        }

        try
        {
            data = EvecProjectArchive.Read(filePath);
        }
        catch (InvalidDataException) { }
        catch (Exception exception)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Evec_LoadFailed", exception.Message));
        }

        __state = EvecService.BeginProjectLoad(data);
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence? vsmSequence, EvecProjectLoadState? __state)
    {
        if (vsmSequence == null) return;
        try
        {
            EvecService.CompleteProjectLoad(vsmSequence, __state);
        }
        catch (Exception exception)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Evec_LoadFailed", exception.Message));
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, EvecProjectLoadState? __state)
    {
        EvecService.EndProjectLoad(__state);
        return __exception;
    }
}

public sealed class EvecProjectSavePatch : PatchBase
{
    public override string PatchName => nameof(EvecProjectSavePatch);
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
            EvecProjectArchive.Write(
                Path.Combine(directoryPath, projectName + extension),
                EvecService.BuildProjectData(vsmSequence));
        }
        catch (Exception exception)
        {
            vsmSequence.IsModifiedOutsideOfEditHistory = true;
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Evec_SaveFailed", exception.Message));
        }
    }
}

public sealed class EvecSequenceUndoPatch : PatchBase
{
    public override string PatchName => nameof(EvecSequenceUndoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Undo);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        try
        {
            EvecService.ReconcileAfterUndo(__instance);
        }
        catch
        {
            // Logical history is advisory; native Undo must always survive.
        }
    }
}

public sealed class EvecSequenceRedoPatch : PatchBase
{
    public override string PatchName => nameof(EvecSequenceRedoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Redo);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        try
        {
            EvecService.ReconcileAfterRedo(__instance);
        }
        catch
        {
            // Logical history is advisory; native Redo must always survive.
        }
    }
}

public sealed class EvecSequenceTempoCommitPatch : PatchBase
{
    public override string PatchName => nameof(EvecSequenceTempoCommitPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Commit);
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, out bool __state)
    {
        try
        {
            __state = EvecService.ApplyPendingTempoTiming(__instance);
        }
        catch
        {
            __state = false;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __result, bool __state)
    {
        if (!__result && __state)
            EvecService.MarkTempoChanged(__instance);
        EvecService.CompletePendingHistory(__instance, __result);
    }
}

public sealed class EvecSequenceTempoRollbackPatch : PatchBase
{
    public override string PatchName => nameof(EvecSequenceTempoRollbackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Rollback);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        EvecService.ClearPendingTempoTiming(__instance);
        EvecService.ClearPendingHistory(__instance);
    }
}

public sealed class EvecInsertTempoPatch : PatchBase
{
    public override string PatchName => nameof(EvecInsertTempoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.InsertTempo);
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick), typeof(int) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, WIVSMTempo? __result)
    {
        if (__result != null)
            EvecService.MarkTempoChanged(__instance);
    }
}

public sealed class EvecDuplicateTempoPatch : PatchBase
{
    public override string PatchName => nameof(EvecDuplicateTempoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.DuplicateTempo);
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick), typeof(WIVSMTempo) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, WIVSMTempo? __result)
    {
        if (__result != null)
            EvecService.MarkTempoChanged(__instance);
    }
}

public sealed class EvecRemoveTempoPatch : PatchBase
{
    public override string PatchName => nameof(EvecRemoveTempoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.RemoveTempo);
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMTempo) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __result)
    {
        if (__result)
            EvecService.MarkTempoChanged(__instance);
    }
}

public sealed class EvecMoveTempoPatch : PatchBase
{
    public override string PatchName => nameof(EvecMoveTempoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.MoveTempo);
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick), typeof(WIVSMTempo) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __result)
    {
        if (__result)
            EvecService.MarkTempoChanged(__instance);
    }
}

public sealed class EvecTempoValuePatch : PatchBase
{
    public override string PatchName => nameof(EvecTempoValuePatch);
    public override Type TargetClass => typeof(WIVSMTempo);
    public override string TargetMethodName => "set_Value";
    public override Type[] ArgumentTypes => new[] { typeof(int) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMTempo __instance, int value, out WIVSMSequence? __state)
    {
        __state = __instance.Value != value ? __instance.Parent : null;
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence? __state) =>
        EvecService.MarkTempoChanged(__state);
}

public sealed class EvecGlobalTempoPatch : PatchBase
{
    public override string PatchName => nameof(EvecGlobalTempoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "set_GlobalTempo";
    public override Type[] ArgumentTypes => new[] { typeof(int) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, int value, out bool __state) =>
        __state = __instance.GlobalTempo != value;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __state)
    {
        if (__state)
            EvecService.MarkTempoChanged(__instance);
    }
}

public sealed class EvecGlobalTempoEnabledPatch : PatchBase
{
    public override string PatchName => nameof(EvecGlobalTempoEnabledPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "set_IsGlobalTempoEnabled";
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, bool value, out bool __state) =>
        __state = __instance.IsGlobalTempoEnabled != value;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __state)
    {
        if (__state)
            EvecService.MarkTempoChanged(__instance);
    }
}

public sealed class EvecAraTempoEnabledPatch : PatchBase
{
    public override string PatchName => nameof(EvecAraTempoEnabledPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "set_IsARATempoEnabled";
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, bool value, out bool __state) =>
        __state = __instance.IsARATempoEnabled != value;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __state)
    {
        if (__state)
            EvecService.MarkTempoChanged(__instance);
    }
}

public sealed class EvecDuplicateNotePatch : PatchBase
{
    public override string PatchName => nameof(EvecDuplicateNotePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "DuplicateNote";
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick), typeof(WIVSMNote) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, WIVSMNote? __result)
    {
        if (__result == null || note == null)
            return;

        try
        {
            EvecService.CloneState(note, __result);
        }
        catch
        {
            // Native duplication must survive a sidecar/cache failure.
        }
    }
}

public sealed class EvecDuplicatePartPatch : PatchBase
{
    public override string PatchName => nameof(EvecDuplicatePartPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => nameof(WIVSMMidiTrack.DuplicatePart);
    public override Type[] ArgumentTypes => new[] { typeof(VSMAbsTick), typeof(WIVSMMidiPart) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart midiPart, WIVSMMidiPart? __result)
    {
        try
        {
            EvecService.ClonePartStates(midiPart, __result);
        }
        catch
        {
            // Native part duplication must survive optional sidecar copying.
        }
    }
}

public sealed class EvecDividePartPatch : PatchBase
{
    public override string PatchName => nameof(EvecDividePartPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => nameof(WIVSMMidiTrack.DividePart);
    public override Type[] ArgumentTypes =>
        new[] { typeof(VSMRelTick), typeof(WIVSMMidiPart) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMMidiPart midiPart,
        out EvecService.PartStructureTransfer? __state)
    {
        __state = null;
        try
        {
            __state = EvecService.PreparePartStructureTransfer(new[] { midiPart });
        }
        catch
        {
            // Native Part division remains authoritative.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMMidiPart midiPart,
        WIVSMMidiPart? __result,
        EvecService.PartStructureTransfer? __state)
    {
        if (__result == null)
            return;
        try
        {
            EvecService.CompletePartStructureTransfer(
                __state,
                new[] { midiPart, __result });
        }
        catch
        {
            // Optional logical-state transfer must not fail native division.
        }
    }
}

public sealed class EvecJoinPartsPatch : PatchBase
{
    public override string PatchName => nameof(EvecJoinPartsPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => nameof(WIVSMMidiTrack.JoinParts);
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart[]) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMMidiPart[] parts,
        out EvecService.PartStructureTransfer? __state)
    {
        __state = null;
        try
        {
            __state = EvecService.PreparePartStructureTransfer(parts);
        }
        catch
        {
            // Native Part joining remains authoritative.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMMidiPart? __result,
        EvecService.PartStructureTransfer? __state)
    {
        if (__result == null)
            return;
        try
        {
            EvecService.CompletePartStructureTransfer(__state, new[] { __result });
        }
        catch
        {
            // Optional logical-state transfer must not fail native joining.
        }
    }
}

public sealed class EvecRemovePartPatch : PatchBase
{
    public override string PatchName => nameof(EvecRemovePartPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => nameof(WIVSMMidiTrack.RemovePart);
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMMidiPart midiPart,
        out EvecService.RemovalTransfer? __state)
    {
        __state = null;
        try
        {
            __state = EvecService.PrepareRemoval(new[] { midiPart });
        }
        catch
        {
            // Native Part removal remains authoritative.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(bool __result, EvecService.RemovalTransfer? __state)
    {
        if (!__result)
            return;
        try
        {
            EvecService.CompleteRemoval(__state);
        }
        catch
        {
            // Native Part removal must survive optional cache maintenance.
        }
    }
}

public sealed class EvecRemoveTrackPatch : PatchBase
{
    public override string PatchName => nameof(EvecRemoveTrackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.RemoveTrack);
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMTrack) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMTrack track,
        out EvecService.RemovalTransfer? __state)
    {
        __state = null;
        try
        {
            __state = EvecService.PrepareTrackRemoval(track);
        }
        catch
        {
            // Native track removal remains authoritative.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(bool __result, EvecService.RemovalTransfer? __state)
    {
        if (!__result)
            return;
        try
        {
            EvecService.CompleteRemoval(__state);
        }
        catch
        {
            // Native track removal must survive optional cache maintenance.
        }
    }
}

public sealed class EvecSequenceClosePatch : PatchBase
{
    public override string PatchName => nameof(EvecSequenceClosePatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.Close);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMSequence __instance,
        out EvecService.SequenceCloseState __state)
    {
        try
        {
            __state = EvecService.CaptureSequenceClose(__instance);
        }
        catch
        {
            __state = new EvecService.SequenceCloseState(
                (IntPtr)__instance,
                Array.Empty<IntPtr>());
        }
    }

    [HarmonyPostfix]
    private static void Postfix(bool __result, EvecService.SequenceCloseState __state)
    {
        if (!__result)
            return;
        try
        {
            EvecService.CompleteSequenceClose(__state);
        }
        catch
        {
            // Native sequence closing must survive optional cache cleanup.
        }
    }
}

public sealed class EvecDuplicateTrackPatch : PatchBase
{
    public override string PatchName => nameof(EvecDuplicateTrackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => nameof(WIVSMSequence.DuplicateTrack);
    public override Type[] ArgumentTypes => new[]
    {
        typeof(ulong),
        typeof(WIVSMTrack),
        typeof(string).MakeByRefType()
    };

    [HarmonyPostfix]
    private static void Postfix(WIVSMTrack track, WIVSMTrack? __result)
    {
        try
        {
            EvecService.CloneTrackStates(track, __result);
        }
        catch
        {
            // Native track duplication must survive optional sidecar copying.
        }
    }
}

public sealed class EvecDuplicateSequencePatch : PatchBase
{
    public override string PatchName => nameof(EvecDuplicateSequencePatch);
    public override Type TargetClass => typeof(WIVSMSequenceManager);
    public override string TargetMethodName => nameof(WIVSMSequenceManager.DuplicateSequence);
    public override Type[] ArgumentTypes => new[]
    {
        typeof(WIVSMSequence),
        typeof(VSMSequenceData)
    };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence vsmSequence, WIVSMSequence? __result)
    {
        try
        {
            EvecService.CloneSequenceStates(vsmSequence, __result);
        }
        catch
        {
            // Native sequence duplication must survive optional sidecar copying.
        }
    }
}

public sealed class EvecDivideNotePatch : PatchBase
{
    public override string PatchName => nameof(EvecDivideNotePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => nameof(WIVSMMidiPart.DivideNote);
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote), typeof(VSMRelTick) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, WIVSMNote? __result)
    {
        if (__result == null || note == null)
            return;

        try
        {
            EvecService.CloneState(note, __result);
            EvecService.ReapplyTimingAfterGeometryChange(note);
            EvecService.ReapplyTimingAfterGeometryChange(__result);
        }
        catch
        {
            // Native division must survive a sidecar/cache failure.
        }
    }
}

public sealed class EvecJoinNotesPatch : PatchBase
{
    public override string PatchName => nameof(EvecJoinNotesPatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => nameof(WIVSMMidiPart.JoinNotes);
    public override Type[] ArgumentTypes => new[] { typeof(List<WIVSMNote>) };

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMMidiPart __instance,
        List<WIVSMNote> notes,
        bool __result)
    {
        if (!__result || notes == null)
            return;

        try
        {
            foreach (var note in notes
                         .Where(item => item != null)
                         .GroupBy(item => item.CppObjPtr)
                         .Select(group => group.First()))
            {
                if (__instance.HasNote(note))
                    EvecService.ReapplyTimingAfterGeometryChange(note);
                else
                    EvecService.RemoveNote(note);
            }
        }
        catch
        {
            // Native joining must survive cache/timing maintenance failure.
        }
    }
}

public sealed class EvecNoteDurationPatch : PatchBase
{
    public override string PatchName => nameof(EvecNoteDurationPatch);
    public override Type TargetClass => typeof(WIVSMNote);
    public override string TargetMethodName => nameof(WIVSMNote.SetDuration);
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote __instance, bool __result)
    {
        if (!__result)
            return;

        try
        {
            EvecService.ReapplyTimingAfterGeometryChange(__instance);
        }
        catch
        {
            // Native duration edits must remain usable without EVEC timing.
        }
    }
}

public sealed class EvecNoteResizeLeftPatch : PatchBase
{
    public override string PatchName => nameof(EvecNoteResizeLeftPatch);
    public override Type TargetClass => typeof(WIVSMNote);
    public override string TargetMethodName => nameof(WIVSMNote.ResizeLeft);
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote __instance, bool __result)
    {
        if (!__result)
            return;

        try
        {
            EvecService.ReapplyTimingAfterGeometryChange(__instance);
        }
        catch
        {
            // Native left-edge edits must remain usable without EVEC timing.
        }
    }
}

public sealed class EvecMoveNoteTimingPatch : PatchBase
{
    public override string PatchName => nameof(EvecMoveNoteTimingPatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => nameof(WIVSMMidiPart.MoveNote);
    public override Type[] ArgumentTypes =>
        new[] { typeof(VSMRelTick), typeof(WIVSMNote) };

    [HarmonyPostfix]
    private static void Postfix(bool __result, WIVSMNote note)
    {
        if (!__result || note == null)
            return;

        try
        {
            EvecService.ReapplyTimingAfterGeometryChange(note);
        }
        catch
        {
            // Native note movement must remain usable without EVEC timing.
        }
    }
}

public sealed class EvecMoveMidiPartTimingPatch : PatchBase
{
    public override string PatchName => nameof(EvecMoveMidiPartTimingPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => nameof(WIVSMMidiTrack.MovePart);
    public override Type[] ArgumentTypes => new[]
    {
        typeof(VSMAbsTick),
        typeof(WIVSMMidiTrack),
        typeof(WIVSMMidiPart)
    };

    [HarmonyPostfix]
    private static void Postfix(bool __result, WIVSMMidiPart midiPart)
    {
        if (!__result || midiPart == null)
            return;

        try
        {
            EvecService.ReapplyPartTimingAfterPositionChange(midiPart);
        }
        catch
        {
            // Native Part movement must remain usable without EVEC timing.
        }
    }
}

public sealed class EvecRawPhonemeWritePatch : PatchBase
{
    public override string PatchName => nameof(EvecRawPhonemeWritePatch);
    public override Type TargetClass => typeof(WIVSMNote);
    public override string TargetMethodName => nameof(WIVSMNote.SetPhonemes);
    public override Type[] ArgumentTypes =>
        new[] { typeof(string), typeof(bool), typeof(int) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote __instance, bool __result)
    {
        try
        {
            EvecService.ReconcileRawPhonemeWrite(__instance, __result);
        }
        catch
        {
            // Raw native phoneme writes must remain authoritative.
        }
    }
}

public sealed class EvecLyricMoveLeftPatch : PatchBase
{
    public override string PatchName => nameof(EvecLyricMoveLeftPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => nameof(MusicalEditorViewModel.LyricMoveLeft);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(
        MusicalEditorViewModel __instance,
        out EvecService.LyricMoveTransfer? __state)
    {
        __state = null;
        try
        {
            if (Settings.EvecEnabled && __instance.ActivePart is { } part)
                __state = EvecService.PrepareLyricMove(part, moveRight: false);
        }
        catch
        {
            // Native lyric movement remains usable without EVEC transfer.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(EvecService.LyricMoveTransfer? __state)
    {
        try { EvecService.CompleteLyricMove(__state); }
        catch { EvecService.AbortLyricMove(__state); }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        EvecService.LyricMoveTransfer? __state)
    {
        if (__exception != null)
            EvecService.AbortLyricMove(__state);
        return __exception;
    }
}

public sealed class EvecLyricMoveRightPatch : PatchBase
{
    public override string PatchName => nameof(EvecLyricMoveRightPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => nameof(MusicalEditorViewModel.LyricMoveRight);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(
        MusicalEditorViewModel __instance,
        out EvecService.LyricMoveTransfer? __state)
    {
        __state = null;
        try
        {
            if (Settings.EvecEnabled && __instance.ActivePart is { } part)
                __state = EvecService.PrepareLyricMove(part, moveRight: true);
        }
        catch
        {
            // Native lyric movement remains usable without EVEC transfer.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(EvecService.LyricMoveTransfer? __state)
    {
        try { EvecService.CompleteLyricMove(__state); }
        catch { EvecService.AbortLyricMove(__state); }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        EvecService.LyricMoveTransfer? __state)
    {
        if (__exception != null)
            EvecService.AbortLyricMove(__state);
        return __exception;
    }
}

public sealed class EvecClipboardNotePatch : PatchBase
{
    public override string PatchName => nameof(EvecClipboardNotePatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "PushNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, WIVSMNote? __result)
    {
        if (__result == null || note == null)
            return;

        try
        {
            EvecService.CloneState(note, __result);
        }
        catch
        {
            // Native clipboard copy must survive a sidecar/cache failure.
        }
    }
}

public sealed class EvecClipboardPartPatch : PatchBase
{
    public override string PatchName => nameof(EvecClipboardPartPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => nameof(WIVSMClipboard.PushMidiPart);
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart midiPart, WIVSMMidiPart? __result)
    {
        try
        {
            EvecService.ClonePartStates(midiPart, __result);
        }
        catch
        {
            // Native clipboard copying must survive optional sidecar copying.
        }
    }
}

public sealed class EvecClipboardClearNotePatch : PatchBase
{
    public override string PatchName => nameof(EvecClipboardClearNotePatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => nameof(WIVSMClipboard.ClearNote);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(WIVSMClipboard __instance, out IntPtr[] __state)
    {
        __state = EvecService.CaptureClipboardNoteHandles(
            __instance,
            includeNotes: true,
            includeMidiParts: false);
    }

    [HarmonyPostfix]
    private static void Postfix(IntPtr[] __state) => EvecService.ReleaseHandles(__state);
}

public sealed class EvecClipboardClearPartPatch : PatchBase
{
    public override string PatchName => nameof(EvecClipboardClearPartPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => nameof(WIVSMClipboard.ClearMidiPart);
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(WIVSMClipboard __instance, out IntPtr[] __state)
    {
        __state = EvecService.CaptureClipboardNoteHandles(
            __instance,
            includeNotes: false,
            includeMidiParts: true);
    }

    [HarmonyPostfix]
    private static void Postfix(IntPtr[] __state) => EvecService.ReleaseHandles(__state);
}

public sealed class EvecClipboardPropertyPatch : PatchBase
{
    public override string PatchName => nameof(EvecClipboardPropertyPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => nameof(WIVSMClipboard.CopyNotePropertyTo);
    public override Type[] ArgumentTypes =>
        new[] { typeof(IEnumerable<WIVSMNote>), typeof(NoteProperty) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMClipboard __instance,
        ref IEnumerable<WIVSMNote> notes,
        NoteProperty property,
        out EvecService.ClipboardPropertyTransfer? __state)
    {
        __state = null;
        if (!Settings.EvecEnabled || notes == null ||
            !property.HasFlag(NoteProperty.LyricsAndPhonemes))
        {
            return;
        }

        // CopyNotePropertyTo enumerates the targets after this prefix. Reuse
        // one materialized list so single-use enumerables cannot be consumed
        // by the logical-state capture before native code sees them.
        try
        {
            var targets = notes.Where(note => note != null).ToList();
            notes = targets;
            __state = EvecService.PrepareClipboardPropertyTransfer(__instance, targets);
        }
        catch
        {
            // Fall back to the native property copy if state capture fails.
            __state = null;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        ref bool __result,
        EvecService.ClipboardPropertyTransfer? __state)
    {
        if (!__result || __state == null)
            return;

        try
        {
            // Returning false makes the caller's existing Transaction roll
            // back both the native property copy and any partial EVEC writes.
            __result = EvecService.CompleteClipboardPropertyTransfer(__state);
        }
        catch
        {
            __result = false;
        }
    }
}

public sealed class EvecClipboardPartPropertyPatch : PatchBase
{
    public override string PatchName => nameof(EvecClipboardPartPropertyPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => nameof(WIVSMClipboard.CopyPartPropertyTo);
    public override Type[] ArgumentTypes =>
        new[] { typeof(IEnumerable<WIVSMPart>), typeof(PartProperty) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMClipboard __instance,
        ref IEnumerable<WIVSMPart> parts,
        PartProperty property,
        out EvecService.ClipboardPartPropertyTransfer? __state)
    {
        __state = null;
        if (!Settings.EvecEnabled || parts == null ||
            !property.HasFlag(PartProperty.Note) &&
            !property.HasFlag(PartProperty.VoiceBank))
        {
            return;
        }

        try
        {
            var targets = parts.Where(part => part != null).ToList();
            parts = targets;
            __state = EvecService.PrepareClipboardPartPropertyTransfer(
                __instance,
                targets,
                property);
        }
        catch
        {
            // Fall back to native part-property copying.
            __state = null;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        ref bool __result,
        EvecService.ClipboardPartPropertyTransfer? __state)
    {
        if (!__result || __state == null)
            return;

        try
        {
            if (!EvecService.CompleteClipboardPartPropertyTransfer(__state))
                __result = false;
        }
        catch
        {
            // Returning false asks the caller's existing Transaction to roll
            // back both the native property copy and our EVEC normalization.
            __result = false;
        }
    }
}

public sealed class EvecRemoveNotePatch : PatchBase
{
    public override string PatchName => nameof(EvecRemoveNotePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "RemoveNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };

    [HarmonyPostfix]
    private static void Postfix(bool __result, WIVSMNote note)
    {
        if (__result && note != null)
            EvecService.RemoveNote(note);
    }
}

public sealed class EvecSetLyricsPatch : PatchBase
{
    public override string PatchName => nameof(EvecSetLyricsPatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "SetLyrics";
    public override Type[] ArgumentTypes =>
        new[] { typeof(WIVSMNote), typeof(string), typeof(int), typeof(bool), typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMNote note, out EvecLyricsEditState? __state)
    {
        __state = null;
        if (!Settings.EvecEnabled || note == null || !note.IsProtected)
            return;

        try
        {
            var cur = EvecService.GetState(note);
            if (cur.HasAnyEvec)
            {
                __state = new EvecLyricsEditState(cur.Clone(), note.IsProtected);

                // IsProtected normally means “keep manually authored
                // phonemes”. EVEC also needs it to protect its suffixes, but
                // that must not prevent an intentional lyric edit from
                // generating a new base pronunciation. Match V6's own manual
                // phoneme editor: unlock for G2PA, then reapply EVEC below.
                note.IsProtected = false;
            }
        }
        catch { }
    }

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMNote note,
        (bool IsSuccess, WIVSMNote? NextNote) __result,
        EvecLyricsEditState? __state)
    {
        if (!Settings.EvecEnabled || note == null || __state == null)
            return;

        if (!__result.IsSuccess)
        {
            note.IsProtected = __state.WasProtected;
            return;
        }

        try
        {
            // This runs inside the editor's existing lyric transaction. Use
            // the same direct, verified isValid=true write as normal EVEC
            // edits instead of sending EVEC suffixes through G2PA.
            EvecService.SetNoteEvec(note, __state.State, commit: false);
        }
        catch (Exception ex)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_Evec_LyricsFailed", ex.Message));
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        WIVSMNote note,
        EvecLyricsEditState? __state,
        Exception? __exception)
    {
        if (__exception != null && note != null && __state != null)
        {
            try { note.IsProtected = __state.WasProtected; }
            catch { }
        }

        return __exception;
    }

    private sealed record EvecLyricsEditState(EvecNoteState State, bool WasProtected);
}

public sealed class EvecNoteRenderBadgePatch : PatchBase
{
    public override string PatchName => nameof(EvecNoteRenderBadgePatch);
    public override Type TargetClass => typeof(UINote);
    public override string TargetMethodName => "OnRender";
    public override Type[] ArgumentTypes => new[] { typeof(DrawingContext) };

    [HarmonyPostfix]
    private static void Postfix(UINote __instance, DrawingContext drawingContext)
    {
        if (drawingContext == null || !Settings.EvecEnabled)
            return;

        try
        {
            EvecBadgeRenderer.RenderBadge(__instance, drawingContext);
        }
        catch
        {
            // Defensive: never let badge rendering crash the UI
        }
    }
}

public sealed class EvecPianorollContextMenuPatch : PatchBase
{
    private const string MenuTag = "VOCALOIDPatcher_Evec_ContextMenu";

    public override string PatchName => nameof(EvecPianorollContextMenuPatch);
    public override Type TargetClass => typeof(PianorollView);
    public override string TargetMethodName => "OnContextMenuOpened";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RoutedEventArgs) };

    [HarmonyPostfix]
    private static void Postfix(PianorollView __instance, object sender)
    {
        if (!Settings.EvecEnabled || sender is not ContextMenu contextMenu)
            return;

        try
        {
            // Remove existing EVEC menu item if any
            for (int i = contextMenu.Items.Count - 1; i >= 0; i--)
            {
                if (contextMenu.Items[i] is FrameworkElement fe && Equals(fe.Tag, MenuTag))
                    contextMenu.Items.RemoveAt(i);
            }

            var musicalEditorVm = __instance.DataContext as MusicalEditorViewModel
                ?? (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
            var activeTrack = musicalEditorVm?.ActiveTrack;
            if (activeTrack == null || activeTrack.Type != VSMTrackType.Midi)
                return;

            var selectedNotes = activeTrack.SelectedNotes;
            if (selectedNotes == null || selectedNotes.Count == 0)
                return;

            var activePart = musicalEditorVm?.ActivePart;
            var voiceBank = activePart?.VoiceBank();
            if (voiceBank == null)
                return;

            var caps = EvecVoicebankDetector.GetCapabilities(voiceBank);
            if (!caps.IsSupported)
                return;

            var states = selectedNotes.Select(EvecService.GetState).ToList();
            var currentState = states[0];
            bool uniformColor = states.All(item => item.VoiceColorId == currentState.VoiceColorId);
            bool uniformAttack = states.All(item => item.AttackId == currentState.AttackId);
            bool uniformExtension = states.All(item =>
                item.ConsonantExtension == currentState.ConsonantExtension);
            bool uniformRelease = states.All(item => item.ReleaseId == currentState.ReleaseId);
            int maximumExtension = selectedNotes
                .Select(note => caps.MaximumSelectableConsonantExtension(note.Phonemes))
                .DefaultIfEmpty(EvecConstants.MinConsonantExtension)
                .Min();

            // Create EVEC top-level menu item
            var evecMenuItem = new MenuItem
            {
                Header = TranslationManager.Tr("VOCALOIDPatcher_Evec_Menu"),
                Tag = MenuTag
            };
            WpfTranslationPatch.MarkUntranslatable(evecMenuItem);

            // 1. Voice Color submenu
            if (caps.HasColors)
            {
                var colorSub = new MenuItem { Header = TranslationManager.Tr("VOCALOIDPatcher_Evec_VoiceColor") };
                WpfTranslationPatch.MarkUntranslatable(colorSub);
                foreach (var opt in caps.Colors)
                {
                    var optCaptured = opt;
                    var item = new MenuItem
                    {
                        Header = TranslationManager.Tr(opt.DisplayKey) + (string.IsNullOrEmpty(opt.Suffix) ? "" : $" ({opt.Suffix})"),
                        IsCheckable = true,
                        IsChecked = uniformColor && currentState.VoiceColorId == opt.Id
                    };
                    WpfTranslationPatch.MarkUntranslatable(item);
                    item.Click += (_, _) => EvecService.UpdateVoiceColor(selectedNotes, optCaptured.Id);
                    colorSub.Items.Add(item);
                }
                evecMenuItem.Items.Add(colorSub);
            }

            // 2. CTop recording character submenu
            if (caps.HasAttacks)
            {
                var attackSub = new MenuItem { Header = TranslationManager.Tr("VOCALOIDPatcher_Evec_Attack") };
                WpfTranslationPatch.MarkUntranslatable(attackSub);
                foreach (var opt in caps.Attacks)
                {
                    var optCaptured = opt;
                    var item = new MenuItem
                    {
                        Header = TranslationManager.Tr(opt.DisplayKey) + (string.IsNullOrEmpty(opt.Suffix) ? "" : $" ({opt.Suffix})"),
                        IsCheckable = true,
                        IsChecked = uniformAttack && currentState.AttackId == opt.Id
                    };
                    WpfTranslationPatch.MarkUntranslatable(item);
                    item.Click += (_, _) => EvecService.UpdateAttack(selectedNotes, optCaptured.Id);
                    attackSub.Items.Add(item);
                }
                evecMenuItem.Items.Add(attackSub);
            }

            // 3. Independent pronunciation-extension count.
            if (caps.HasConsonantExtension)
            {
                var extensionSub = new MenuItem
                {
                    Header = TranslationManager.Tr("VOCALOIDPatcher_Evec_ConsonantExtension")
                };
                WpfTranslationPatch.MarkUntranslatable(extensionSub);
                for (int value = EvecConstants.MinConsonantExtension;
                     value <= EvecConstants.MaxConsonantExtension;
                     value++)
                {
                    int captured = value;
                    string key = value == 0
                        ? "VOCALOIDPatcher_Evec_Extension_None"
                        : "VOCALOIDPatcher_Evec_Extension_Count";
                    string header = TranslationManager.Tr(key);
                    if (value > 0)
                        header = header.Replace("{0}", value.ToString(), StringComparison.Ordinal);
                    var item = new MenuItem
                    {
                        Header = header,
                        IsCheckable = true,
                        IsChecked = uniformExtension && currentState.ConsonantExtension == value,
                        IsEnabled = value <= maximumExtension
                    };
                    WpfTranslationPatch.MarkUntranslatable(item);
                    item.Click += (_, _) =>
                        EvecService.UpdateConsonantExtension(selectedNotes, captured);
                    extensionSub.Items.Add(item);
                }
                evecMenuItem.Items.Add(extensionSub);
            }

            // 4. Voice Release submenu
            if (caps.HasReleases)
            {
                var releaseSub = new MenuItem { Header = TranslationManager.Tr("VOCALOIDPatcher_Evec_Release") };
                WpfTranslationPatch.MarkUntranslatable(releaseSub);
                foreach (var opt in caps.Releases)
                {
                    var optCaptured = opt;
                    var item = new MenuItem
                    {
                        Header = TranslationManager.Tr(opt.DisplayKey) + (string.IsNullOrEmpty(opt.Suffix) ? "" : $" ({opt.Suffix})"),
                        IsCheckable = true,
                        IsChecked = uniformRelease && currentState.ReleaseId == opt.Id
                    };
                    WpfTranslationPatch.MarkUntranslatable(item);
                    item.Click += (_, _) => EvecService.UpdateRelease(selectedNotes, optCaptured.Id);
                    releaseSub.Items.Add(item);
                }
                evecMenuItem.Items.Add(releaseSub);
            }

            // Separator & Reset
            evecMenuItem.Items.Add(new Separator());
            var resetItem = new MenuItem { Header = TranslationManager.Tr("VOCALOIDPatcher_Evec_Reset") };
            WpfTranslationPatch.MarkUntranslatable(resetItem);
            resetItem.Click += (_, _) => EvecService.ResetNotes(selectedNotes);
            evecMenuItem.Items.Add(resetItem);

            contextMenu.Items.Add(new Separator { Tag = MenuTag });
            contextMenu.Items.Add(evecMenuItem);
        }
        catch
        {
            // Defensive
        }
    }
}

public sealed class EvecNoteInspectorLoadedPatch : PatchBase
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NoteInspector, EvecInspectorView> Views = new();

    public override string PatchName => nameof(EvecNoteInspectorLoadedPatch);
    public override Type TargetClass => typeof(NoteInspector);
    public override string TargetMethodName => "OnLoaded";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RoutedEventArgs) };

    [HarmonyPostfix]
    private static void Postfix(NoteInspector __instance)
    {
        EnsureInjected(__instance);
    }

    internal static EvecInspectorView? EnsureInjected(NoteInspector inspector)
    {
        if (!Settings.EvecEnabled)
            return null;

        if (Views.TryGetValue(inspector, out var existing))
            return existing;

        try
        {
            var attackReleaseView = AccessTools.Field(typeof(NoteInspector), "xAttackReleaseEffectView")?.GetValue(inspector) as UserControl
                ?? inspector.FindName("xAttackReleaseEffectView") as UserControl;
            if (attackReleaseView != null)
            {
                var lyricStackPanel = AccessTools.Field(attackReleaseView.GetType(), "xLyricStackPanel")?.GetValue(attackReleaseView) as FrameworkElement
                    ?? attackReleaseView.FindName("xLyricStackPanel") as FrameworkElement;

                if (lyricStackPanel?.Parent is Panel parentPanel)
                {
                    var view = new EvecInspectorView();
                    int lyricIndex = parentPanel.Children.IndexOf(lyricStackPanel);
                    if (lyricIndex >= 0)
                        parentPanel.Children.Insert(lyricIndex + 1, view);
                    else
                        parentPanel.Children.Add(view);

                    Views.Add(inspector, view);
                    return view;
                }
                else if (attackReleaseView.Content is Panel rootPanel)
                {
                    var view = new EvecInspectorView();
                    rootPanel.Children.Insert(Math.Min(1, rootPanel.Children.Count), view);
                    Views.Add(inspector, view);
                    return view;
                }
            }
        }
        catch
        {
            // Defensive
        }

        return null;
    }
}

public sealed class EvecNoteInspectorUpdateViewsPatch : PatchBase
{
    public override string PatchName => nameof(EvecNoteInspectorUpdateViewsPatch);
    public override Type TargetClass => typeof(NoteInspector);
    public override string TargetMethodName => "UpdateViews";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(NoteInspector __instance)
    {
        if (!Settings.EvecEnabled)
            return;

        try
        {
            var view = EvecNoteInspectorLoadedPatch.EnsureInjected(__instance);
            view?.UpdateView(GetMusicalEditorVM());
        }
        catch
        {
            // Defensive
        }
    }

    private static MusicalEditorViewModel? GetMusicalEditorVM()
    {
        return (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
    }
}

public sealed class EvecNoteInspectorUpdateLetterPatch : PatchBase
{
    public override string PatchName => nameof(EvecNoteInspectorUpdateLetterPatch);
    public override Type TargetClass => typeof(NoteInspector);
    public override string TargetMethodName => "UpdateLetterPhoneticControl";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(NoteInspector __instance)
    {
        if (!Settings.EvecEnabled)
            return;

        try
        {
            var view = EvecNoteInspectorLoadedPatch.EnsureInjected(__instance);
            view?.UpdateView(GetMusicalEditorVM());
        }
        catch
        {
            // Defensive
        }
    }

    private static MusicalEditorViewModel? GetMusicalEditorVM()
    {
        return (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
    }
}

public sealed class EvecNoteInspectorProtectChangedPatch : PatchBase
{
    public override string PatchName => nameof(EvecNoteInspectorProtectChangedPatch);
    public override Type TargetClass => typeof(NoteInspector);
    public override string TargetMethodName => "UpdateView";
    public override Type[] ArgumentTypes => new[]
    {
        typeof(object),
        typeof(Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag),
        typeof(UpdateObserverNotifyEventArgs),
        typeof(object)
    };

    [HarmonyPostfix]
    private static void Postfix(NoteInspector __instance, Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag typeFlags)
    {
        if (!Settings.EvecEnabled)
            return;

        if (typeFlags == Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag.PhoneticSymbolProtectChanged)
        {
            try
            {
                AccessTools.Method(typeof(NoteInspector), "UpdateLetterPhoneticControl")?.Invoke(__instance, null);
                var view = EvecNoteInspectorLoadedPatch.EnsureInjected(__instance);
                view?.UpdateView(GetMusicalEditorVM());
            }
            catch
            {
                // Defensive
            }
        }
    }

    private static MusicalEditorViewModel? GetMusicalEditorVM()
    {
        return (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
    }
}

public sealed class EvecLetterPhoneticLockPatch : PatchBase
{
    public override string PatchName => nameof(EvecLetterPhoneticLockPatch);
    public override Type TargetClass => typeof(LetterPhoneticControl);
    public override string TargetMethodName => "OnClickPhonemeLock";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RoutedEventArgs) };

    [HarmonyPostfix]
    private static void Postfix(LetterPhoneticControl __instance)
    {
        if (!Settings.EvecEnabled)
            return;

        try
        {
            var vm = (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
            var note = vm?.ActiveTrack?.SelectedNotes?.FirstOrDefault();
            if (note != null)
            {
                EvecService.GetState(note);
            }
            EvecService.Refresh();
        }
        catch
        {
            // Defensive
        }
    }
}

public sealed class EvecPhoneticSymbolTextBoxPatch : PatchBase
{
    public override string PatchName => nameof(EvecPhoneticSymbolTextBoxPatch);
    public override Type TargetClass => typeof(PhoneticSymbolTextBox);
    public override string TargetMethodName => "SetPhonemesToSequence";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote), typeof(string) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note)
    {
        if (!Settings.EvecEnabled || note == null)
            return;

        try
        {
            EvecService.GetState(note);
            EvecService.Refresh();
        }
        catch
        {
            // Defensive
        }
    }
}

public sealed class EvecPhonemeFloatingTextBoxPatch : PatchBase
{
    public override string PatchName => nameof(EvecPhonemeFloatingTextBoxPatch);
    public override Type TargetClass => typeof(PhonemeFloatingTextBox);
    public override string TargetMethodName => "SetPhonemes";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(PhonemeFloatingTextBox __instance)
    {
        if (!Settings.EvecEnabled || __instance.Note == null)
            return;

        try
        {
            EvecService.GetState(__instance.Note);
            EvecService.Refresh();
        }
        catch
        {
            // Defensive
        }
    }
}

public sealed class EvecG2paSetPhonemesPatch : PatchBase
{
    public override string PatchName => nameof(EvecG2paSetPhonemesPatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "SetPhonemes";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote), typeof(string) };

    [HarmonyPostfix]
    private static void Postfix(bool __result, WIVSMNote note)
    {
        if (!Settings.EvecEnabled || !__result || note == null)
            return;

        try
        {
            EvecService.GetState(note);
            EvecService.Refresh();
        }
        catch
        {
            // Defensive
        }
    }
}

public sealed class EvecG2paResetPhonemesNotePatch : PatchBase
{
    public override string PatchName => nameof(EvecG2paResetPhonemesNotePatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "ResetPhonemes";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote), typeof(WIVSMNote) };

    [HarmonyPostfix]
    private static void Postfix(bool __result, WIVSMNote beginNote)
    {
        if (!Settings.EvecEnabled || !__result || beginNote == null)
            return;

        try
        {
            EvecService.GetState(beginNote);
            EvecService.Refresh();
        }
        catch
        {
            // Defensive
        }
    }
}

public sealed class EvecG2paResetPhonemesPartPatch : PatchBase
{
    public override string PatchName => nameof(EvecG2paResetPhonemesPartPatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "ResetPhonemes";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMMidiPart part,
        out EvecService.VoiceBankChange? __state)
    {
        __state = null;
        if (!Settings.EvecEnabled || part == null)
            return;

        try
        {
            __state = EvecService.BeginAutomaticVoiceBankReset(part);
        }
        catch
        {
            // Native G2PA remains authoritative.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        bool __result,
        WIVSMMidiPart part,
        EvecService.VoiceBankChange? __state)
    {
        if (!Settings.EvecEnabled || part == null)
            return;

        try
        {
            if (__state != null)
                EvecService.CompleteAutomaticVoiceBankReset(__state, __result);
            else if (__result)
                EvecService.Refresh();
        }
        catch
        {
            // Defensive
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        EvecService.VoiceBankChange? __state)
    {
        if (__exception != null)
            EvecService.CompleteAutomaticVoiceBankReset(__state, success: false);
        return __exception;
    }
}

public sealed class EvecAutomaticVoiceBankChangePatch : PatchBase
{
    public override string PatchName => nameof(EvecAutomaticVoiceBankChangePatch);
    public override Type TargetClass => typeof(ReplaceVoiceHelper);
    public override string TargetMethodName => nameof(ReplaceVoiceHelper.ReplaceVoice);
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMMidiPart midiPart,
        out EvecService.VoiceBankChange? __state)
    {
        __state = null;
        if (!Settings.EvecEnabled || midiPart == null)
            return;

        try
        {
            // Do not unlock here: both load and import call ResetLyrics before
            // ResetPhonemes. Protection is released only at the G2PA boundary.
            __state = EvecService.PrepareAutomaticVoiceBankChange(midiPart);
        }
        catch
        {
            // Automatic voice replacement must remain usable without EVEC.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        bool __result,
        EvecService.VoiceBankChange? __state)
    {
        try
        {
            EvecService.QueueAutomaticVoiceBankChange(__state, __result);
        }
        catch
        {
            // Native result remains authoritative.
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        EvecService.VoiceBankChange? __state)
    {
        if (__exception != null)
            EvecService.AbortAutomaticVoiceBankChange(__state);
        return __exception;
    }
}

public sealed class EvecVoiceBankChangePatch : PatchBase
{
    public override string PatchName => nameof(EvecVoiceBankChangePatch);
    public override Type TargetClass => typeof(WIVSMMidiPartExtension);
    public override string TargetMethodName => nameof(WIVSMMidiPartExtension.SetVoiceBank);
    public override Type[] ArgumentTypes => new[]
    {
        typeof(WIVSMMidiPart),
        typeof(Yamaha.VOCALOID.VDM.VoiceBank)
    };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMMidiPart part,
        Yamaha.VOCALOID.VDM.VoiceBank voiceBank,
        out EvecService.VoiceBankChange? __state)
    {
        __state = null;
        if (!Settings.EvecEnabled || part == null || voiceBank == null)
            return;

        try
        {
            string currentVoiceBankId = part.IsAi
                ? part.AiVoiceBankID
                : part.VoiceBankID;
            if (string.Equals(
                    currentVoiceBankId,
                    voiceBank.CompID,
                    StringComparison.Ordinal))
            {
                return;
            }

            __state = EvecService.PrepareVoiceBankChange(part);
        }
        catch
        {
            // Voice-bank selection must remain usable if EVEC inspection fails.
        }
    }

    [HarmonyPostfix]
    private static void Postfix(bool __result, EvecService.VoiceBankChange? __state)
    {
        try
        {
            EvecService.CompleteVoiceBankChange(__state, __result);
        }
        catch
        {
            // Native result remains authoritative.
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        EvecService.VoiceBankChange? __state)
    {
        if (__exception != null)
            EvecService.AbortVoiceBankChange(__state);
        return __exception;
    }
}
