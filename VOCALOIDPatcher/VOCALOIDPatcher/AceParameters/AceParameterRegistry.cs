using System;
using System.Collections.Generic;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.RegisterShift;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.AceParameters;

// Glue between the pure AceParameterDefinitions data and the host UI/settings.
// All custom parameter types (BVL/REG/ACE) are enumerated here so the shared
// parameter-header patches stay in one place.
internal static class AceParameterRegistry
{
    internal static bool IsAceEnabled => Settings.AceParameters;

    internal static IReadOnlyList<AceParameterDefinition> EnabledDefinitions
        => Settings.AceParameters ? AceParameterDefinitions.All : Array.Empty<AceParameterDefinition>();

    internal static bool IsAceType(ControlParameterTypeEnum type)
        => AceParameterDefinitions.IsAceType(type);

    internal static bool TryGet(ControlParameterTypeEnum type, out AceParameterDefinition definition)
        => AceParameterDefinitions.TryGet(type, out definition);

    internal static bool TryGetByCarrier(VSMControllerType carrier, out AceParameterDefinition definition)
        => AceParameterDefinitions.TryGetByCarrier(carrier, out definition);

    internal static bool IsCustomType(ControlParameterTypeEnum type)
        => AceParameterDefinitions.IsAceType(type) ||
           type.Equals(BreathVolumeService.ParameterType) ||
           type.Equals(RegisterShiftService.ParameterType);

    internal static bool IsCustomPanelType(ControlParameterTypeEnum type)
        => type.Equals(BreathVolumeService.ParameterType) ||
           type.Equals(RegisterShiftService.ParameterType);

    internal static void SynchronizeList(List<ControlParameterTypeEnum>? list, bool isAiTrack)
    {
        if (list == null)
            return;

        list.RemoveAll(item => AceParameterDefinitions.IsAceType(item));
        list.RemoveAll(item => item.Equals(BreathVolumeService.ParameterType));
        list.RemoveAll(item => item.Equals(RegisterShiftService.ParameterType));

        if (!isAiTrack && Settings.AceParameters)
        {
            foreach (var definition in AceParameterDefinitions.All)
                list.Add(definition.ParameterType);
        }

        if (Settings.IndividualBreathVolume)
            list.Add(BreathVolumeService.ParameterType);
        if (Settings.RegisterShift)
            list.Add(RegisterShiftService.ParameterType);
    }

    internal static bool TryGetRange(
        ControlParameterTypeEnum type,
        out int minimum,
        out int maximum,
        out int defaultValue)
    {
        if (AceParameterDefinitions.TryGet(type, out var ace))
        {
            minimum = ace.Minimum;
            maximum = ace.Maximum;
            defaultValue = ace.DefaultValue;
            return true;
        }
        if (type.Equals(BreathVolumeService.ParameterType))
        {
            minimum = BreathVolumeService.MinValue;
            maximum = BreathVolumeService.MaxValue;
            defaultValue = BreathVolumeService.DefaultValue;
            return true;
        }
        if (type.Equals(RegisterShiftService.ParameterType))
        {
            minimum = RegisterShiftService.MinValue;
            maximum = RegisterShiftService.MaxValue;
            defaultValue = RegisterShiftService.DefaultValue;
            return true;
        }
        minimum = 0;
        maximum = 0;
        defaultValue = 0;
        return false;
    }

    internal static string? GetDisplayName(ControlParameterTypeEnum type, bool shortName)
    {
        if (AceParameterDefinitions.TryGet(type, out var ace))
            return shortName ? ace.ShortName : TranslationManager.Tr(ace.NameKey);
        if (type.Equals(BreathVolumeService.ParameterType))
            return shortName ? "BVL" : TranslationManager.Tr("VOCALOIDPatcher_BreathVolume_Name");
        if (type.Equals(RegisterShiftService.ParameterType))
            return shortName ? "REG" : TranslationManager.Tr("VOCALOIDPatcher_RegisterShift_Name");
        return null;
    }

    internal static bool TryGetCarrier(ControlParameterTypeEnum type, out VSMControllerType carrier)
    {
        if (AceParameterDefinitions.TryGet(type, out var ace) && Settings.AceParameters)
        {
            carrier = ace.CarrierType;
            return true;
        }
        carrier = default;
        return false;
    }

    internal static bool IsCarrierType(VSMControllerType type)
    {
        if (!Settings.AceParameters)
            return false;
        foreach (var definition in AceParameterDefinitions.All)
        {
            if (definition.CarrierType == type)
                return true;
        }
        return false;
    }
}
