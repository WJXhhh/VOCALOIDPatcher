using System.Collections.Concurrent;

namespace VOCALOIDPatcher.McpBridge;

public static class ProjectRevisionGuard
{
    public static BridgeError? Validate(
        string currentProjectId,
        long currentRevision,
        string suppliedProjectId,
        long suppliedRevision)
    {
        if (!string.Equals(currentProjectId, suppliedProjectId, StringComparison.Ordinal))
            return Error("The target project has been replaced.", currentProjectId, currentRevision);
        if (currentRevision != suppliedRevision)
            return Error("The project revision has changed.", currentProjectId, currentRevision);
        return null;
    }

    private static BridgeError Error(string message, string projectId, long revision)
        => new(
            "stale_project",
            message,
            Details: System.Text.Json.JsonSerializer.SerializeToElement(
                new { project_id = projectId, revision },
                BridgeProtocol.JsonOptions));
}

public sealed class BoundedIdempotencyCache<T>
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, T> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public BoundedIdempotencyCache(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _values.Count;

    public bool TryGet(string key, out T? value) => _values.TryGetValue(key, out value);

    public void Store(string key, T value)
    {
        if (_values.TryAdd(key, value))
            _order.Enqueue(key);
        else
            _values[key] = value;

        while (_values.Count > _capacity && _order.TryDequeue(out string? oldest))
            _values.TryRemove(oldest, out _);
    }
}
