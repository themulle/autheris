namespace Autheris.Application.Governance.Interfaces;

using Autheris.Domain.Common;

/// <summary>Issue #111 WP-6: what a dbt tag import did for one table (counts only, never tag values).</summary>
public sealed record DatasetTagImportResult(bool Changed, int InvalidDropped, int OverflowDropped, bool Failed)
{
    public static DatasetTagImportResult Unchanged { get; } = new(false, 0, 0, false);
}

/// <summary>
/// Issue #111 (A-2, A-3): the dbt model-level tags of a table become dbt-source dataset tags. Invalid tags are dropped one by one, the total cap
/// across sources is enforced from the strict before-state (excess dbt tags are dropped in sorted order), admin tags are never touched, and one
/// <c>CATALOG_TAGS_CHANGED</c> audit entry is written per table only when the set changed.
/// </summary>
public interface IDatasetTagImportService
{
    Task<DatasetTagImportResult> ImportDbtTagsAsync(TableIdentifier table, IEnumerable<string?> rawTags, CancellationToken ct);
}
