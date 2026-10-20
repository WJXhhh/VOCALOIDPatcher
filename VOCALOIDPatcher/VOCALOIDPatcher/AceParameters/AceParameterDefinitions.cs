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
        string nameKey)
    {
        Kind = kind;
        ParameterType = parameterType;
        CarrierType = carrierType;
        ShortName = shortName;
        NameKey = nameKey;
    }

    internal AceParameterKind Kind { get; }

    // Fabricated enum value appended to the parameter combo box. The value is
    // intentionally far outside the native enum range (4CC style), matching the
    // established BVL/REG approach.
    internal ControlParameterTypeEnum ParameterType { get; }

    // Engine-visible VSM curve storage. Use only carriers verified by the
    // offline render probe; some also appear in the native parameter UI.
    internal VSMControllerType CarrierType { get; set; }

    internal string ShortName { get; }

    internal string NameKey { get; }

    internal int Order => (int)Kind;

    internal int Minimum => AceParameterMapping.CurveMinimum;

    internal int Maximum => AceParameterMapping.CurveMaximum;

    internal int DefaultValue => AceParameterMapping.CurveDefault;
}

// Carrier storage notes
//
// The fabricated parameter types reuse real VSMControllerType values only as
// curve storage for the native ParameterView (draw/edit/undo). That storage is
// engine-visible: several controller types are consumed by the renderer, so a
// user curve written into them also drives an unintended native parameter.
//
// Offline render A/B (voicebank/tools/evec_render_harness --ace-carrier-audibility-probe,
// V4X MIKU, V5 Chinese Luo_Tianyi_Wan, V6 Tianyi_CHN) shows:
//   - DynamicsHmm and PitchBendHmm are audible. A localized bump there changes
//     the later notes even after the curve returns to the ACE neutral 50,
//     because 50 is not that controller's engine-neutral value. This was the
//     "Tension affects following notes" bug.
//   - WeightHmm, Expression, S5Air and XSynth leave the render bit-identical in
//     every tested scenario, so they are safe as storage carriers.
// Keep the four parameters on inert carriers only. Do not move them back to
// DynamicsHmm/PitchBendHmm, and re-run the probe before changing any carrier.
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
        EnergyNameKey);

    private static readonly AceParameterDefinition Air = new(
        AceParameterKind.Air,
        (ControlParameterTypeEnum)AirTypeId,
        VSMControllerType.S5Air,
        "ACE-A",
        AirNameKey);

    private static readonly AceParameterDefinition Tension = new(
        AceParameterKind.Tension,
        (ControlParameterTypeEnum)TensionTypeId,
        VSMControllerType.XSynth,
        "ACE-T",
        TensionNameKey);

    private static readonly AceParameterDefinition Falsetto = new(
        AceParameterKind.Falsetto,
        (ControlParameterTypeEnum)FalsettoTypeId,
        VSMControllerType.Expression,
        "ACE-F",
        FalsettoNameKey);

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
