using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

namespace Autheris.Application.Sql.Tree;

/// <summary>
/// What a caller may learn about one read table: fixed-text row-filter descriptions only. <see cref="RowFilterApplied"/>
/// means "a row filter defined by the data owner or consent applies to you", not "all rows are returned"; tenant
/// isolation, virtual and mandatory filters are intentionally not reported.
/// </summary>
public sealed record DatasetAccessFacts(TableIdentifier Table, IReadOnlyList<string> Path, IReadOnlyList<string> RowFilterDescriptions)
{
    public bool RowFilterApplied => RowFilterDescriptions.Count > 0;

    public static DatasetAccessFacts From(TableIdentifier table, IReadOnlyList<string> path, TableAccessDecision decision)
        => new(table, path, RowFilterDisclosure.Describe(decision));
}
