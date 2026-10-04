using System.Collections.Generic;
using Autheris.Domain.Common;

namespace Autheris.Application.Connectors.Pushdown;

public sealed record PushdownExecutionPlan(
    TableIdentifier Table,
    IReadOnlyList<string> ProjectedColumns,
    string? PushedFilterSql,
    IReadOnlyDictionary<string, object?> PushedArguments,
    int Limit,
    int Offset,
    bool RequiresMemoryFilter,
    bool RequiresMemoryMasking,
    string? ResidualFilterSql);
