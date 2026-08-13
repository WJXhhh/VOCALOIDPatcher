using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.Mcp.Core;

internal static class McpEventHub
{
    private static readonly BoundedEventBuffer Buffer = new(2048);

    public static long LatestEventId => Buffer.LatestEventId;

    public static void Publish(string type, string? projectId = null, long? revision = null, object? data = null)
        => Buffer.Publish(type, projectId, revision, data);

    public static async Task<object> WaitAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        long afterEventId = arguments.TryGetProperty("after_event_id", out JsonElement after) && after.TryGetInt64(out long parsedAfter)
            ? parsedAfter
            : 0;
        int timeoutMs = arguments.TryGetProperty("timeout_ms", out JsonElement timeout) && timeout.TryGetInt32(out int parsedTimeout)
            ? Math.Clamp(parsedTimeout, 0, 60_000)
            : 30_000;
        int limit = arguments.TryGetProperty("limit", out JsonElement limitElement) && limitElement.TryGetInt32(out int parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 1000)
            : 100;
        HashSet<string>? types = null;
        if (arguments.TryGetProperty("types", out JsonElement typesElement) && typesElement.ValueKind == JsonValueKind.Array)
            types = typesElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<McpEvent> events = await Buffer.WaitAsync(afterEventId, TimeSpan.FromMilliseconds(timeoutMs), limit, types, cancellationToken).ConfigureAwait(false);
        return new
        {
            events,
            latest_event_id = Buffer.LatestEventId,
            timed_out = events.Count == 0,
        };
    }

    public static async Task<object> WaitForAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string condition = arguments.TryGetProperty("condition", out JsonElement conditionElement) && conditionElement.ValueKind == JsonValueKind.String
            ? conditionElement.GetString()!.ToLowerInvariant()
            : throw new ArgumentException("condition is required.");
        string eventType = condition switch
        {
            "revision" => "project_revision_changed",
            "render_idle" => "render_idle",
            "playback" => "transport_changed",
            _ => throw new ArgumentException("condition must be revision, render_idle, or playback."),
        };
        long after = arguments.TryGetProperty("after_event_id", out JsonElement afterElement) && afterElement.TryGetInt64(out long parsedAfter) ? parsedAfter : 0;
        int timeoutMs = arguments.TryGetProperty("timeout_ms", out JsonElement timeoutElement) && timeoutElement.TryGetInt32(out int parsedTimeout) ? Math.Clamp(parsedTimeout, 0, 60_000) : 30_000;
        long targetRevision = arguments.TryGetProperty("target_revision", out JsonElement revisionElement) && revisionElement.TryGetInt64(out long parsedRevision) ? parsedRevision : 0;
        bool? playing = arguments.TryGetProperty("is_playing", out JsonElement playingElement) && playingElement.ValueKind is JsonValueKind.True or JsonValueKind.False ? playingElement.GetBoolean() : null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new { satisfied = false, condition, latest_event_id = Buffer.LatestEventId, timed_out = true };
            IReadOnlyList<McpEvent> events = await Buffer.WaitAsync(after, remaining, 100, new HashSet<string>(StringComparer.Ordinal) { eventType }, cancellationToken).ConfigureAwait(false);
            McpEvent? match = events.FirstOrDefault(item => condition switch
            {
                "revision" => item.Revision >= targetRevision,
                "render_idle" => true,
                "playback" => playing == null || item.Data is { } data && data.TryGetProperty("is_playing", out JsonElement value) && value.GetBoolean() == playing,
                _ => false,
            });
            if (match != null)
                return new { satisfied = true, condition, @event = match, latest_event_id = Buffer.LatestEventId, timed_out = false };
            if (events.Count == 0)
                return new { satisfied = false, condition, latest_event_id = Buffer.LatestEventId, timed_out = true };
            after = events[^1].EventId;
        }
    }
}
