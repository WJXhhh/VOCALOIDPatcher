using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HarmonyLib;
using VOCALOIDPatcher.AceParameters;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using UpdateViewTypeFlag = Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag;

namespace VOCALOIDPatcher.Patch.Patches;

// Host-side Harmony patches for the four ACE parameters. The parameters reuse
// the native ParameterView pipeline by mapping their fabricated
// ControlParameterTypeEnum values onto unused-but-valid VSMControllerType
// storage carriers (see AGENTS.md / docs/ace-parameters-integration.md).

public sealed class AceParameterCarrierMapPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterCarrierMapPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "CtrlParamTypeToVSMControllerType";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ControlParameterTypeEnum fromType, ref VSMControllerType __result)
    {
        if (!AceParameterDefinitions.TryGet(fromType, out var definition) || !Settings.AceParameters)
            return true;
        __result = definition.CarrierType;
        return false;
    }
}

public sealed class AceParameterComboInitPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterComboInitPatch);
    public override Type TargetClass => typeof(ParameterHeaderControl);
    public override string TargetMethodName => "UpdateControlParameterComboBox";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiTrack), typeof(bool) };

    // The native initializer ends with SelectedIndex = (int)ControlParameterType.
    // That works for native values by accident, but a fabricated 4CC id is far
    // outside the item range. When a persisted ACE selection is restored on a
    // traditional track we synchronize the list ourselves and skip the native
    // initializer; on any track where the entry does not apply the native
    // fallback (Dynamics/Velocity) must still run.
    [HarmonyPrefix]
    private static bool Prefix(ParameterHeaderControl __instance, WIVSMMidiTrack track, bool isInitialize)
    {
        try
        {
            if (!isInitialize || track == null)
                return true;
            if (__instance.DataContext is not MusicalEditorViewModel vm)
                return true;
            if (!Settings.AceParameters || !AceParameterDefinitions.IsAceType(vm.ControlParameterType))
                return true;

            var fieldName = track.Type == VSMTrackType.MidiAi
                ? "MidiAiControlParameterTypes"
                : "MidiControlParameterTypes";
            var list = AccessTools.Field(typeof(ParameterHeaderControl), fieldName)
                ?.GetValue(__instance) as List<ControlParameterTypeEnum>;
            if (list == null || !list.Contains(vm.ControlParameterType))
                return true;

            CustomParameterUi.SynchronizeHeader(__instance);
            __instance.ControlParameterTypes = list.ToList();
            var combo = AccessTools.Field(typeof(ParameterHeaderControl), "xControlParameterComboBox")
                ?.GetValue(__instance) as FlatComboBox;
            if (combo != null)
                combo.SelectedItem = vm.ControlParameterType;
            return false;
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
            return true;
        }
    }
}

public sealed class AceParameterCommitMappingPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterCommitMappingPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Commit";
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, bool updateHistory)
        => AceParameterMappingService.OnSequenceCommit(__instance, updateHistory);
}

public sealed class AceParameterUndoMappingPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterUndoMappingPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Undo";

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
        => AceParameterMappingService.OnSequenceHistoryRestored(__instance);
}

public sealed class AceParameterRedoMappingPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterRedoMappingPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Redo";

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
        => AceParameterMappingService.OnSequenceHistoryRestored(__instance);
}

public sealed class AceParameterProjectLoadPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterProjectLoadPatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "LoadProjectSequenceFile";
    public override Type[] ArgumentTypes => new[] { typeof(string), typeof(WIVSMSequence).MakeByRefType() };

    [HarmonyPrefix]
    private static void Prefix(string filePath) => AceParameterMappingService.BeginProjectLoad(filePath);

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence? vsmSequence)
    {
        try { AceParameterMappingService.OnProjectOpened(vsmSequence); }
        finally { AceParameterMappingService.EndProjectLoad(); }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception != null)
            AceParameterMappingService.EndProjectLoad();
        return __exception;
    }
}

public sealed class AceParameterProjectOpenCompletePatch : PatchBase
{
    public override string PatchName => nameof(AceParameterProjectOpenCompletePatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "Load";
    public override Type[] ArgumentTypes => new[] { typeof(string) };

    [HarmonyPrefix]
    private static void Prefix(string path) => AceParameterMappingService.BeginProjectLoad(path);

    [HarmonyPostfix]
    private static void Postfix(Yamaha.VOCALOID.Sequence __instance)
    {
        try
        {
            if (__instance.VSMSequence is { IsOpen: true } sequence)
                AceParameterMappingService.OnProjectOpened(sequence);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
        finally
        {
            AceParameterMappingService.EndProjectLoad();
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception != null)
            AceParameterMappingService.EndProjectLoad();
        return __exception;
    }
}

public sealed class AceParameterProjectSavePatch : PatchBase
{
    public override string PatchName => nameof(AceParameterProjectSavePatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "SaveSequence";
    public override Type[] ArgumentTypes => new[]
    {
        typeof(WIVSMSequence), typeof(string), typeof(string), typeof(string),
    };

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMSequence vsmSequence,
        string directoryPath,
        string projectName,
        string extension,
        bool __result)
    {
        if (!__result || !Settings.AceParameters ||
            !extension.StartsWith(".vpr", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            string filePath = Path.Combine(directoryPath, projectName + extension);
            var data = AceParameterMappingService.BuildProjectData(vsmSequence);
            AceParameterProjectArchive.Write(filePath, data);
        }
        catch (Exception e)
        {
            try { vsmSequence.IsModifiedOutsideOfEditHistory = true; } catch { }
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }
}

public sealed class AceParameterSequenceClosePatch : PatchBase
{
    public override string PatchName => nameof(AceParameterSequenceClosePatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Close";

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance)
        => AceParameterMappingService.OnSequenceClosing(__instance);
}

public sealed class AceAcousticRenderStartPatch : PatchBase
{
    public override string PatchName => nameof(AceAcousticRenderStartPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererStarted";
    public override Type[] ArgumentTypes => [typeof(object), typeof(RendererObserverStartEventArgs)];

    [HarmonyPrefix]
    private static void Prefix(RendererObserverStartEventArgs e)
    {
        try { if (e?.MidiPart != null) AceAcousticHost.RenderStarted(e.MidiPart); }
        catch (Exception ex) { Debug.Print("ACE render start: " + ex.GetType().Name); }
    }
}

public sealed class AceAcousticRenderCompletePatch : PatchBase
{
    public override string PatchName => nameof(AceAcousticRenderCompletePatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "OnRendererCompleted";
    public override Type[] ArgumentTypes => [typeof(object), typeof(RendererObserverCompleteEventArgs)];

    [HarmonyPostfix]
    private static void Postfix(RendererObserverCompleteEventArgs e)
    {
        try { if (e?.MidiPart != null) AceAcousticHost.RenderCompleted(e.MidiPart); }
        catch (Exception ex) { Debug.Print("ACE render complete: " + ex.GetType().Name); }
    }
}

public sealed class AceAcousticRenderCancelPatch : PatchBase
{
    public override string PatchName => nameof(AceAcousticRenderCancelPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererCanceled";
    public override Type[] ArgumentTypes => [typeof(object), typeof(RendererObserverCancelEventArgs)];

    [HarmonyPostfix]
    private static void Postfix(RendererObserverCancelEventArgs e)
    {
        try { if (e?.MidiPart != null) AceAcousticHost.RenderCompleted(e.MidiPart); }
        catch (Exception ex) { Debug.Print("ACE render cancellation: " + ex.GetType().Name); }
    }
}

internal static class AceParameterNativeOps
{
    internal static bool IsAnyAceSelected(MusicalEditorViewModel vm)
    {
        var parts = vm.ActiveTrack?.MidiParts;
        if (parts == null)
            return false;
        foreach (var definition in AceParameterDefinitions.All)
        {
            foreach (var part in parts)
            {
                if (part != null && part.HasSelectedBreakPoint(definition.CarrierType))
                    return true;
            }
        }
        return false;
    }

    internal static void RemoveSelected(MusicalEditorViewModel vm)
    {
        var parts = vm.ActiveTrack?.MidiParts;
        if (parts == null)
            return;
        foreach (var definition in AceParameterDefinitions.All)
        {
            foreach (var part in parts)
            {
                if (part == null)
                    continue;
                var controllers = part.SelectedControlParameter(definition.CarrierType);
                for (int index = controllers.Count - 1; index >= 0; index--)
                {
                    var controller = controllers[index];
                    if (controller != null)
                        part.RemoveController(controller);
                }
            }
        }
    }

    internal static void CopySelected(MusicalEditorViewModel vm, WIVSMClipboard clipboard)
    {
        var parts = vm.ActiveTrack?.MidiParts;
        if (parts == null || clipboard == null)
            return;
        foreach (var definition in AceParameterDefinitions.All)
        {
            foreach (var part in parts)
            {
                if (part == null)
                    continue;
                foreach (var controller in part.SelectedControlParameter(definition.CarrierType))
                {
                    if (controller != null)
                        clipboard.PushMidiPartController(definition.CarrierType, controller);
                }
            }
        }
    }

    internal static bool HasAceClipboardControllers(WIVSMClipboard? clipboard)
    {
        if (clipboard == null)
            return false;
        foreach (var definition in AceParameterDefinitions.All)
        {
            if (clipboard.GetNumMidiPartController(definition.CarrierType) > 0)
                return true;
        }
        return false;
    }

    // Controller-only paste (the common "copy a parameter curve" workflow).
    // Mixed note+controller pastes keep the native note behavior and skip the
    // ACE curves in v1.
    internal static bool PasteControllerOnly(MusicalEditorViewModel vm)
    {
        var clipboard = vm.Clipboard;
        if (clipboard == null || clipboard.NumNote > 0 || !HasAceClipboardControllers(clipboard))
            return false;

        var parts = new List<(AceParameterDefinition Definition, List<WIVSMMidiController> Controllers)>();
        long anchor = long.MaxValue;
        foreach (var definition in AceParameterDefinitions.All)
        {
            var controllers = clipboard.GetMidiPartControllers(definition.CarrierType);
            if (controllers.Count == 0)
                continue;
            parts.Add((definition, controllers));
            foreach (var controller in controllers)
                anchor = Math.Min(anchor, controller.AbsPosTick.Value);
        }
        if (parts.Count == 0 || anchor == long.MaxValue)
            return false;

        var part = vm.DeterminePartForCreatingNote(vm.SongPosition, false);
        var sequence = vm.VSMSequence;
        if (part == null || sequence == null)
            return false;

        using (new Yamaha.VOCALOID.Transaction(sequence))
        {
            foreach (var (_, controllers) in parts)
            {
                foreach (var controller in controllers)
                {
                    long relTick = vm.SongPosition.Value + (controller.AbsPosTick.Value - anchor) - part.AbsPosTick.Value;
                    var duplicated = part.DuplicateController(new VSMRelTick(relTick), controller);
                    if (duplicated != null)
                        duplicated.Selected = true;
                }
            }
        }

        if (!part.Equals(vm.ActivePart))
            vm.ChangeActivePart(part);
        vm.DoUpdateView(vm, UpdateViewTypeFlag.AllParameterSelectionChanged);
        return true;
    }

    internal static bool DuplicateSelected(MusicalEditorViewModel vm)
    {
        var mainVm = vm.MainVM;
        var parts = vm.ActiveTrack?.MidiParts;
        if (mainVm == null || parts == null)
            return false;

        long minTick = long.MaxValue;
        long maxTick = long.MinValue;
        foreach (var part in parts)
        {
            if (part == null)
                continue;
            foreach (var note in part.SelectedNotes)
            {
                minTick = Math.Min(minTick, note.AbsPosTick.Value);
                maxTick = Math.Max(maxTick, note.AbsEndTick.Value);
            }
        }
        if (minTick == long.MaxValue)
            return false;

        var offset = mainVm.CalcDuplicateOffset(new VSMAbsTick(minTick), new VSMAbsTick(maxTick));
        foreach (var part in parts)
        {
            if (part == null)
                continue;
            if (part.SelectedNotes.Count == 0)
                continue;
            foreach (var definition in AceParameterDefinitions.All)
            {
                var controllers = part.SelectedControlParameter(definition.CarrierType);
                for (int index = controllers.Count - 1; index >= 0; index--)
                {
                    var controller = controllers[index];
                    if (controller == null)
                        continue;
                    var duplicated = part.DuplicateController(controller.RelPosTick + offset.Value, controller);
                    if (duplicated == null)
                        continue;
                    controller.Selected = false;
                    duplicated.Selected = true;
                }
            }
        }
        return true;
    }

    internal static void ResetSelectedAceDefaults(MusicalEditorViewModel vm)
    {
        var part = vm.ActivePart;
        if (part == null)
            return;
        foreach (var definition in AceParameterDefinitions.All)
        {
            var selected = part.SelectedControlParameter(definition.CarrierType);
            if (selected.Count == 0)
                continue;
            bool first = true;
            for (int index = selected.Count - 1; index >= 0; index--)
            {
                var controller = selected[index];
                if (controller == null)
                    continue;
                if (first)
                {
                    controller.Value = definition.DefaultValue;
                    first = false;
                }
                else
                {
                    part.RemoveController(controller);
                }
            }
        }
    }
}

public sealed class AceParameterHasSelectedBreakPointPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterHasSelectedBreakPointPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "HasSelectedBreakPoint";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(MusicalEditorViewModel __instance, ref bool __result)
    {
        if (__result || !Settings.AceParameters)
            return;
        try
        {
            __result = AceParameterNativeOps.IsAnyAceSelected(__instance);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }
}

public sealed class AceParameterRemoveSelectedPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterRemoveSelectedPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "RemoveSelectedParameter";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(MusicalEditorViewModel __instance)
    {
        if (!Settings.AceParameters)
            return;
        try { AceParameterNativeOps.RemoveSelected(__instance); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message)); }
    }
}

public sealed class AceParameterCopySelectedPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterCopySelectedPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "CopySelectedParameter";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMClipboard) };

    [HarmonyPostfix]
    private static void Postfix(MusicalEditorViewModel __instance, WIVSMClipboard clipboard, bool __result)
    {
        if (!__result || !Settings.AceParameters)
            return;
        try { AceParameterNativeOps.CopySelected(__instance, clipboard); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message)); }
    }
}

public sealed class AceParameterClipboardHasControllerPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterClipboardHasControllerPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "HasCopyMidiController";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMClipboard __instance, ref bool __result)
    {
        if (__result || !Settings.AceParameters)
            return;
        try { __result = AceParameterNativeOps.HasAceClipboardControllers(__instance); }
        catch { }
    }
}

public sealed class AceParameterPastePatch : PatchBase
{
    public override string PatchName => nameof(AceParameterPastePatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "PasteParameter";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static bool Prefix(MusicalEditorViewModel __instance)
    {
        if (!Settings.AceParameters)
            return true;
        try
        {
            // Controller-only clipboard: handle it here and skip the native
            // paste so no untouched native transaction is left behind.
            return !AceParameterNativeOps.PasteControllerOnly(__instance);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
            return true;
        }
    }
}

public sealed class AceParameterDuplicatePatch : PatchBase
{
    public override string PatchName => nameof(AceParameterDuplicatePatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "Duplicate";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPrefix]
    private static void Prefix(MusicalEditorViewModel __instance)
    {
        if (!Settings.AceParameters)
            return;
        try { AceParameterNativeOps.DuplicateSelected(__instance); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message)); }
    }
}

public sealed class AceParameterResetPatch : PatchBase
{
    public override string PatchName => nameof(AceParameterResetPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "ResetControlParametersToDefault";
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(MusicalEditorViewModel __instance)
    {
        if (!Settings.AceParameters)
            return;
        try { AceParameterNativeOps.ResetSelectedAceDefaults(__instance); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message)); }
    }
}

internal static class AceParameterUi
{
    internal static void RefreshSetting()
    {
        void Apply()
        {
            AceAcousticHost.RefreshSettings();
            CustomParameterUi.RefreshHeaders(synchronize: true);
            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm &&
                mainVm.MusicalEditorVM is { } vm)
            {
                if (!Settings.AceParameters && AceParameterDefinitions.IsAceType(vm.ControlParameterType))
                    vm.ControlParameterType = ControlParameterTypeEnum.Dynamics;
                if (vm.ParameterView != null)
                    BreathVolumeUi.UpdateView(vm.ParameterView);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke((Action)Apply);
    }

    internal static void RebuildMapping()
    {
        try
        {
            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm &&
                mainVm.MusicalEditorVM?.VSMSequence is { } sequence)
            {
                AceParameterMappingService.RebuildAll(sequence);
                mainVm.MusicalEditorVM.UpdateViewport();
            }
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }
}
