using System;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.BreathVolume;

internal interface ICustomParameterHistoryEdit
{
    void ApplyBefore();
    void ApplyAfter();
    void AfterApply();
}

/// <summary>
/// Single ordering boundary shared by native edits and patch-owned note parameters.
/// BVL retains the storage implementation so existing project histories remain compatible.
/// </summary>
internal static class CustomParameterHistoryCoordinator
{
    public static void Push(
        WIVSMSequence sequence,
        ICustomParameterHistoryEdit edit)
        => BreathVolumeService.PushExternalHistory(sequence, edit);

    public static void OnNativeCommit(WIVSMSequence sequence, bool updateHistory, bool result)
        => BreathVolumeService.OnNativeCommit(sequence, updateHistory, result);

    public static bool CanUndo(WIVSMSequence sequence, bool nativeResult)
        => BreathVolumeService.CanUndo(sequence, nativeResult);

    public static bool CanRedo(WIVSMSequence sequence, bool nativeResult)
        => BreathVolumeService.CanRedo(sequence, nativeResult);

    public static bool IsDirty(WIVSMSequence sequence)
        => BreathVolumeService.IsProjectDirty(sequence);

    public static bool Undo(WIVSMSequence sequence) => BreathVolumeService.HandleUndo(sequence);
    public static bool Redo(WIVSMSequence sequence) => BreathVolumeService.HandleRedo(sequence);

    public static bool UndoPatchOwned(WIVSMSequence sequence)
        => BreathVolumeService.HandlePatchOwnedUndo(sequence);

    public static bool RedoPatchOwned(WIVSMSequence sequence)
        => BreathVolumeService.HandlePatchOwnedRedo(sequence);
}
