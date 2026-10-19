namespace Autheris.Application.Mcp.Interfaces;

using Autheris.Application.Mcp.Services;

/// <summary>
/// PLAN-SEC-115: the startup-only read-path switches (REST data endpoint, MCP tool groups, disabled tools). An immutable snapshot of
/// the configuration; no member takes request-derived data, so an agent can never select or widen a switch.
/// </summary>
public interface IReadPathSwitches
{
    /// <summary>False: the REST data family (<c>/api/v1/data/*</c>, change-token, changes, sample-rows) is not mapped and not reachable in-process.</summary>
    bool RestDataEndpointEnabled { get; }

    /// <summary>Issue #222: Dedicated switch for bulk keyset streaming endpoint (/stream, /bulk).</summary>
    bool BulkStreamEnabled { get; }

    /// <summary>Issue #240: Dedicated switch for query analysis endpoint (POST /api/v1/governance/queries/analyze).</summary>
    bool QueryAnalysisEnabled { get; }

    bool IsGroupEnabled(McpToolGroup group);

    /// <summary>False when the tool's group is off or the name is listed in <c>Mcp:DisabledTools</c>. Unknown (dynamic) names follow <see cref="McpToolGroup.Query"/>.</summary>
    bool IsToolEnabled(string toolName);

    /// <summary>Internal audit text only (never shown to a caller); null for enabled tools and for names that were never registered.</summary>
    string? DisabledReason(string toolName);

    /// <summary>Resource gate (A-6). An unknown URI is enabled so the unknown branch stays the single not-found producer.</summary>
    bool IsResourceEnabled(string resourceUri);

    bool IsPromptEnabled(string promptName);

    /// <summary>Called by the registry at startup for a tool it refused to register (bounded set, feeds <see cref="DisabledReason"/>).</summary>
    void RecordDropped(string toolName);
}
