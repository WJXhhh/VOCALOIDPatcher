using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace VOCALOIDPatcher.Mcp.Domains.ExtensionParameters;

internal sealed record ExtensionParameterDescriptor(
    string Id,
    string Source,
    string Scope,
    string ValueType,
    int Minimum,
    int Maximum,
    int DefaultValue,
    bool CanClear,
    string Persistence,
    string Unit,
    string CapabilityId,
    string MinimumEditorVersion);

internal static class ExtensionParameterContracts
{
    public const string DomainId = "extension_parameters";
    public const string QueryKind = "extension_parameters";
    public const string ChangedEvent = "extension_parameter_changed";
    public const string RebuildEvent = "extension_parameter_rebuild_requested";
    public const string RebuildCompletedEvent = "extension_parameter_rebuild_completed";

    public static IReadOnlyList<ExtensionParameterDescriptor> Parameters { get; } = new[]
    {
        new ExtensionParameterDescriptor("patcher.bvl", "patcher", "note", "integer", 0, 127, 127, true,
            "vpr:VOCALOIDPatcher/breath-volume.json", "level", "operation.extension_parameters.bvl", "6.13.0"),
        new ExtensionParameterDescriptor("patcher.register_shift", "patcher", "note", "integer",
            -24, 24, 0, true,
            "vpr:VOCALOIDPatcher/register-shift.json", "semitone", "operation.extension_parameters.register_shift", "6.13.0"),
    };

    public static string Validate(JsonElement operation)
    {
        if (operation.ValueKind != JsonValueKind.Object)
            return "Operation must be an object.";
        string? op = String(operation, "op");
        if (op is not "set" and not "clear")
            return "op must be set or clear.";
        string? id = String(operation, "parameter_id");
        ExtensionParameterDescriptor? descriptor = Parameters.FirstOrDefault(item => item.Id == id);
        if (descriptor == null)
            return "parameter_id must identify a registered Patcher parameter.";
        if (!TryInt(operation, "track_index", out int track) || track < 0)
            return "track_index must be a non-negative integer.";
        if (!TryInt(operation, "part_index", out int part) || part < 0)
            return "part_index must be a non-negative integer.";
        if (!TryInt(operation, "note_index", out int note) || note < 0)
            return "note_index must be a non-negative integer.";
        if (op == "set" && (!TryInt(operation, "value", out int value) || value < descriptor.Minimum || value > descriptor.Maximum))
            return $"value must be between {descriptor.Minimum} and {descriptor.Maximum}.";
        return string.Empty;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.ToLowerInvariant()
            : null;

    private static bool TryInt(JsonElement element, string name, out int result)
    {
        result = default;
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out result);
    }
}
