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
    TableMetadata Metadata);

public sealed record OlapQueryRequest(
    string Sql,
    IReadOnlyList<OlapTableSource> Sources,
    int? Limit = null);

public sealed record OlapQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int TotalRowCount,
    TimeSpan ExecutionDuration);

public interface IDuckDbOlapEngine
{
    Task<OlapQueryResult> ExecuteOlapQueryAsync(
        OlapQueryRequest request,
        CancellationToken ct = default);
}
