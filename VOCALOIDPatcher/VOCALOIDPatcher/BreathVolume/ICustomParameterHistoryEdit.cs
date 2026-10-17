namespace VOCALOIDPatcher.BreathVolume;

internal interface ICustomParameterHistoryEdit
{
    void ApplyBefore();
    void ApplyAfter();
    void AfterApply();
}

// A companion to a real native history entry. It never replaces Native Undo;
// it rebinds patch-owned identities only after native objects are restored.
internal interface INativeParameterHistoryEdit
{
    void AfterNativeHistory(bool undo);
}
