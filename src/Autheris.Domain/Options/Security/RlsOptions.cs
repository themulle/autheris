using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

public sealed class CasbinOptions
{
    public bool Enabled { get; init; } = false;
    public bool EnforceInQueryPipeline { get; init; } = true;
    public string? ModelPath { get; init; }
    public string? PolicyPath { get; init; }
    public bool WatchPolicyFile { get; init; } = true;
}

/// <summary>
/// F-SEC-04: Relationship-Based Access Control (ReBAC) Options.
/// Configures Zanzibar/OpenFGA graph traversal depth, caching, and batch DataLoader settings.
/// </summary>
public sealed class RebacOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxTraversalDepth { get; init; } = 10;
    public int CacheTtlSeconds { get; init; } = 60;
    public int MaxCachedDecisions { get; init; } = 50000;
    [Range(0, 5000)] public int GenerationCacheMilliseconds { get; init; } = 1000;
    public bool EnforceOnStreaming { get; init; } = false;

    /// <summary>
    /// POL-6: also require the ReBAC relation <c>can_query</c> on OData, GraphQL, WebSQL and stored procedures.
    /// Off by default: the evaluator denies every table without tuples, so enable it only once tuples are maintained.
    /// The unified PDP (MCP-RAG, DuckDB OLAP) checks ReBAC whenever <see cref="Enabled"/> is true.
    /// </summary>
    public bool EnforceOnQueryPaths { get; init; } = false;

    /// <summary>
    /// SR15-23: When true, users with the <c>viewer</c> relation automatically inherit <c>can_query</c>.
    /// Defaults to true for backwards compatibility with POL-6 / OLAP / Arrow egress, but can be set to false
    /// to require explicit <c>can_query</c>, <c>editor</c>, or <c>owner</c> relations, preventing existing <c>viewer</c>
    /// tuples from automatically granting full analytical and raw egress capabilities.
    /// </summary>
    public bool InheritCanQueryFromViewer { get; init; } = true;
    public string? OpenFgaApiUrl { get; init; }
    public string? OpenFgaStoreId { get; init; }

    /// <summary>
    /// Initial relationship tuples seeded into the ReBAC store on startup (for Development, PoC, or testing).
    /// </summary>
    public List<RebacTuple> SeedTuples { get; init; } = [];
}

/// <summary>Virtual filters (docs/plans/2026-10-08-virtuelle-filter.md).</summary>
public sealed class VirtualFilterOptions
{
    /// <summary>
    /// A sync that removes more bindings than this (or all of them) is not applied without <c>force</c>: removing a
    /// binding widens what its grantee sees. 0 disables the check.
    /// </summary>
    public int MaxRemovals { get; init; } = 10;

    /// <summary>
    /// How often (seconds) an instance compares the virtual filter generation with the governance database; changes made
    /// on another instance take effect within this time. 0 compares on every access.
    /// </summary>
    public int GenerationCheckSeconds { get; init; } = 5;

    /// <summary>
    /// When enabled, manual changes to virtual filters and access profiles require four-eyes approval by a distinct approver.
    /// </summary>
    public bool RequireApproval { get; init; } = false;

    /// <summary>F-GOV-14: Virtual Filters on Web-API tables in WebSQL cross-source queries.</summary>
    public VirtualFilterFederationOptions Federation { get; init; } = new();

    /// <summary>
    /// SR15-11: Optional allowlist of table names or qualified identifiers allowed in virtual filter subqueries.
    /// When non-empty, any table referenced in a virtual filter subquery must be present in this list.
    /// </summary>
    public IReadOnlyList<string>? AllowedReferenceTables { get; init; } = null;

    /// <summary>
    /// SR15-15: Sliding time window in minutes across which removals and relaxations accumulate toward MaxRemovals.
    /// Default is 60 minutes.
    /// </summary>
    public int MaxRemovalsWindowMinutes { get; init; } = 60;

    /// <summary>
    /// HMAC-SHA256 secret used to validate GitHub webhook triggers for virtual filter GitOps sync (X-Hub-Signature-256).
    /// </summary>
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Configured Git reference (e.g. "refs/heads/main" or "refs/tags/v1.0") to accept push events for.
    /// Default is null (accepts any push when not configured).
    /// </summary>
    public string? GitRef { get; init; }
}

/// <summary>
/// Row-Level Security row filter generation strategy options.
/// </summary>
public sealed class RowFilterOptions
{
    public RowFilterSubqueryStrategy SubqueryStrategy { get; set; } = RowFilterSubqueryStrategy.Exists;
}
