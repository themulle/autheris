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

/// <summary>Persisted catalog settings of one dataset: tags of all sources, visibility tier, and privacy overrides.</summary>
public sealed record DatasetCatalogSettings(
    IReadOnlyList<DatasetTag> Tags,
    DatasetVisibility Visibility,
    int? MinGroupSize = null,
    bool? NonPersonal = null,
    bool DiscoveryHidden = false,
    bool SamplesDisabled = false,
    IReadOnlyList<string>? HiddenColumns = null);

/// <summary>Result of a catalog settings write: before and after state of one dataset (audit input).</summary>
public sealed record DatasetCatalogChange(TableIdentifier Table, DatasetCatalogSettings Before, DatasetCatalogSettings After)
{
    public bool Changed =>
        Before.Visibility != After.Visibility ||
        Before.MinGroupSize != After.MinGroupSize ||
        Before.NonPersonal != After.NonPersonal ||
        Before.DiscoveryHidden != After.DiscoveryHidden ||
        Before.SamplesDisabled != After.SamplesDisabled ||
        !SequenceEqual(Before.HiddenColumns, After.HiddenColumns) ||
        !Before.Tags.OrderBy(t => t.Tag, StringComparer.Ordinal).ThenBy(t => t.Source)
            .SequenceEqual(After.Tags.OrderBy(t => t.Tag, StringComparer.Ordinal).ThenBy(t => t.Source));

    private static bool SequenceEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a ?? []).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual((b ?? []).OrderBy(x => x, StringComparer.Ordinal));
}

/// <summary>Issue #111 (A-9): number of tables per visibility tier (inactive tables with a settings row may be included); feeds the gauge only.</summary>
public sealed record DatasetVisibilityCounts(long Hidden, long Summary, long Full);
