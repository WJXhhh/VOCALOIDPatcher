using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using HarmonyLib;
using VOCALOIDPatcher.AceParameters;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.RegisterShift;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

// Registry-driven parameter header support shared by BVL, REG and the ACE
// parameters. Keeping this outside the BVL feature group ensures the combo box
// stays consistent no matter which of the features is enabled.
internal static class CustomParameterUi
{
    private static readonly ConditionalWeakTable<ParameterHeaderControl, object> ToolTipHooks = new();
    private static readonly List<WeakReference<ParameterHeaderControl>> Headers = new();
    private static bool _subscribed;

    public static void RegisterHeader(ParameterHeaderControl header)
    {
        lock (Headers)
        {
            Headers.Add(new WeakReference<ParameterHeaderControl>(header));
            if (!_subscribed)
            {
                BreathVolumeService.Changed += (_, _) => RefreshHeaders();
                RegisterShiftService.Changed += _ => RefreshHeaders();
                _subscribed = true;
            }
        }
        AttachSafeToolTip(header);
        SynchronizeHeader(header);
    }

    public static void SynchronizeHeader(ParameterHeaderControl header)
    {
        var ai = AccessTools.Field(typeof(ParameterHeaderControl), "MidiAiControlParameterTypes")
            ?.GetValue(header) as List<ControlParameterTypeEnum>;
        var standard = AccessTools.Field(typeof(ParameterHeaderControl), "MidiControlParameterTypes")
            ?.GetValue(header) as List<ControlParameterTypeEnum>;
        AceParameterRegistry.SynchronizeList(ai, isAiTrack: true);
        AceParameterRegistry.SynchronizeList(standard, isAiTrack: false);

        if (header.DataContext is MusicalEditorViewModel vm)
        {
            if (!Settings.IndividualBreathVolume && vm.ControlParameterType.Equals(BreathVolumeService.ParameterType))
                vm.ControlParameterType = ControlParameterTypeEnum.Dynamics;
            else if (!Settings.RegisterShift && vm.ControlParameterType.Equals(RegisterShiftService.ParameterType))
                vm.ControlParameterType = ControlParameterTypeEnum.Dynamics;
            else if (AceParameterDefinitions.IsAceType(vm.ControlParameterType) && !Settings.AceParameters)
                vm.ControlParameterType = ControlParameterTypeEnum.Dynamics;

            var current = vm.ActiveTrack?.Type == VSMTrackType.MidiAi ? ai : standard;
            if (current != null)
                header.ControlParameterTypes = current.ToList();
        }
    }

    public static void RefreshHeaders(bool synchronize = false)
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
                    {
                        UpdateHeaderToolTip(header, vm.ControlParameterType);
                        BreathVolumeUi.UpdateHeaderValue(header, vm.ControlParameterType);
                    }
                }
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Refresh();
        else
            dispatcher.BeginInvoke((Action)Refresh);
    }

    public static void UpdateHeaderToolTip(ParameterHeaderControl header, ControlParameterTypeEnum type)
    {
        try
        {
            var combo = AccessTools.Field(typeof(ParameterHeaderControl), "xControlParameterComboBox")
                ?.GetValue(header) as FlatComboBox;
            if (combo == null)
                return;

            var name = GetCustomParameterName(type);
            if (name != null)
                ToolTipService.SetToolTip(combo, name);
            else
                combo.ClearValue(ToolTipService.ToolTipProperty);
        }
        catch (Exception ex)
        {
            Debug.Print($"UpdateHeaderToolTip error: {ex}");
        }
    }

    private static void AttachSafeToolTip(ParameterHeaderControl header)
    {
        if (ToolTipHooks.TryGetValue(header, out _))
            return;

        var combo = AccessTools.Field(typeof(ParameterHeaderControl), "xControlParameterComboBox")
            ?.GetValue(header) as FlatComboBox;
        if (combo == null)
            return;

        combo.AddHandler(
            ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(OnControlParameterToolTipOpening),
            handledEventsToo: true);
        ToolTipHooks.Add(header, new object());
    }

    private static void OnControlParameterToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FlatComboBox combo)
            return;

        var source = e.OriginalSource as DependencyObject;
        var toolTip = source == null ? null : ToolTipService.GetToolTip(source);
        var type = toolTip is ControlParameterTypeEnum toolTipType
            ? toolTipType
            : source is FrameworkElement { DataContext: ControlParameterTypeEnum dataType }
                ? dataType
                : combo.SelectedItem is ControlParameterTypeEnum selectedType
                    ? selectedType
                    : (ControlParameterTypeEnum?)null;
        var name = type.HasValue ? GetCustomParameterName(type.Value) : null;
        if (name == null)
            return;

        // WPF's default EnumConverter throws for our intentionally out-of-range
        // parameter IDs while constructing a delayed ToolTip popup. Replace the
        // raw enum before ContentPresenter performs its default expansion.
        ToolTipService.SetToolTip(source ?? combo, name);
    }

    private static string? GetCustomParameterName(ControlParameterTypeEnum type)
        => AceParameterRegistry.GetDisplayName(type, shortName: false);
}

public sealed class CustomParameterHeaderConstructorPatch : PatchBase
{
    public override string PatchName => nameof(CustomParameterHeaderConstructorPatch);
    public override Type TargetClass => typeof(ParameterHeaderControl);
    public override string TargetMethodName => ".ctor";
    public override bool IsConstructor => true;

    [HarmonyPostfix]
    private static void Postfix(ParameterHeaderControl __instance)
    {
        try { CustomParameterUi.RegisterHeader(__instance); }
        catch (Exception e) { LogFailure(e); }
    }

    private static void LogFailure(Exception e)
        => Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message));
}

public sealed class CustomParameterComboPatch : PatchBase
{
    public override string PatchName => nameof(CustomParameterComboPatch);
    public override Type TargetClass => typeof(ParameterHeaderControl);
    public override string TargetMethodName => "UpdateControlParameterComboBox";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMMidiTrack), typeof(bool) };

    [HarmonyPostfix]
    private static void Postfix(ParameterHeaderControl __instance)
    {
        try { CustomParameterUi.SynchronizeHeader(__instance); }
        catch (Exception e) { Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message)); }
    }
}

public sealed class CustomParameterNamePatch : PatchBase
{
    public override string PatchName => nameof(CustomParameterNamePatch);
    public override Type TargetClass => typeof(ControlParameterTypeEnumToStringConverter);
    public override string TargetMethodName => "Convert";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(Type), typeof(object), typeof(CultureInfo) };

    [HarmonyPrefix]
    private static bool Prefix(object value, ref object __result)
    {
        if (value is not ControlParameterTypeEnum type)
            return true;
        var name = AceParameterRegistry.GetDisplayName(type, shortName: false);
        if (name == null)
            return true;
        __result = name;
        return false;
    }
}

public sealed class CustomParameterShortNamePatch : PatchBase
{
    public override string PatchName => nameof(CustomParameterShortNamePatch);
    public override Type TargetClass => typeof(ControlParameterTypeEnumShortToStringConverter);
    public override string TargetMethodName => "Convert";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(Type), typeof(object), typeof(CultureInfo) };

    [HarmonyPrefix]
    private static bool Prefix(object value, ref object __result)
    {
        if (value is not ControlParameterTypeEnum type)
            return true;
        var name = AceParameterRegistry.GetDisplayName(type, shortName: true);
        if (name == null)
            return true;
        __result = name;
        return false;
    }
}

public sealed class CustomParameterMinimumPatch : PatchBase
{
    public override string PatchName => nameof(CustomParameterMinimumPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "GetMinControllerValue";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ControlParameterTypeEnum type, ref int __result)
    {
        if (!AceParameterRegistry.TryGetRange(type, out var minimum, out _, out _))
            return true;
        __result = minimum;
        return false;
    }
}

public sealed class CustomParameterMaximumPatch : PatchBase
{
    public override string PatchName => nameof(CustomParameterMaximumPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "GetMaxControllerValue";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ControlParameterTypeEnum type, ref int __result)
    {
        if (!AceParameterRegistry.TryGetRange(type, out _, out var maximum, out _))
            return true;
        __result = maximum;
        return false;
    }
}

public sealed class CustomParameterDefaultPatch : PatchBase
{
    public override string PatchName => nameof(CustomParameterDefaultPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "GetDefaultControllerValue";
    public override Type[] ArgumentTypes => new[] { typeof(ControlParameterTypeEnum) };

    [HarmonyPrefix]
    private static bool Prefix(ControlParameterTypeEnum type, ref int __result)
    {
        if (!AceParameterRegistry.TryGetRange(type, out _, out _, out var defaultValue))
            return true;
        __result = defaultValue;
        return false;
    }
}
