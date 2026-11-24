namespace Autheris.Application.Mcp.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Candidate golden query list returned by <see cref="IGoldenQueryService.GetCandidateQueriesAsync"/>; <c>default</c> marks an
/// unconfigured test double so callers fall back safely.
/// </summary>
public readonly record struct GoldenQueryCandidateSet(
    bool HasKnownCandidates,
    IReadOnlyList<GoldenQuery> Queries)
{
    public static GoldenQueryCandidateSet Unknown => default;
    public static GoldenQueryCandidateSet Known(IReadOnlyList<GoldenQuery>? queries) => new(true, queries ?? []);
}

public interface IGoldenQueryService
{
    void RegisterGoldenQuery(GoldenQuery query);

    /// <summary>
    /// MCP-15: <paramref name="visible"/> is applied before the per-request cap so
    /// that entries the caller may not see cannot crowd out visible ones. A <c>null</c> predicate keeps the cap; a predicate
    /// that throws excludes that entry.
    /// </summary>
    ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(string? domain, string? tableName, Func<GoldenQuery, bool>? visible, CancellationToken ct);

    /// <summary>
    /// Serves configured entries plus the tenant's stored entries (dbt <c>meta.golden_queries</c>). Caller visibility from
    /// <paramref name="context"/> is applied before the per-request cap: an entry is returned only when its owning and referenced datasets
    /// are visible and it mentions no hidden column. Configured entries win on an equal id, except against a persisted config copy.
    /// </summary>
    ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(GoldenQueryReadContext context, string? domain, string? tableName, CancellationToken ct);

    /// <summary>
    /// Caller-independent candidate entries for (<paramref name="tenant"/>, <paramref name="domain"/>, <paramref name="tableName"/>) used
    /// only to scope <c>GoldenQueryCallerView</c> relation walks; callers must still filter via
    /// <see cref="GetGoldenQueriesAsync(GoldenQueryReadContext, string?, string?, CancellationToken)"/>.
    /// </summary>
    ValueTask<GoldenQueryCandidateSet> GetCandidateQueriesAsync(TenantId? tenant, string? domain, string? tableName, CancellationToken ct = default)
        => ValueTask.FromResult(GoldenQueryCandidateSet.Unknown);

    ValueTask<GoldenQuery?> GetByIdAsync(string id, CancellationToken ct = default);
}
