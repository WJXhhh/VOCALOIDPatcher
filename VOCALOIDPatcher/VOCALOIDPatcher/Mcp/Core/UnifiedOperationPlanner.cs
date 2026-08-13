using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp.Core;

internal sealed class UnifiedOperationPlanningException : Exception
{
    public int OperationIndex { get; }
    public string OperationId { get; }
    public string? Field { get; }

    public UnifiedOperationPlanningException(int operationIndex, string operationId, string? field, string message)
        : base(message)
    {
        OperationIndex = operationIndex;
        OperationId = operationId;
        Field = field;
    }
}

internal sealed class UnifiedOperationPlanner
{
    internal sealed record PlannedOperation(
        int Index,
        string Domain,
        string OperationId,
        JsonElement Payload,
        string? TempId,
        string? ClientTag,
        bool Creates,
        bool Deletes);

    private readonly WIVSMSequence _sequence;
    private readonly string _projectId;
    private readonly long _revision;
    private readonly Dictionary<string, EntityRef> _temporary = new(StringComparer.Ordinal);

    public UnifiedOperationPlanner(WIVSMSequence sequence, string projectId, long revision)
    {
        _sequence = sequence;
        _projectId = projectId;
        _revision = revision;
    }

    public IReadOnlyList<PlannedOperation> Plan(JsonElement operations)
    {
        var result = new List<PlannedOperation>(operations.GetArrayLength());
        int index = 0;
        foreach (JsonElement source in operations.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Operation {index} must be an object.");
            string domain = ReadString(source, "domain")?.ToLowerInvariant()
                            ?? throw new InvalidOperationException($"Operation {index} requires domain.");
            JsonObject payload = JsonNode.Parse(source.GetRawText())!.AsObject();
            payload.Remove("domain");
            ResolveReferences(payload);
            string verb = domain == "g2pa" ? ReadString(source, "action") ?? string.Empty : ReadString(source, "op") ?? string.Empty;
            if (verb.Length == 0)
                throw new InvalidOperationException($"Operation {index} requires {(domain == "g2pa" ? "action" : "op")}.");
            bool creates = domain switch
            {
                "structure" => verb is "add_track" or "add_part" or "duplicate_part" or "add_tempo" or "add_time_signature",
                "notes" => verb is "add" or "duplicate" or "copy",
                "parameters" => verb is "add_controller" or "set_controller" or "track_volume" or "track_pan" or "master_volume",
                "g2pa" => false,
                "extension_parameters" => false,
                _ when McpDomainRegistry.TryClassify(domain, verb, out bool domainCreates, out _) => domainCreates,
                _ => throw new InvalidOperationException($"Unknown operation domain '{domain}'."),
            };
            bool deletes = McpDomainRegistry.TryClassify(domain, verb, out _, out bool domainDeletes)
                ? domainDeletes
                : verb.StartsWith("delete", StringComparison.Ordinal) || verb is "clear" or "clear_direct_pitch";
            string? tempId = ReadString(source, "temp_id");
            if (tempId != null)
            {
                if (!creates)
                    throw new InvalidOperationException($"Operation {index} uses temp_id but does not create an entity.");
                if (!_temporary.TryAdd(tempId, PredictedReference(domain, verb, payload)))
                    throw new InvalidOperationException($"Duplicate temp_id '{tempId}'.");
            }
            JsonElement element = JsonSerializer.SerializeToElement(payload, BridgeProtocol.JsonOptions);
            string operationId = $"{domain}.{verb}";
            IReadOnlyList<string> missing = OperationContractValidator.Validate(operationId, element);
            if (missing.Count > 0)
                throw new UnifiedOperationPlanningException(index, operationId, missing[0], $"Operation {index} ({operationId}) is missing: {string.Join(", ", missing)}.");
            result.Add(new PlannedOperation(index, domain, operationId, element, tempId, ReadString(source, "client_tag"), creates, deletes));
            index++;
        }
        return result;
    }

    public void UpdateTemporary(string tempId, EntityRef reference) => _temporary[tempId] = reference;

    public PlannedOperation Resolve(PlannedOperation operation)
    {
        JsonObject payload = JsonNode.Parse(operation.Payload.GetRawText())!.AsObject();
        ResolveReferences(payload);
        return operation with { Payload = JsonSerializer.SerializeToElement(payload, BridgeProtocol.JsonOptions) };
    }

    private void ResolveReferences(JsonObject payload)
    {
        ResolveTemp(payload, "track_temp_id", "track_index", "track");
        ResolveTemp(payload, "part_temp_id", "part_index", "part");
        ResolveTemp(payload, "note_temp_id", "note_index", "note");
        ResolveStable(payload, "track_entity_id", "track_index", "track");
        ResolveStable(payload, "part_entity_id", "part_index", "part");
        ResolveStable(payload, "note_entity_id", "note_index", "note");
        if (payload["entity_id"]?.GetValue<string>() is { } entityId)
        {
            var resolved = McpEntityRegistry.Resolve(_sequence, _projectId, entityId)
                           ?? throw new InvalidOperationException($"Entity '{entityId}' is no longer present in this project.");
            payload["track_index"] = resolved.TrackIndex;
            if (resolved.PartIndex >= 0)
                payload["part_index"] = resolved.PartIndex;
            if (resolved.ItemIndex >= 0)
                payload["note_index"] = resolved.ItemIndex;
        }
    }

    private void ResolveTemp(JsonObject payload, string sourceName, string indexName, string expectedKind)
    {
        if (payload[sourceName]?.GetValue<string>() is not { } tempId)
            return;
        if (!_temporary.TryGetValue(tempId, out EntityRef? reference) || !string.Equals(reference.Kind, expectedKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown or incompatible {sourceName} '{tempId}'.");
        if (string.Equals(reference.EntityId, "temporary", StringComparison.Ordinal))
            payload[$"_mcp_virtual_{expectedKind}"] = true;
        payload["track_index"] = reference.TrackIndex;
        if (reference.PartIndex >= 0)
            payload["part_index"] = reference.PartIndex;
        if (reference.ItemIndex >= 0)
            payload[indexName] = reference.ItemIndex;
    }

    private void ResolveStable(JsonObject payload, string sourceName, string indexName, string expectedKind)
    {
        if (payload[sourceName]?.GetValue<string>() is not { } entityId)
            return;
        var resolved = McpEntityRegistry.Resolve(_sequence, _projectId, entityId)
                       ?? throw new InvalidOperationException($"Entity '{entityId}' is no longer present in this project.");
        if (!string.Equals(resolved.Kind, expectedKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"{sourceName} does not identify a {expectedKind}.");
        payload["track_index"] = resolved.TrackIndex;
        if (resolved.PartIndex >= 0)
            payload["part_index"] = resolved.PartIndex;
        if (resolved.ItemIndex >= 0)
            payload[indexName] = resolved.ItemIndex;
    }

    private EntityRef PredictedReference(string domain, string verb, JsonObject payload)
    {
        if (domain == "structure" && verb == "add_track")
        {
            int index = payload["index"]?.GetValue<int>() ?? _sequence.Tracks.Count;
            return new EntityRef(_projectId, _revision, "track", index, EntityId: "temporary");
        }
        if (domain == "structure" && verb is "add_part" or "duplicate_part")
        {
            int track = payload["track_index"]?.GetValue<int>() ?? -1;
            int part = track >= 0 && track < _sequence.Tracks.Count ? _sequence.Tracks[track].Parts.Count : 0;
            return new EntityRef(_projectId, _revision, "part", track, part, EntityId: "temporary");
        }
        if (domain == "notes" && verb is "add" or "duplicate" or "copy")
        {
            int track = payload["track_index"]?.GetValue<int>() ?? -1;
            int part = payload["part_index"]?.GetValue<int>() ?? -1;
            int note = track >= 0 && track < _sequence.Tracks.Count && part >= 0 && part < _sequence.Tracks[track].Parts.Count && _sequence.Tracks[track].Parts[part] is WIVSMMidiPart midi
                ? midi.Notes.Count
                : 0;
            return new EntityRef(_projectId, _revision, "note", track, part, note, "temporary");
        }
        return new EntityRef(_projectId, _revision, domain, EntityId: "temporary");
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
