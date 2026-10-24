namespace Autheris.Application.Mcp.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IGoldenQueryService
{
    void RegisterGoldenQuery(GoldenQuery query);
    ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(string? domain = null, string? tableName = null, CancellationToken ct = default);

    /// <summary>
    /// MCP-15: like the three-argument overload, but <paramref name="visible"/> is applied before the per-request cap so
    /// that entries the caller may not see cannot crowd out visible ones. A <c>null</c> predicate keeps the cap; a predicate
    /// that throws excludes that entry.
    /// </summary>
    ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(string? domain, string? tableName, Func<GoldenQuery, bool>? visible, CancellationToken ct);

    ValueTask<GoldenQuery?> GetByIdAsync(string id, CancellationToken ct = default);
}
