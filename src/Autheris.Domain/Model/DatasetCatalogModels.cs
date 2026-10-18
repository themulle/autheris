namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

/// <summary>Issue #111: where a persisted dataset tag came from. Admin rows are never replaced by a dbt import.</summary>
public enum DatasetTagSource
{
    Admin = 0,
    Dbt = 1
}

public sealed record DatasetTag(string Tag, DatasetTagSource Source);

/// <summary>Persisted catalog settings of one dataset: tags of all sources and the visibility tier (missing row = Hidden).</summary>
public sealed record DatasetCatalogSettings(IReadOnlyList<DatasetTag> Tags, DatasetVisibility Visibility);

/// <summary>Result of a catalog settings write: before and after state of one dataset (audit input).</summary>
public sealed record DatasetCatalogChange(TableIdentifier Table, DatasetCatalogSettings Before, DatasetCatalogSettings After)
{
    public bool Changed =>
        Before.Visibility != After.Visibility ||
        !Before.Tags.OrderBy(t => t.Tag, StringComparer.Ordinal).ThenBy(t => t.Source)
            .SequenceEqual(After.Tags.OrderBy(t => t.Tag, StringComparer.Ordinal).ThenBy(t => t.Source));
}

/// <summary>Issue #111 (A-9): number of tables per visibility tier (inactive tables with a settings row may be included); feeds the gauge only.</summary>
public sealed record DatasetVisibilityCounts(long Hidden, long Summary, long Full);
