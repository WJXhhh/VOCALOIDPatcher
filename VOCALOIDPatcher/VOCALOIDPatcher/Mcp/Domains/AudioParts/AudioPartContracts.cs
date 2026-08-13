using System;
using System.Collections.Generic;
using System.Text.Json;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.Mcp.Domains.AudioParts;

internal static class AudioPartContracts
{
    internal static readonly OperationContract[] Operations =
    {
        new("audio_parts.create", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "source_path" }, new[] { "absolute_tick", "duration_tick", "region_tick_begin", "region_tick_end", "name", "temp_id", "client_tag" }),
        new("audio_parts.replace_source", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "part_index", "source_path" }, new[] { "entity_id", "client_tag" }, true),
        new("audio_parts.move", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "part_index", "absolute_tick" }, new[] { "to_track_index", "entity_id", "client_tag" }),
        new("audio_parts.trim_region", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "part_index", "region_tick_begin", "region_tick_end" }, new[] { "entity_id", "client_tag" }),
        new("audio_parts.set_length", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "part_index", "duration_tick" }, new[] { "entity_id", "client_tag" }),
        new("audio_parts.normalize", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "part_index" }, new[] { "entity_id", "client_tag" }),
        new("audio_parts.time_stretch", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "part_index", "duration_tick" }, new[] { "entity_id", "client_tag" }),
        new("audio_parts.delete", "audio_parts", "v6_edit_structure", new[] { "op", "track_index", "part_index" }, new[] { "entity_id", "client_tag" }, true),
    };

    internal static IReadOnlyList<string> ValidateOfflineOperation(JsonElement operation)
    {
        var errors = new List<string>();
        string? op = ReadString(operation, "op");
        if (op is not ("audio_normalize" or "normalize" or "audio_time_stretch" or "time_stretch"))
            errors.Add("op must be audio_normalize or audio_time_stretch.");
        ValidateIndex(operation, "track_index", errors);
        ValidateIndex(operation, "part_index", errors);
        if (op is "audio_time_stretch" or "time_stretch")
        {
            if (!operation.TryGetProperty("duration_tick", out JsonElement duration) || !duration.TryGetInt64(out long value) || value <= 0 || value > int.MaxValue)
                errors.Add("duration_tick must be between 1 and 2147483647.");
        }
        return errors;
    }

    private static void ValidateIndex(JsonElement operation, string name, ICollection<string> errors)
    {
        if (!operation.TryGetProperty(name, out JsonElement element) || !element.TryGetInt32(out int value) || value < 0)
            errors.Add($"{name} must be a non-negative integer.");
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
