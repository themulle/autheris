namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Options;

/// <summary>
/// PLAN-SEC-115 A-2/A-7: startup snapshot of the read-path switches (the switch values are immutable; only the bounded set of dropped tool names is filled during startup registration). Built once from the bound options; never reloads
/// (no <c>IOptionsMonitor</c>), so tools/list and the mapped routes can never disagree within one process.
/// </summary>
public sealed class ReadPathSwitches : IReadPathSwitches
{
    /// <summary>Upper bound of names remembered as "dropped at registration" (startup-only, never request-driven).</summary>
    public const int MaxDroppedNames = 4096;

    /// <summary>Reason text for a name listed in <c>Mcp:DisabledTools</c>.</summary>
    public const string ListedReason = "DisabledTools";

    private readonly bool _catalog;
    private readonly bool _query;
    private readonly bool _api;
    private readonly bool _access;
    private readonly FrozenSet<string> _disabledTools;
    private readonly ConcurrentDictionary<string, byte> _dropped = new(StringComparer.OrdinalIgnoreCase);

    private ReadPathSwitches(bool restData, bool bulkStream, McpToolGroupOptions tools, IEnumerable<string> disabled)
    {
        RestDataEndpointEnabled = restData;
        BulkStreamEnabled = bulkStream;
        _catalog = tools.Catalog;
        _query = tools.Query;
        _api = tools.Api;
        _access = tools.Access;
        _disabledTools = disabled.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public static ReadPathSwitches From(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mcp = options.Mcp ?? new McpOptions();
        var disabled = (mcp.DisabledTools ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToArray();
        bool bulkStream = (options.BulkStream?.Enabled ?? true) && (options.RestApi?.BulkStream?.Enabled ?? true);
        return new ReadPathSwitches(
            options.RestApi?.DataEndpoint?.Enabled ?? true,
            bulkStream,
            mcp.Tools ?? new McpToolGroupOptions(),
            disabled);
    }

    public bool RestDataEndpointEnabled { get; }
    public bool BulkStreamEnabled { get; }

    /// <summary>Number of remembered dropped names (test and diagnostics aid).</summary>
    public int DroppedCount => _dropped.Count;

    public bool IsGroupEnabled(McpToolGroup group) => group switch
    {
        McpToolGroup.Catalog => _catalog,
        McpToolGroup.Query => _query,
        McpToolGroup.Api => _api,
        McpToolGroup.Access => _access,
        _ => true
    };

    public bool IsToolEnabled(string toolName)
    {
        if (toolName is null) return false;
        var name = toolName.Trim();
        return !_disabledTools.Contains(name) && IsGroupEnabled(McpToolGroups.Of(name));
    }

    public string? DisabledReason(string toolName)
    {
        if (toolName is null) return null;
        var name = toolName.Trim();
        if (_disabledTools.Contains(name)) return ListedReason;

        var group = McpToolGroups.Of(name);
        if (McpToolGroups.IsKnownBuiltIn(name))
        {
            return IsGroupEnabled(group) ? null : group.ToString();
        }

        // A dynamic (non-table) name only has a reason when the registry actually dropped it: random unknown names get none.
        return _dropped.ContainsKey(name) ? group.ToString() : null;
    }

    public bool IsResourceEnabled(string resourceUri)
    {
        if (resourceUri is null) return true;
        // The dataset resource is rendered by describe_dataset, so it also follows that tool's DisabledTools entry.
        if (resourceUri.StartsWith("autheris://datasets/", StringComparison.OrdinalIgnoreCase))
        {
            return _catalog && IsToolEnabled(McpDatasetTools.DescribeDataset);
        }

        if (resourceUri.StartsWith("autheris://catalog/", StringComparison.OrdinalIgnoreCase))
        {
            return _catalog;
        }

        if (resourceUri.StartsWith("autheris://governance/", StringComparison.OrdinalIgnoreCase)) return _access;
        if (resourceUri.StartsWith("autheris://api/", StringComparison.OrdinalIgnoreCase)) return _api;
        return true;
    }

    public bool IsPromptEnabled(string promptName)
    {
        if (string.Equals(promptName, "explore_dataset", StringComparison.OrdinalIgnoreCase)) return _catalog && _query;
        if (string.Equals(promptName, "audit_access_compliance", StringComparison.OrdinalIgnoreCase)) return _access && _catalog;
        return true;
    }

    /// <remarks>Best-effort bound: the count check and the add are not atomic, which is acceptable because registration happens at startup only.</remarks>
    public void RecordDropped(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName) || _dropped.Count >= MaxDroppedNames) return;
        _dropped.TryAdd(toolName.Trim(), 0);
    }
}
