using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.AceParameters;

// Pure mapping math shared with the offline test project. This file must not
// reference Yamaha/VSM types so it can be compiled without VOCALOID6.dll.

internal enum AceParameterKind
{
    Energy = 0,
    Air = 1,
    Tension = 2,
    Falsetto = 3,
}

internal enum AceSoundTarget
{
    Dynamics = 0,
    Breathiness = 1,
    NativeAir = 2,
    Brightness = 3,
    Character = 4,
}

internal enum AceAirMappingTarget
{
    Breathiness = 0,
    NativeAir = 1,
}

internal readonly record struct AceCurvePoint(long Tick, int Value);

internal readonly record struct NativeControllerPoint(long Tick, int Value);

internal static class AceParameterMapping
{
    internal const int CurveMinimum = 0;
    internal const int CurveMaximum = 100;

    // Neutral ACE curve value. 50 maps to the target controller's native
    // default value so an untouched curve never shifts the sound.
    internal const int CurveDefault = 50;

    internal static bool TryGetTargetRange(
        AceSoundTarget target,
        out int minimum,
        out int maximum,
        out int defaultValue)
    {
        switch (target)
        {
            case AceSoundTarget.Dynamics:
                minimum = 0;
                maximum = 127;
                defaultValue = 64;
                return true;
            case AceSoundTarget.Breathiness:
            case AceSoundTarget.NativeAir:
                minimum = 0;
                maximum = 127;
                defaultValue = 0;
                return true;
            case AceSoundTarget.Brightness:
                minimum = 0;
                maximum = 127;
                defaultValue = 64;
                return true;
            case AceSoundTarget.Character:
                minimum = -64;
                maximum = 63;
                defaultValue = 0;
                return true;
            default:
                minimum = 0;
                maximum = 0;
                defaultValue = 0;
                return false;
        }
    }

    internal static int Map(AceParameterKind kind, AceSoundTarget target, int curveValue)
    {
        if (!TryGetTargetRange(target, out var minimum, out var maximum, out var defaultValue))
            return 0;

        // Neutral maps to the native default; each side scales independently so
        // curve 0/100 hit the native min/max even when the default is not the
        // midpoint (e.g. Dynamics default 64 of 0..127, Breathiness 0 of 0..127).
        int value = clampCurve(curveValue);
        double scaled = value >= CurveDefault
            ? defaultValue + (value - CurveDefault) * (double)(maximum - defaultValue) / (CurveMaximum - CurveDefault)
            : defaultValue + (value - CurveDefault) * (double)(defaultValue - minimum) / (CurveDefault - CurveMinimum);

        int rounded = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, minimum, maximum);
    }

    internal static AceSoundTarget MapAirTarget(AceAirMappingTarget target)
        => target == AceAirMappingTarget.NativeAir
            ? AceSoundTarget.NativeAir
            : AceSoundTarget.Breathiness;

    internal static List<NativeControllerPoint> BuildDerivedCurve(
        AceParameterKind kind,
        AceSoundTarget target,
        IReadOnlyList<AceCurvePoint> curve)
    {
        var result = new List<NativeControllerPoint>(curve.Count);
        long lastTick = long.MinValue;
        for (int index = 0; index < curve.Count; index++)
        {
            var point = curve[index];
            if (point.Tick < 0)
                continue;
            int value = Map(kind, target, point.Value);
            if (point.Tick == lastTick)
            {
                // Later points win on duplicate ticks; keep the list sorted
                // and free of duplicate positions for the native controller API.
                result[^1] = new NativeControllerPoint(point.Tick, value);
                continue;
            }
            result.Add(new NativeControllerPoint(point.Tick, value));
            lastTick = point.Tick;
        }
        return result;
    }

    internal static int clampCurve(int value)
        => Math.Clamp(value, CurveMinimum, CurveMaximum);

    internal static bool IsAirTargetSwitchable(AceParameterKind kind)
        => kind == AceParameterKind.Air;
}
