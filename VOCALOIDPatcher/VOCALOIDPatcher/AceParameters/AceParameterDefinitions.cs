using System;
using System.Collections.Generic;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.AceParameters;

internal sealed class AceParameterDefinition
{
    internal AceParameterDefinition(
        AceParameterKind kind,
        ControlParameterTypeEnum parameterType,
        VSMControllerType carrierType,
        string shortName,
        string nameKey,
        VSMControllerType soundTarget)
    {
        Kind = kind;
        ParameterType = parameterType;
        CarrierType = carrierType;
        ShortName = shortName;
        NameKey = nameKey;
        SoundTarget = soundTarget;
    }

    internal AceParameterKind Kind { get; }

    // Fabricated enum value appended to the parameter combo box. The value is
    // intentionally far outside the native enum range (4CC style), matching the
    // established BVL/REG approach.
    internal ControlParameterTypeEnum ParameterType { get; }

    // Native VSM controller used only as the curve storage. These carriers are
    // not exposed by the parameter UI and are expected to be ignored by the
    // classic V4/V5 renderer; the host probe verifies that before shipping.
    internal VSMControllerType CarrierType { get; set; }

    internal string ShortName { get; }

    internal string NameKey { get; }

    // Native controller that actually drives the sound for this ACE parameter.
    internal VSMControllerType SoundTarget { get; }

    internal int Order => (int)Kind;

    internal int Minimum => AceParameterMapping.CurveMinimum;

    internal int Maximum => AceParameterMapping.CurveMaximum;

    internal int DefaultValue => AceParameterMapping.CurveDefault;
}

internal static class AceParameterDefinitions
{
    // 4CC ids; must not collide with BVL (0x42564C) or REG (0x524547).
    private const int EnergyTypeId = 0x41454E; // 'AEN'
    private const int AirTypeId = 0x414152;    // 'AAR'
    private const int TensionTypeId = 0x414154; // 'AAT'
    private const int FalsettoTypeId = 0x414146; // 'AAF'

    internal const string EnergyNameKey = "VOCALOIDPatcher_AceParameter_Energy_Name";
    internal const string AirNameKey = "VOCALOIDPatcher_AceParameter_Air_Name";
    internal const string TensionNameKey = "VOCALOIDPatcher_AceParameter_Tension_Name";
    internal const string FalsettoNameKey = "VOCALOIDPatcher_AceParameter_Falsetto_Name";

    private static readonly AceParameterDefinition Energy = new(
        AceParameterKind.Energy,
        (ControlParameterTypeEnum)EnergyTypeId,
        VSMControllerType.WeightHmm,
        "ACE-E",
        EnergyNameKey,
        VSMControllerType.Dynamics);

    private static readonly AceParameterDefinition Air = new(
        AceParameterKind.Air,
        (ControlParameterTypeEnum)AirTypeId,
        VSMControllerType.PitchBendHmm,
        "ACE-A",
        AirNameKey,
        VSMControllerType.Breathiness);

    private static readonly AceParameterDefinition Tension = new(
        AceParameterKind.Tension,
        (ControlParameterTypeEnum)TensionTypeId,
        VSMControllerType.DynamicsHmm,
        "ACE-T",
        TensionNameKey,
        VSMControllerType.Brightness);

    private static readonly AceParameterDefinition Falsetto = new(
        AceParameterKind.Falsetto,
        (ControlParameterTypeEnum)FalsettoTypeId,
        VSMControllerType.Expression,
        "ACE-F",
        FalsettoNameKey,
        VSMControllerType.Character);

    internal static readonly IReadOnlyList<AceParameterDefinition> All = new[]
    {
        Energy, Air, Tension, Falsetto,
    };

    private static Dictionary<ControlParameterTypeEnum, AceParameterDefinition> _byParameterType = BuildByParameterType();
    private static Dictionary<VSMControllerType, AceParameterDefinition> _byCarrierType = BuildByCarrierType();

    internal static bool TryGet(ControlParameterTypeEnum parameterType, out AceParameterDefinition definition)
        => _byParameterType.TryGetValue(parameterType, out definition!);

    internal static bool TryGetByCarrier(VSMControllerType carrierType, out AceParameterDefinition definition)
        => _byCarrierType.TryGetValue(carrierType, out definition!);

    internal static bool IsAceType(ControlParameterTypeEnum parameterType)
        => _byParameterType.ContainsKey(parameterType);

    // Used by the host probe to evaluate alternative carrier types at runtime.
    internal static void SetCarrierType(AceParameterKind kind, VSMControllerType carrierType)
    {
        foreach (var definition in All)
        {
            if (definition.Kind == kind)
                definition.CarrierType = carrierType;
        }
        _byCarrierType = BuildByCarrierType();
    }

    private static Dictionary<ControlParameterTypeEnum, AceParameterDefinition> BuildByParameterType()
    {
        var result = new Dictionary<ControlParameterTypeEnum, AceParameterDefinition>();
        foreach (var definition in All)
            result[definition.ParameterType] = definition;
        return result;
    }

    private static Dictionary<VSMControllerType, AceParameterDefinition> BuildByCarrierType()
    {
        var result = new Dictionary<VSMControllerType, AceParameterDefinition>();
        foreach (var definition in All)
            result[definition.CarrierType] = definition;
        return result;
    }
}
