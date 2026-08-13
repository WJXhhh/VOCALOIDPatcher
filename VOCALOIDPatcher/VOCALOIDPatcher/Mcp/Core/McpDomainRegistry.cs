using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp.Core;

internal sealed record McpDomainAdapter(
    DomainContract Contract,
    IReadOnlyList<OperationContract> Operations,
    Func<IReadOnlyList<CapabilityStatus>> Capabilities,
    Func<string, WIVSMSequence, string, long, JsonElement, object?> Query,
    Action<WIVSMSequence, JsonElement, bool> Apply,
    Func<string, (bool Creates, bool Deletes)> Classify);

internal static class McpDomainRegistry
{
    private static readonly Dictionary<string, McpDomainAdapter> Domains = new(StringComparer.Ordinal);

    public static void Register(McpDomainAdapter adapter)
    {
        if (!Domains.TryAdd(adapter.Contract.Id, adapter))
            throw new InvalidOperationException($"MCP domain '{adapter.Contract.Id}' is already registered.");
    }

    public static IReadOnlyList<OperationContract> Operations
        => Domains.Values.SelectMany(item => item.Operations).ToArray();

    public static IReadOnlyList<DomainContract> Contracts
        => Domains.Values.Select(item => item.Contract).ToArray();

    public static IReadOnlyList<CapabilityStatus> Capabilities
        => Domains.Values.SelectMany(item => item.Capabilities()).ToArray();

    public static bool TryClassify(string domain, string verb, out bool creates, out bool deletes)
    {
        if (Domains.TryGetValue(domain, out McpDomainAdapter? adapter))
        {
            (creates, deletes) = adapter.Classify(verb);
            return true;
        }
        creates = deletes = false;
        return false;
    }

    public static bool TryQuery(string kind, WIVSMSequence sequence, string projectId, long revision, JsonElement arguments, out object? result)
    {
        foreach (McpDomainAdapter adapter in Domains.Values)
        {
            if (!adapter.Contract.QueryKinds.Contains(kind, StringComparer.Ordinal))
                continue;
            result = adapter.Query(kind, sequence, projectId, revision, arguments);
            return true;
        }
        result = null;
        return false;
    }

    public static bool TryApply(string domain, WIVSMSequence sequence, JsonElement operation, bool execute)
    {
        if (!Domains.TryGetValue(domain, out McpDomainAdapter? adapter))
            return false;
        adapter.Apply(sequence, operation, execute);
        return true;
    }
}
