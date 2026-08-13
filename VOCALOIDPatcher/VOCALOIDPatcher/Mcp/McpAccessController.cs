using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;

namespace VOCALOIDPatcher.Mcp;

internal static class McpAccessController
{
    private sealed record ObservedClient(BridgeClientInfo Info, DateTimeOffset LastSeenUtc);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ObservedClient> Clients = new(StringComparer.Ordinal);
    private static readonly HashSet<string> GrantedClients = new(StringComparer.Ordinal);
    private static readonly WriteLeaseManager Lease = new();

    public static void Observe(BridgeClientInfo client)
    {
        lock (Gate)
            Clients[client.Id] = new ObservedClient(client, DateTimeOffset.UtcNow);
    }

    public static object GetStatus()
    {
        lock (Gate)
            return new
            {
                lease = Lease.Snapshot(DateTimeOffset.UtcNow),
                clients = Clients.Values.Select(client => new
                {
                    id = client.Info.Id,
                    name = client.Info.Name,
                    version = client.Info.Version,
                    transport = client.Info.Transport,
                    write_granted = GrantedClients.Contains(LeaseClientId(client.Info)),
                    last_seen_utc = client.LastSeenUtc,
                }).ToArray(),
            };
    }

    public static IReadOnlyList<string> ClientSummaries()
    {
        lock (Gate)
            return Clients.Values
                .OrderBy(client => client.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(client => $"{client.Info.Name} {client.Info.Version ?? "?"} · {client.Info.Transport}")
                .ToArray();
    }

    public static bool TryAcquire(BridgeClientInfo client, out BridgeError? error)
    {
        if (Lease.TryAcquire(LeaseClientId(client), client.Name, DateTimeOffset.UtcNow, out string? heldBy))
        {
            error = null;
            return true;
        }

        error = new BridgeError("write_lease_held", $"The write lease is held by {heldBy ?? "another client"}.", true);
        return false;
    }

    public static bool Release(BridgeClientInfo client)
    {
        bool released = Lease.Release(LeaseClientId(client));
        if (released)
            McpEventHub.Publish("write_lease_revoked", data: new { reason = "released" });
        return released;
    }

    public static bool BeginJob(BridgeClientInfo client)
        => Lease.BeginJob(LeaseClientId(client), DateTimeOffset.UtcNow);

    public static void EndJob(BridgeClientInfo client)
        => Lease.EndJob(LeaseClientId(client), DateTimeOffset.UtcNow);

    public static void RevokeAll()
    {
        Lease.Revoke();
        lock (Gate)
        {
            GrantedClients.Clear();
            Clients.Clear();
        }
        McpEventHub.Publish("write_lease_revoked", data: new { reason = "revoked" });
    }

    public static bool AuthorizeWrite(BridgeClientInfo client, string action, bool alwaysConfirm, out BridgeError? error)
    {
        string leaseClientId = LeaseClientId(client);
        if (!Lease.Touch(leaseClientId, DateTimeOffset.UtcNow))
        {
            error = new BridgeError("write_lease_held", "Acquire the write lease with v6_session before modifying the project.", true);
            return false;
        }

        if (!Settings.McpConfirmWrites)
        {
            lock (Gate)
                GrantedClients.Add(leaseClientId);
            error = null;
            return true;
        }

        bool requiresGrant;
        lock (Gate)
            requiresGrant = !GrantedClients.Contains(leaseClientId);
        if (!requiresGrant && !alwaysConfirm)
        {
            error = null;
            return true;
        }

        string message = TranslationManager.Tr(
            alwaysConfirm ? "VOCALOIDPatcher_Mcp_ConfirmDangerousWrite" : "VOCALOIDPatcher_Mcp_ConfirmWrite",
            client.Name,
            client.Version ?? "?",
            client.Transport,
            action);
        var result = System.Windows.MessageBox.Show(
            message,
            TranslationManager.Tr("VOCALOIDPatcher_Mcp_ConfirmationTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            error = new BridgeError("confirmation_denied", "The user denied the operation in VOCALOID.");
            return false;
        }

        lock (Gate)
            GrantedClients.Add(leaseClientId);
        error = null;
        return true;
    }

    private static string LeaseClientId(BridgeClientInfo client)
        => $"{client.Transport.ToLowerInvariant()}:{client.Name}:{client.Version ?? string.Empty}";

    public static bool TryResolvePath(string path, out string fullPath, out BridgeError? error)
    {
        var roots = new List<string>(Settings.McpAllowedDirectories);
        try
        {
            string? current = App.Shared?.Document?.DocumentUri?.LocalPath;
            string? directory = string.IsNullOrWhiteSpace(current) ? null : Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(directory))
                roots.Add(directory);
        }
        catch
        {
        }

        var allowlist = new PathAllowlist(roots);
        if (allowlist.TryResolve(path, out fullPath, out string? reason))
        {
            error = null;
            return true;
        }

        error = new BridgeError("path_not_allowed", reason ?? "The path is not allowed.");
        return false;
    }
}
