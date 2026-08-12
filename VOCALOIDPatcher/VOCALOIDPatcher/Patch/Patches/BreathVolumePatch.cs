using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using ParameterUpdateViewTypeFlag = Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag;

namespace VOCALOIDPatcher.Patch.Patches;

internal static class BreathVolumeUi
{
    private static readonly ConditionalWeakTable<ParameterView, BreathVolumeOverlay> Overlays = new();
    private static readonly List<WeakReference<ParameterHeaderControl>> Headers = new();
    private static bool _subscribed;

    public static void Attach(ParameterView view)
    {
        var overlay = BreathVolumeOverlay.Attach(view);
        if (overlay != null)
            Overlays.Add(view, overlay);
    }

    public static void RegisterHeader(ParameterHeaderControl header)
    {
        lock (Headers)
        {
            Headers.Add(new WeakReference<ParameterHeaderControl>(header));
            if (!_subscribed)
            {
                BreathVolumeService.Changed += (_, _) => RefreshHeaders();
                _subscribed = true;
            }
        }
        SynchronizeHeader(header);
    }

    public static void UpdateView(ParameterView view)
    {
        if (!Overlays.TryGetValue(view, out var overlay))
            return;
        if (view.DataContext is MusicalEditorViewModel vm && BreathVolumeService.IsActive(vm.ControlParameterType))
            overlay.Show();
        else
            overlay.Hide();
    }

    public static void MoveSongPosition(ParameterView view)
    {
        if (Overlays.TryGetValue(view, out var overlay))
            overlay.MoveSongPosition();
    }

    public static void RefreshSetting()
    {
        if (!Settings.IndividualBreathVolume)
            BreathVolumeService.DisableAndCleanup();
        else
            BreathVolumeService.InitializeDiagnostics();

        RefreshHeaders(synchronize: true);
        if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm && mainVm.MusicalEditorVM is { } vm)
        {
            if (!Settings.IndividualBreathVolume && vm.ControlParameterType.Equals(BreathVolumeService.ParameterType))
                vm.ControlParameterType = ControlParameterTypeEnum.Dynamics;
            else if (Settings.IndividualBreathVolume && vm.VSMSequence != null)
            {
                BreathVolumeService.RebuildProject(vm.VSMSequence);
            }
            if (vm.ParameterView != null)
                UpdateView(vm.ParameterView);
        }
    }

    public static void SynchronizeHeader(ParameterHeaderControl header)
    {
        var ai = AccessTools.Field(typeof(ParameterHeaderControl), "MidiAiControlParameterTypes")
            ?.GetValue(header) as List<ControlParameterTypeEnum>;
        var standard = AccessTools.Field(typeof(ParameterHeaderControl), "MidiControlParameterTypes")
            ?.GetValue(header) as List<ControlParameterTypeEnum>;
        SynchronizeList(ai);
        SynchronizeList(standard);

        if (header.DataContext is MusicalEditorViewModel vm)
        {
            if (!Settings.IndividualBreathVolume && vm.ControlParameterType.Equals(BreathVolumeService.ParameterType))
                vm.ControlParameterType = ControlParameterTypeEnum.Dynamics;
            var current = vm.ActiveTrack?.Type == VSMTrackType.MidiAi ? ai : standard;
            if (current != null)
                header.ControlParameterTypes = current.ToList();
        }
    }

    public static bool UpdateHeaderValue(ParameterHeaderControl header, ControlParameterTypeEnum type)
    {
        if (!BreathVolumeService.IsActive(type))
            return false;

        var textBox = AccessTools.Field(typeof(ParameterHeaderControl), "xControlParameterValueTextBox")
            ?.GetValue(header) as RegexTextBox;
        var regex = AccessTools.Property(typeof(ParameterHeaderControl), "ControlParameterRegex")
            ?.GetValue(header) as Regex;
        if (textBox != null)
        {
            if (regex != null)
                textBox.Regex = regex;
            textBox.MaxLength = 3;
        }

        var selected = BreathVolumeService.GetSelection().ToArray();
        if (selected.Length == 0)
        {
            header.IsEnabledControlParameterValueTextBox = false;
            header.ControlParameterValue = "-";
            return true;
        }

        var first = BreathVolumeService.GetValue(selected[0]);
        header.IsEnabledControlParameterValueTextBox = true;
        header.ControlParameterValue = selected.Skip(1).Any(handle => BreathVolumeService.GetValue(handle) != first)
            ? "-"
            : first.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    public static bool SetHeaderValue(ParameterHeaderControl header, string text)
    {
        if (header.DataContext is not MusicalEditorViewModel vm ||
            !BreathVolumeService.IsActive(vm.ControlParameterType))
            return false;

        if (vm.VSMSequence == null || vm.ActivePart == null ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            UpdateHeaderValue(header, vm.ControlParameterType);
            return true;
        }

        BreathVolumeService.SetValues(
            vm.VSMSequence,
            vm.ActivePart,
            BreathVolumeService.GetSelection(),
            Math.Clamp(value, BreathVolumeService.MinValue, BreathVolumeService.MaxValue));
        UpdateHeaderValue(header, vm.ControlParameterType);
        return true;
    }

    private static void SynchronizeList(List<ControlParameterTypeEnum>? list)
    {
        if (list == null)
            return;
        list.RemoveAll(item => item.Equals(BreathVolumeService.ParameterType));
        if (Settings.IndividualBreathVolume)
            list.Add(BreathVolumeService.ParameterType);
    }

    private static void RefreshHeaders(bool synchronize = false)
    {
        void Refresh()
        {
            lock (Headers)
            {
                for (var index = Headers.Count - 1; index >= 0; index--)
                {
                    if (!Headers[index].TryGetTarget(out var header))
                    {
                        Headers.RemoveAt(index);
                        continue;
                    }
                    if (synchronize)
                        SynchronizeHeader(header);
                    if (header.DataContext is MusicalEditorViewModel vm)
                        UpdateHeaderValue(header, vm.ControlParameterType);
                }
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Refresh();
        else
            dispatcher.BeginInvoke((Action)Refresh);
    }
}

public sealed class BreathVolumeParameterHeaderConstructorPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeParameterHeaderConstructorPatch);
    public override Type TargetClass => typeof(ParameterHeaderControl);
    public override string TargetMethodName => ".ctor";
    public override bool IsConstructor => true;

    [HarmonyPostfix]
    private static void Postfix(ParameterHeaderControl __instance)
    {
        try { BreathVolumeUi.RegisterHeader(__instance); }
        catch (Exception e) { LogFailure(e); }
    }

    private static void LogFailure(Exception e)
        => Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message));
}

public sealed class BreathVolumeParameterComboPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeParameterComboPatch);
    public override Type TargetClass => typeof(ParameterHeaderControl);
    public override string TargetMethodName => "UpdateControlParameterComboBox";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiTrack), typeof(bool) };

    [HarmonyPostfix]
    private static void Postfix(ParameterHeaderControl __instance)
    {
        try { BreathVolumeUi.SynchronizeHeader(__instance); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message)); }
    }
}

public sealed class BreathVolumeParameterNamePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeParameterNamePatch);
    public override Type TargetClass => typeof(ControlParameterTypeEnumToStringConverter);
    public override string TargetMethodName => "Convert";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(Type), typeof(object), typeof(CultureInfo) };

    [HarmonyPrefix]
    private static bool Prefix(object value, ref object __result)
    {
        if (value is not ControlParameterTypeEnum type || !type.Equals(BreathVolumeService.ParameterType))
            return true;
        __result = TranslationManager.Tr("VOCALOIDPatcher_BreathVolume_Name");
        return false;
    }
}

public sealed class BreathVolumeParameterShortNamePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeParameterShortNamePatch);
    public override Type TargetClass => typeof(ControlParameterTypeEnumShortToStringConverter);
    public override string TargetMethodName => "Convert";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(Type), typeof(object), typeof(CultureInfo) };

    [HarmonyPrefix]
    private static bool Prefix(object value, ref object __result)
    {
        if (value is not ControlParameterTypeEnum type || !type.Equals(BreathVolumeService.ParameterType))
            return true;
        __result = "BVL";
        return false;
    }
}

public sealed class BreathVolumeHeaderValuePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeHeaderValuePatch);
    public override Type TargetClass => typeof(ParameterHeaderControl);
    public override string TargetMethodName => "UpdateControlParameterIndicatedValue";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ParameterHeaderControl __instance, ControlParameterTypeEnum type)
        => !BreathVolumeUi.UpdateHeaderValue(__instance, type);
}

public sealed class BreathVolumeHeaderInputPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeHeaderInputPatch);
    public override Type TargetClass => typeof(ParameterHeaderControl);
    public override string TargetMethodName => "SetControlParameterValue";
    public override Type[] ArgumentTypes => new[] { typeof(string) };

    [HarmonyPrefix]
    private static bool Prefix(ParameterHeaderControl __instance, string valueText)
        => !BreathVolumeUi.SetHeaderValue(__instance, valueText);
}

public sealed class BreathVolumeParameterViewConstructorPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeParameterViewConstructorPatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => ".ctor";
    public override bool IsConstructor => true;

    [HarmonyPostfix]
    private static void Postfix(ParameterView __instance)
    {
        try { BreathVolumeUi.Attach(__instance); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message)); }
    }
}

public sealed class BreathVolumeParameterViewUpdatePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeParameterViewUpdatePatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => "UpdateView";
    public override Type[] ArgumentTypes => new[]
    {
        typeof(object), typeof(ParameterUpdateViewTypeFlag), typeof(UpdateObserverNotifyEventArgs), typeof(object)
    };

    [HarmonyPrefix]
    private static bool Prefix(ParameterView __instance, ParameterUpdateViewTypeFlag typeFlags, object? addition)
    {
        try
        {
            if (__instance.DataContext is not MusicalEditorViewModel vm)
                return true;

            var selectingBvl = addition is ControlParameterTypeEnum type && type.Equals(BreathVolumeService.ParameterType);
            if (!selectingBvl && !BreathVolumeService.IsActive(vm.ControlParameterType))
            {
                BreathVolumeUi.UpdateView(__instance);
                return true;
            }

            if (selectingBvl || typeFlags is ParameterUpdateViewTypeFlag.ActivePartChanged or ParameterUpdateViewTypeFlag.ActiveTrackChanged)
                BreathVolumeService.ClearSelection();
            if (selectingBvl || ShouldRefreshOverlay(typeFlags))
                BreathVolumeUi.UpdateView(__instance);
            return typeFlags is ParameterUpdateViewTypeFlag.SongPositionChanged or ParameterUpdateViewTypeFlag.ShowMusicalEditor;
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message));
            return true;
        }
    }

    private static bool ShouldRefreshOverlay(ParameterUpdateViewTypeFlag typeFlags)
        => typeFlags is ParameterUpdateViewTypeFlag.SequenceChanged
            or ParameterUpdateViewTypeFlag.ActiveTrackChanged
            or ParameterUpdateViewTypeFlag.ActivePartChanged
            or ParameterUpdateViewTypeFlag.ShowMusicalEditor
            or ParameterUpdateViewTypeFlag.HorizontalZoomed
            or ParameterUpdateViewTypeFlag.VerticalZoomed
            or ParameterUpdateViewTypeFlag.Scrolled
            or ParameterUpdateViewTypeFlag.DisplayControlParameterChanged
            or ParameterUpdateViewTypeFlag.ControlParameterAreaSizeChanged
            or ParameterUpdateViewTypeFlag.QuantizeChanged
            or ParameterUpdateViewTypeFlag.OpenParameterView
            or ParameterUpdateViewTypeFlag.TimeSigSectionInfosChanged
            or ParameterUpdateViewTypeFlag.TempoSectionInfosChanged
            or ParameterUpdateViewTypeFlag.MeasureOffsetChanged;
}

public sealed class BreathVolumeSongPositionPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeSongPositionPatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => "SongPositionPropertyChanged";

    [HarmonyPostfix]
    private static void Postfix(ParameterView __instance)
    {
        try { BreathVolumeUi.MoveSongPosition(__instance); }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message));
        }
    }
}

public sealed class BreathVolumeMinimumPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeMinimumPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "GetMinControllerValue";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ControlParameterTypeEnum type, ref int __result)
    {
        if (!type.Equals(BreathVolumeService.ParameterType))
            return true;
        __result = BreathVolumeService.MinValue;
        return false;
    }
}

public sealed class BreathVolumeMaximumPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeMaximumPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "GetMaxControllerValue";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ControlParameterTypeEnum type, ref int __result)
    {
        if (!type.Equals(BreathVolumeService.ParameterType))
            return true;
        __result = BreathVolumeService.MaxValue;
        return false;
    }
}

public sealed class BreathVolumeDefaultPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeDefaultPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "GetDefaultControllerValue";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ControlParameterTypeEnum type, ref int __result)
    {
        if (!type.Equals(BreathVolumeService.ParameterType))
            return true;
        __result = BreathVolumeService.DefaultValue;
        return false;
    }
}

public sealed class BreathVolumeRemoveNativeParameterPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRemoveNativeParameterPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "RemoveControlParameter";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart), typeof(VSMRelTick), typeof(VSMRelTick), typeof(bool) };

    [HarmonyPrefix]
    private static bool Prefix(MusicalEditorViewModel __instance, ref bool __result)
    {
        if (!BreathVolumeService.IsActive(__instance.ControlParameterType))
            return true;
        __result = true;
        return false;
    }
}

public sealed class BreathVolumeV2CompatibilityPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeV2CompatibilityPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "GetControllerValueForV2Compatibility";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart), typeof(VSMRelTick), typeof(VSMRelTick) };

    [HarmonyPrefix]
    private static bool Prefix(MusicalEditorViewModel __instance, ref int? __result)
    {
        if (!BreathVolumeService.IsActive(__instance.ControlParameterType))
            return true;
        __result = null;
        return false;
    }
}

public sealed class BreathVolumeWavePathPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeWavePathPatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_WaveFilePath";

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref string __result)
    {
        try { __result = BreathVolumeService.SubstituteWavePath(__instance, __result); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", e.Message)); }
    }
}

public sealed class BreathVolumeRendererBlockPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRendererBlockPatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "OnRendererBlockRendered";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverBlockRenderingEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(RendererObserverBlockRenderingEventArgs e)
    {
        try
        {
            var sequence = App.Shared?.Document?.Sequence?.VSMSequence;
            if (sequence == null || e?.MidiPart == null)
                return;
            BreathVolumeService.ProcessRenderedBlock(
                sequence, e.MidiPart, e.AudioBufferList, e.ScoreList,
                Math.Max(e.Progress.FirstEnd, e.Progress.SecondEnd));
        }
        catch (Exception exception)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_BufferFailed", exception.Message));
        }
    }
}

public sealed class BreathVolumeRendererStartPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRendererStartPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererStarted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverStartEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(MusicalEditorViewModel __instance, RendererObserverStartEventArgs e)
    {
        try
        {
            if (__instance.VSMSequence != null && e?.MidiPart != null)
                BreathVolumeService.StartRender(__instance.VSMSequence, e.MidiPart);
        }
        catch (Exception exception)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", exception.Message));
        }
    }
}

public sealed class BreathVolumeRendererCancelPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRendererCancelPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererCanceled";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCancelEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(RendererObserverCancelEventArgs e)
    {
        if (e?.MidiPart != null)
            BreathVolumeService.CancelRender(e.MidiPart);
    }
}

public sealed class BreathVolumeRendererCompletePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRendererCompletePatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "OnRendererCompleted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCompleteEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(RendererObserverCompleteEventArgs e)
    {
        try
        {
            var sequence = App.Shared?.Document?.Sequence?.VSMSequence;
            if (sequence != null && e?.MidiPart != null)
                BreathVolumeService.CompleteRender(sequence, e.MidiPart);
        }
        catch (Exception exception)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", exception.Message));
        }
    }
}

public sealed class BreathVolumeProjectLoadPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeProjectLoadPatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "LoadProjectSequenceFile";
    public override Type[] ArgumentTypes => new[] { typeof(string), typeof(WIVSMSequence).MakeByRefType() };

    [HarmonyPrefix]
    private static void Prefix(string filePath, ref BreathProjectData? __state)
    {
        __state = null;
        if (!File.Exists(filePath) ||
            !(filePath.EndsWith(".vpr", StringComparison.OrdinalIgnoreCase) ||
              filePath.EndsWith(".vpr.bak", StringComparison.OrdinalIgnoreCase)))
            return;
        try { __state = BreathVolumeService.ReadProjectData(filePath); }
        catch (InvalidDataException) { }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_LoadFailed", e.Message)); }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence? vsmSequence, BreathProjectData? __state)
    {
        if (vsmSequence == null)
            return;
        try { BreathVolumeService.LoadProjectData(vsmSequence, __state); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_LoadFailed", e.Message)); }
    }
}

public sealed class BreathVolumeProjectOpenCompletePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeProjectOpenCompletePatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "Load";
    public override Type[] ArgumentTypes => new[] { typeof(string) };

    [HarmonyPostfix]
    private static void Postfix(Yamaha.VOCALOID.Sequence __instance)
    {
        try
        {
            if (__instance.VSMSequence is { IsOpen: true } sequence)
                BreathVolumeService.RebuildProject(sequence);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", e.Message));
        }
    }
}

public sealed class BreathVolumeProjectSavePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeProjectSavePatch);
    public override Type TargetClass => typeof(Yamaha.VOCALOID.Sequence);
    public override string TargetMethodName => "SaveSequence";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMSequence), typeof(string), typeof(string), typeof(string) };

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMSequence vsmSequence,
        string directoryPath,
        string projectName,
        string extension,
        bool __result)
    {
        if (!__result || !extension.StartsWith(".vpr", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var data = BreathVolumeService.BuildProjectData(vsmSequence);
            if (!BreathVolumeService.RequiresProjectDataWrite(vsmSequence, data))
                return;
            BreathVolumeService.WriteProjectData(
                Path.Combine(directoryPath, projectName + extension), vsmSequence, data);
        }
        catch (Exception e)
        {
            BreathVolumeService.MarkSaveFailed(vsmSequence, e);
        }
    }
}

public sealed class BreathVolumeDuplicateNotePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeDuplicateNotePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "DuplicateNote";
    public override Type[] ArgumentTypes => new[] { typeof(VSMRelTick), typeof(WIVSMNote) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, WIVSMNote? __result)
        => BreathVolumeService.CopyNoteValue(note, __result);
}

public sealed class BreathVolumeDuplicatePartPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeDuplicatePartPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => "DuplicatePart";
    public override Type[] ArgumentTypes => new[] { typeof(VSMAbsTick), typeof(WIVSMMidiPart) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart midiPart, WIVSMMidiPart? __result)
    {
        BreathVolumeService.CopyPartValues(midiPart, __result);
        var sequence = App.Shared?.Document?.Sequence?.VSMSequence;
        if (sequence != null && __result != null && __result.HasValidRenderedWave)
            BreathVolumeService.RefreshRegionsAsync(sequence, __result, rebuildAfterRefresh: true);
    }
}

public sealed class BreathVolumeDuplicateTrackPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeDuplicateTrackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "DuplicateTrack";
    public override Type[] ArgumentTypes => new[]
    {
        typeof(ulong), typeof(WIVSMTrack), typeof(string).MakeByRefType()
    };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, WIVSMTrack track, WIVSMTrack? __result)
    {
        BreathVolumeService.CopyTrackValues(track, __result);
        BreathVolumeService.RebuildTrack(__instance, __result);
    }
}

public sealed class BreathVolumeDuplicateSequencePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeDuplicateSequencePatch);
    public override Type TargetClass => typeof(WIVSMSequenceManager);
    public override string TargetMethodName => "DuplicateSequence";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMSequence), typeof(VSMSequenceData) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence vsmSequence, WIVSMSequence? __result)
        => BreathVolumeService.CopySequenceValues(vsmSequence, __result);
}

public sealed class BreathVolumeClipboardNotePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeClipboardNotePatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "PushNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, WIVSMNote? __result)
        => BreathVolumeService.CopyClipboardNoteValue(note, __result);
}

public sealed class BreathVolumeClipboardPartPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeClipboardPartPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "PushMidiPart";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart midiPart, WIVSMMidiPart? __result)
        => BreathVolumeService.CopyClipboardPartValues(midiPart, __result);
}

public sealed class BreathVolumeClipboardClearNotePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeClipboardClearNotePatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "ClearNote";

    [HarmonyPrefix]
    private static void Prefix(WIVSMClipboard __instance, out IntPtr[] __state)
        => __state = BreathVolumeService.CaptureClipboardNoteHandles(
            __instance, includeNotes: true, includeMidiParts: false);

    [HarmonyPostfix]
    private static void Postfix(IntPtr[] __state)
        => BreathVolumeService.ReleaseNoteHandles(__state);
}

public sealed class BreathVolumeClipboardClearPartPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeClipboardClearPartPatch);
    public override Type TargetClass => typeof(WIVSMClipboard);
    public override string TargetMethodName => "ClearMidiPart";

    [HarmonyPrefix]
    private static void Prefix(WIVSMClipboard __instance, out IntPtr[] __state)
        => __state = BreathVolumeService.CaptureClipboardNoteHandles(
            __instance, includeNotes: false, includeMidiParts: true);

    [HarmonyPostfix]
    private static void Postfix(IntPtr[] __state)
        => BreathVolumeService.ReleaseNoteHandles(__state);
}

public sealed class BreathVolumeRemoveNotePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRemoveNotePatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "RemoveNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMNote note, out IntPtr __state)
        => __state = note?.CppObjPtr ?? IntPtr.Zero;

    [HarmonyPostfix]
    private static void Postfix(bool __result, IntPtr __state)
    {
        if (__result)
            BreathVolumeService.ReleaseNoteHandles(new[] { __state });
    }
}

public sealed class BreathVolumeG2paDeleteNotePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeG2paDeleteNotePatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "DeleteNote";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMNote note, out IntPtr __state)
        => __state = note?.CppObjPtr ?? IntPtr.Zero;

    [HarmonyPostfix]
    private static void Postfix(bool __result, IntPtr __state)
    {
        if (__result)
            BreathVolumeService.ReleaseNoteHandles(new[] { __state });
    }
}

public sealed class BreathVolumeJoinNotesPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeJoinNotesPatch);
    public override Type TargetClass => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "JoinNotes";
    public override Type[] ArgumentTypes => new[] { typeof(List<WIVSMNote>) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMMidiPart __instance, out IntPtr[] __state)
        => __state = BreathVolumeService.CapturePartNoteHandles(__instance);

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, bool __result, IntPtr[] __state)
    {
        if (__result)
            BreathVolumeService.ReleaseMissingPartNotes(__instance, __state);
    }
}

public sealed class BreathVolumeRemovePartPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRemovePartPatch);
    public override Type TargetClass => typeof(WIVSMMidiTrack);
    public override string TargetMethodName => "RemovePart";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMMidiPart midiPart, out BreathNativeObjectHandles __state)
        => __state = BreathVolumeService.CapturePartObjects(midiPart);

    [HarmonyPostfix]
    private static void Postfix(bool __result, BreathNativeObjectHandles __state)
    {
        if (__result)
            BreathVolumeService.ReleaseNativeObjects(__state);
    }
}

public sealed class BreathVolumeRemoveTrackPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRemoveTrackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "RemoveTrack";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMTrack) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMTrack track, out BreathNativeObjectHandles __state)
        => __state = BreathVolumeService.CaptureTrackObjects(track);

    [HarmonyPostfix]
    private static void Postfix(bool __result, BreathNativeObjectHandles __state)
    {
        if (__result)
            BreathVolumeService.ReleaseNativeObjects(__state);
    }
}

public sealed class BreathVolumeCommitHistoryPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeCommitHistoryPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Commit";
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool updateHistory, bool __result)
        => BreathVolumeService.OnNativeCommit(__instance, updateHistory, __result);
}

public sealed class BreathVolumeCanUndoPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeCanUndoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "CanUndo";

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, ref bool __result)
        => __result = BreathVolumeService.CanUndo(__instance, __result);
}

public sealed class BreathVolumeCanRedoPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeCanRedoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "CanRedo";

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, ref bool __result)
        => __result = BreathVolumeService.CanRedo(__instance, __result);
}

public sealed class BreathVolumeDirtyPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeDirtyPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "get_IsDirty";

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, ref bool __result)
        => __result |= BreathVolumeService.IsProjectDirty(__instance);
}

public sealed class BreathVolumeUndoPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeUndoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Undo";

    [HarmonyPrefix]
    private static bool Prefix(WIVSMSequence __instance)
        => !BreathVolumeService.HandleUndo(__instance);
}

public sealed class BreathVolumeRedoPatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeRedoPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Redo";

    [HarmonyPrefix]
    private static bool Prefix(WIVSMSequence __instance)
        => !BreathVolumeService.HandleRedo(__instance);
}

public sealed class BreathVolumeSequenceClosePatch : PatchBase
{
    public override string PatchName => nameof(BreathVolumeSequenceClosePatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Close";

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance)
        => BreathVolumeService.CloseSequence(__instance);
}
