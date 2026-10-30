namespace Autheris.Domain.Model;

using System.Collections.Generic;

/// <summary>
/// A declared index as a catalog caller sees it (I404-SC-L3): columns and uniqueness only. The operator-chosen index name is never
/// part of any caller-facing output, because it is free text that can embed the names of columns the caller must not see.
/// </summary>
/// <param name="Directions">Issue #390: per-column directions, parallel to <paramref name="Columns"/>; null when every column is ascending.</param>
public sealed record DatasetIndexInfo(
    IReadOnlyList<string> Columns,
    bool Unique,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<KeyColumnDirection>? Directions = null);
