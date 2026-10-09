using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Olap;

public sealed record OlapTableSource(
    TableIdentifier Table,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> GovernedRows,
    TableMetadata Metadata,
    string? StagingTableName = null,
    IReadOnlySet<string>? MaskedColumns = null);

public sealed record OlapQueryRequest(
    string Sql,
    IReadOnlyList<OlapTableSource> Sources,
    int? Limit = null);

public sealed record OlapQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int TotalRowCount,
    TimeSpan ExecutionDuration);

public sealed record GeneratedOlapQuery(string Sql);

public interface IDuckDbOlapEngine : IDisposable
{
    Task<OlapQueryResult> ExecuteOlapQueryAsync(
        OlapQueryRequest request,
        CancellationToken ct = default);

    Task<OlapQueryResult> ExecuteGeneratedAsync(
        GeneratedOlapQuery query,
        IReadOnlyList<OlapTableSource> sources,
        int? limit = null,
        CancellationToken ct = default);
}
