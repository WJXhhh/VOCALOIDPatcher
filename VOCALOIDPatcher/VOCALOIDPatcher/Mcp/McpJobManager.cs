using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.Mcp;

internal static class McpJobManager
{
    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string ClientId { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public BridgeJobStatus Status { get; set; }
        public double Progress { get; set; }
        public JsonElement? Result { get; set; }
        public BridgeError? Error { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    public static JobInfo Start(
        string kind,
        string clientId,
        Func<CancellationToken, Action<double>, Task<object?>> action)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entry = new Entry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            ClientId = clientId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = BridgeJobStatus.Queued,
        };
        Entries[entry.Id] = entry;
        McpEventHub.Publish("job_progress", data: new { job_id = entry.Id, kind, status = entry.Status.ToString(), progress = 0.0 });
        _ = RunAsync(entry, action);
        return Snapshot(entry);
    }

    public static IReadOnlyList<JobInfo> List(string clientId)
        => Entries.Values
            .Where(entry => string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .Take(200)
            .Select(Snapshot)
            .ToArray();

    public static JobInfo? Get(string id, string clientId)
        => Entries.TryGetValue(id, out Entry? entry)
           && string.Equals(entry.ClientId, clientId, StringComparison.Ordinal)
            ? Snapshot(entry)
            : null;

    public static bool Cancel(string id, string clientId)
    {
        if (!Entries.TryGetValue(id, out Entry? entry)
            || !string.Equals(entry.ClientId, clientId, StringComparison.Ordinal)
            || entry.Status is BridgeJobStatus.Succeeded or BridgeJobStatus.Failed or BridgeJobStatus.Cancelled or BridgeJobStatus.CompletedAfterCancel)
            return false;
        entry.Cancellation.Cancel();
        entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
        McpEventHub.Publish("job_progress", data: new { job_id = entry.Id, status = entry.Status.ToString(), cancel_requested = true });
        return true;
    }

    private static async Task RunAsync(
        Entry entry,
        Func<CancellationToken, Action<double>, Task<object?>> action)
    {
        try
        {
            entry.Status = BridgeJobStatus.Running;
            entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
            McpEventHub.Publish("job_progress", data: new { job_id = entry.Id, status = entry.Status.ToString(), progress = entry.Progress });
            object? result = await action(entry.Cancellation.Token, progress =>
            {
                entry.Progress = Math.Clamp(progress, 0.0, 1.0);
                entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                McpEventHub.Publish("job_progress", data: new { job_id = entry.Id, status = entry.Status.ToString(), progress = entry.Progress });
            }).ConfigureAwait(false);
            entry.Result = JsonSerializer.SerializeToElement(result, BridgeProtocol.JsonOptions);
            entry.Progress = 1.0;
            entry.Status = entry.Cancellation.IsCancellationRequested
                ? BridgeJobStatus.CompletedAfterCancel
                : BridgeJobStatus.Succeeded;
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Status = BridgeJobStatus.Cancelled;
            entry.Error = new BridgeError("cancelled", "The job was cancelled.");
        }
        catch (Exception exception)
        {
            entry.Status = BridgeJobStatus.Failed;
            entry.Error = new BridgeError("job_failed", exception.Message, true);
        }
        finally
        {
            entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
            McpEventHub.Publish("job_progress", data: new { job_id = entry.Id, status = entry.Status.ToString(), progress = entry.Progress });
        }
    }

    private static JobInfo Snapshot(Entry entry)
        => new(
            entry.Id,
            entry.Kind,
            entry.Status,
            entry.Progress,
            entry.CreatedAtUtc,
            entry.UpdatedAtUtc,
            entry.Result,
            entry.Error,
            entry.Cancellation.IsCancellationRequested);
}
