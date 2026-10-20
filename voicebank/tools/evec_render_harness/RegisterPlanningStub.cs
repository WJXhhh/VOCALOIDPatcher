namespace VOCALOIDPatcher.RegisterShift;

internal static class RegisterShiftService
{
    internal static int CaptureValue(Yamaha.VOCALOID.VSM.WIVSMNote note) => 0;
}

// This standalone harness does not publish register-shift controls. Refuse a
// shifted request explicitly instead of silently testing a different plan.
internal static class NativeRegisterShift
{
    internal readonly record struct PlanningNote(long Begin, long End, int Pitch, int Shift);
    internal static bool BeginPlanning(PlanningNote[]? notes)
    {
        if (notes?.Any(n => n.Shift != 0) == true)
            throw new NotSupportedException("Register-shift planning is not enabled in this harness.");
        return false;
    }
    internal static void EndPlanning() => throw new InvalidOperationException("No register planning scope.");
}
