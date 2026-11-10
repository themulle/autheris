namespace Autheris.Application.Sql;

using System.Collections.Generic;
using System.Collections.Immutable;

public sealed record GovernedResultInfo(
    IReadOnlyList<SqlResultColumn> Columns,
    ImmutableArray<bool> MaskedColumns,
    IReadOnlyList<Autheris.Domain.Model.WebSqlIndexHint>? IndexHints = null);
