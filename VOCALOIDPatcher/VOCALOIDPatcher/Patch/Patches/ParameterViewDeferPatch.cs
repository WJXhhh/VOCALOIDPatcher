using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.RegisterShift;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using UpdateViewTypeFlag = Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag;

namespace VOCALOIDPatcher.Patch.Patches;

public class ParameterViewDeferPatch : PatchBase
{
    public override string PatchName        => "ParameterViewDeferPatch";
    public override Type   TargetClass      => typeof(ParameterView);
    public override string TargetMethodName => "UpdateView";

    public override Type[] ArgumentTypes => new[]
    {
        typeof(object),
        typeof(UpdateViewTypeFlag),
        typeof(UpdateObserverNotifyEventArgs),
        typeof(object)
    };

    private static readonly MethodInfo? MUpdateActiveTrack =
        AccessTools.Method(typeof(ParameterView), "UpdateActiveTrack", Type.EmptyTypes);

    private static readonly MethodInfo? MUpdateActivePart =
        AccessTools.Method(typeof(ParameterView), "UpdateActivePart", Type.EmptyTypes);

    private static readonly MethodInfo? MClearControlParameter =
        AccessTools.Method(typeof(ParameterView), "ClearControlParameter", Type.EmptyTypes);

    private static readonly FieldInfo? NomineeSurface =
        AccessTools.Field(typeof(ParameterView), "xUINomineeControlParameters");

    private sealed class PendingBox
    {
        public bool Scheduled;
        public bool TrackKind;
        public DispatcherOperation? Operation;
    }

    private static readonly ConditionalWeakTable<ParameterView, PendingBox> Pending = new();

    [HarmonyPrefix]
    private static bool Prefix(ParameterView __instance, UpdateViewTypeFlag typeFlags)
    {
        if (typeFlags == UpdateViewTypeFlag.SequenceChanged && Pending.TryGetValue(__instance, out var previous))
        {
            previous.Operation?.Abort();
            previous.Operation = null;
            previous.Scheduled = false;
            previous.TrackKind = false;
        }

        if (!Settings.DeferParameterViewUpdate || MUpdateActiveTrack == null || MUpdateActivePart == null ||
            MClearControlParameter == null)
            return true;

        if (typeFlags != UpdateViewTypeFlag.SequenceChanged &&
            typeFlags != UpdateViewTypeFlag.ActiveTrackChanged && typeFlags != UpdateViewTypeFlag.ActivePartChanged)
            return true;

        // Render runs before Background. Part wrappers survive native sequence
        // disposal, so leaving the old drawing list alive here is a use-after-free.
        try
        {
            MClearControlParameter.Invoke(__instance, null);
            if (NomineeSurface?.GetValue(__instance) is UINomineeControlParameters nominees)
            {
                nominees.ControlParameters.Clear();
                nominees.InvalidateVisual();
            }
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_ParameterViewDefer_Failed", e.Message));
            return true;
        }

        if (typeFlags == UpdateViewTypeFlag.SequenceChanged)
            return true;

        if (__instance.DataContext is not MusicalEditorViewModel vm ||
            vm.VSMSequence is not { IsOpen: true } ||
            BreathVolumeService.IsActive(vm.ControlParameterType) ||
            RegisterShiftService.IsActive(vm.ControlParameterType))
            return true;

        var sequence = vm.Sequence;

        var box = Pending.GetOrCreateValue(__instance);
        if (typeFlags == UpdateViewTypeFlag.ActiveTrackChanged)
            box.TrackKind = true;

        if (box.Scheduled)
            return false;

        box.Scheduled = true;
        box.Operation = __instance.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Operation = null;
            box.Scheduled = false;
            bool trackKind = box.TrackKind;
            box.TrackKind = false;

            try
            {
                if (!ReferenceEquals(__instance.DataContext, vm) || !ReferenceEquals(vm.Sequence, sequence) ||
                    vm.VSMSequence is not { IsOpen: true } ||
                    BreathVolumeService.IsActive(vm.ControlParameterType) ||
                    RegisterShiftService.IsActive(vm.ControlParameterType))
                    return;
                (trackKind ? MUpdateActiveTrack : MUpdateActivePart).Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_ParameterViewDefer_Failed", e.Message));
            }
        }), DispatcherPriority.Background);

        return false;
    }
}
