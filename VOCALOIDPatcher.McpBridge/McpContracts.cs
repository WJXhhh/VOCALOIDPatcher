using System.Text.Json;

namespace VOCALOIDPatcher.McpBridge;

public static class McpErrorCodes
{
    public const string Cancelled = "cancelled";
    public const string InternalError = "internal_error";
    public const string InvalidReference = "invalid_reference";
    public const string InvalidRequest = "invalid_request";
    public const string OperationFailed = "operation_failed";
    public const string PermissionDenied = "permission_denied";
    public const string QueryTooLarge = "query_too_large";
    public const string StaleProject = "stale_project";
    public const string TemporarilyUnavailable = "temporarily_unavailable";
    public const string Unsupported = "unsupported";
    public const string V6Unavailable = "v6_unavailable";
    public const string WriteLeaseHeld = "write_lease_held";

    public static IReadOnlyList<object> Catalog { get; } = new object[]
    {
        Entry(Cancelled, false), Entry(InternalError, false), Entry(InvalidReference, false),
        Entry(InvalidRequest, false), Entry(OperationFailed, false), Entry(PermissionDenied, false),
        Entry(QueryTooLarge, true), Entry(StaleProject, true), Entry(TemporarilyUnavailable, true),
        Entry(Unsupported, false), Entry(V6Unavailable, true), Entry(WriteLeaseHeld, true),
    };

    private static object Entry(string code, bool retryable) => new { code, retryable };
}

public sealed record OperationContract(
    string Id,
    string Domain,
    string LegacyTool,
    string[] RequiredFields,
    string[] OptionalFields,
    bool Dangerous = false,
    string? MinimumEditorVersion = "6.13.0");

public sealed record DomainContract(
    string Id,
    string[] QueryKinds,
    string[] OperationIds,
    string[] EntityKinds,
    string CapabilityPrefix);

public static class McpContractCatalog
{
    public static IReadOnlyList<CapabilityStatus> StageSevenCapabilities { get; } = new[]
    {
        new CapabilityStatus("ui.selection", true, false, "6.13.0", null, "available"),
        new CapabilityStatus("ui.navigation.viewport", true, false, "6.13.0", null, "available"),
        new CapabilityStatus("ui.panel.parameter", true, false, "6.13.0", null, "available"),
        new CapabilityStatus("ui.panel.lower_zone", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("ui.panel.right_zone", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("transport.pause_resume", true, false, "6.13.0", null, "available"),
        new CapabilityStatus("transport.markers", true, false, "6.13.0", null, "available"),
        new CapabilityStatus("transport.grid_step", true, false, "6.13.0", null, "available"),
        new CapabilityStatus("transport.start_mode", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("transport.playback_rate", false, false, "6.13.0", "V6 6.13 exposes no confirmed semantic playback-rate setter.", "unsupported"),
        new CapabilityStatus("ui.edit_tools", true, false, "6.13.0", null, "available"),
    };
    public static IReadOnlyList<OperationContract> Operations { get; } = new[]
    {
        new OperationContract("structure.add_track", "structure", "v6_edit_structure", new[] { "op" }, new[] { "index", "type", "name", "temp_id", "client_tag" }),
        new OperationContract("structure.add_part", "structure", "v6_edit_structure", new[] { "op", "track_index" }, new[] { "absolute_tick", "duration_tick", "name", "temp_id", "client_tag" }),
        new OperationContract("structure.delete_track", "structure", "v6_edit_structure", new[] { "op", "track_index" }, new[] { "entity_id", "client_tag" }, true),
        new OperationContract("notes.add", "notes", "v6_edit_notes", new[] { "op", "track_index", "part_index", "duration_tick", "note_number" }, new[] { "absolute_tick", "part_relative_tick", "lyric", "temp_id", "client_tag" }),
        new OperationContract("notes.update", "notes", "v6_edit_notes", new[] { "op", "track_index", "part_index", "note_index" }, new[] { "entity_id", "lyric", "phonemes", "note_number", "duration_tick", "client_tag" }),
        new OperationContract("parameters.add_controller", "parameters", "v6_edit_parameters", new[] { "op", "track_index", "part_index", "parameter_type", "value" }, new[] { "part_relative_tick", "absolute_tick", "client_tag" }),
        new OperationContract("g2pa.set_lyrics", "g2pa", "v6_g2pa_apply", new[] { "action", "track_index", "part_index", "note_index", "lyrics" }, new[] { "entity_id", "client_tag" }),
    };

    public static IReadOnlyList<CapabilityStatus> BaselineCapabilities { get; } = new[]
    {
        Capability("query.summary", true), Capability("query.tracks", true),
        Capability("query.parts", true), Capability("query.notes", true),
        Capability("query.parameters", true), Capability("operation.structure", true),
        Capability("operation.notes", true), Capability("operation.parameters", true),
        Capability("operation.g2pa", true), Capability("operation.apply", true),
        Capability("events.wait", true), Capability("events.revision", true),
        new CapabilityStatus("project.revert", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("project.native_import.project", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("project.native_import.midi", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("project.native_import.tempo_time_signature", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("project.native_import.audio", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
        new CapabilityStatus("project.recent", true, false, "6.13.0", "Awaiting repeatable V6 host validation.", "host_validation_required"),
    };

    public static IReadOnlyList<DomainContract> Domains { get; } = new[]
    {
        new DomainContract("structure", new[] { "summary", "tracks", "parts", "tempos", "time_signatures" }, Operations.Where(item => item.Domain == "structure").Select(item => item.Id).ToArray(), new[] { "track", "part", "tempo", "time_signature" }, "operation.structure"),
        new DomainContract("notes", new[] { "notes", "lyrics", "phonemes" }, Operations.Where(item => item.Domain == "notes").Select(item => item.Id).ToArray(), new[] { "note" }, "operation.notes"),
        new DomainContract("parameters", new[] { "parameters" }, Operations.Where(item => item.Domain == "parameters").Select(item => item.Id).ToArray(), new[] { "parameter", "direct_pitch", "track_volume", "track_pan", "master_volume" }, "operation.parameters"),
        new DomainContract("g2pa", Array.Empty<string>(), Operations.Where(item => item.Domain == "g2pa").Select(item => item.Id).ToArray(), new[] { "note" }, "operation.g2pa"),
    };

    private static CapabilityStatus Capability(string id, bool implemented)
        => new(id, implemented, implemented, "6.13.0", implemented ? null : "Not implemented.", implemented ? "available" : "unsupported");
}

public static class OperationContractValidator
{
    public static IReadOnlyList<string> Validate(string operationId, JsonElement payload)
    {
        OperationContract? contract = McpContractCatalog.Operations.FirstOrDefault(item => string.Equals(item.Id, operationId, StringComparison.Ordinal));
        if (contract == null)
            return Array.Empty<string>();
        var missing = new List<string>();
        foreach (string field in contract.RequiredFields)
        {
            if (!payload.TryGetProperty(field, out JsonElement value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                missing.Add(field);
        }
        return missing;
    }
}

public sealed record McpEvent(
    long EventId,
    string Type,
    DateTimeOffset TimestampUtc,
    string? ProjectId = null,
    long? Revision = null,
    JsonElement? Data = null);

public sealed class BoundedEventBuffer
{
    private readonly object _gate = new();
    private readonly Queue<McpEvent> _events = new();
    private readonly List<TaskCompletionSource<bool>> _waiters = new();
    private readonly int _capacity;
    private long _nextEventId;

    public BoundedEventBuffer(int capacity = 1024)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public long LatestEventId
    {
        get { lock (_gate) return _nextEventId; }
    }

    public McpEvent Publish(string type, string? projectId = null, long? revision = null, object? data = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Event type is required.", nameof(type));
        TaskCompletionSource<bool>[] waiters;
        McpEvent value;
        lock (_gate)
        {
            value = new McpEvent(
                ++_nextEventId,
                type,
                DateTimeOffset.UtcNow,
                projectId,
                revision,
                data == null ? null : JsonSerializer.SerializeToElement(data, BridgeProtocol.JsonOptions));
            _events.Enqueue(value);
            while (_events.Count > _capacity)
                _events.Dequeue();
            waiters = _waiters.ToArray();
            _waiters.Clear();
        }
        foreach (TaskCompletionSource<bool> waiter in waiters)
            waiter.TrySetResult(true);
        return value;
    }

    public IReadOnlyList<McpEvent> ReadAfter(long afterEventId, int limit = 100, IReadOnlySet<string>? types = null)
    {
        limit = Math.Clamp(limit, 1, 1000);
        lock (_gate)
            return _events.Where(item => item.EventId > afterEventId && (types == null || types.Contains(item.Type))).Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<McpEvent>> WaitAsync(
        long afterEventId,
        TimeSpan timeout,
        int limit = 100,
        IReadOnlySet<string>? types = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<McpEvent> available = ReadAfter(afterEventId, limit, types);
        if (available.Count > 0 || timeout <= TimeSpan.Zero)
            return available;

        TaskCompletionSource<bool> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            available = _events.Where(item => item.EventId > afterEventId && (types == null || types.Contains(item.Type))).Take(limit).ToArray();
            if (available.Count > 0)
                return available;
            _waiters.Add(waiter);
        }

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await waiter.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
                _waiters.Remove(waiter);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ReadAfter(afterEventId, limit, types);
    }
}

public sealed class QueryBudget
{
    public const int DefaultMaxScannedItems = 25_000;
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;
    public const int DefaultMaxDispatcherMilliseconds = 250;

    private readonly long _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    public int MaxScannedItems { get; }
    public int MaxResponseBytes { get; }
    public int MaxDispatcherMilliseconds { get; }
    public int ScannedItems { get; private set; }

    public QueryBudget(int maxScannedItems = DefaultMaxScannedItems, int maxResponseBytes = DefaultMaxResponseBytes, int maxDispatcherMilliseconds = DefaultMaxDispatcherMilliseconds)
    {
        MaxScannedItems = Math.Clamp(maxScannedItems, 1, 1_000_000);
        MaxResponseBytes = Math.Clamp(maxResponseBytes, 1024, BridgeProtocol.MaxMessageBytes);
        MaxDispatcherMilliseconds = Math.Clamp(maxDispatcherMilliseconds, 10, 5000);
    }

    public bool TryScan(int count = 1)
    {
        ScannedItems = checked(ScannedItems + count);
        return ScannedItems <= MaxScannedItems && ElapsedMilliseconds <= MaxDispatcherMilliseconds;
    }

    public long ElapsedMilliseconds => (long)System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds;
}
