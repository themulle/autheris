namespace Autheris.Domain.Model;

using System.Collections.Generic;

/// <summary>
/// One raw <c>meta.golden_queries</c> entry as read from a dbt manifest node: values are untrusted and not yet validated or sanitised.
/// <see cref="ShapeReason"/> is a reason code when the entry could not be read at all (wrong JSON shape or an oversized value).
/// </summary>
public sealed record DbtGoldenQueryRawEntry(
    int Index,
    string? Title,
    string? Description,
    string? Query,
    string? VariablesJson,
    string? ShapeReason = null);

/// <summary>
/// The <c>meta.golden_queries</c> declaration of a dbt model or source. <see cref="Invalid"/> marks a declaration that is not a list;
/// it is skipped as a whole and the stored entries stay untouched. <see cref="HasDeclaration"/> distinguishes an explicit
/// <c>meta.golden_queries</c> declaration (including <c>[]</c> or <c>null</c>) from an omitted declaration. At most the first entries
/// up to the per-dataset cap are retained; <see cref="TotalEntries"/> carries the declared count.
/// </summary>
public sealed record DbtGoldenQueryMeta(
    bool Invalid,
    int TotalEntries,
    IReadOnlyList<DbtGoldenQueryRawEntry> Entries,
    bool HasDeclaration = true)
{
    /// <summary>Raw entries retained per dataset by the parser (the validator applies the cap of 20).</summary>
    public const int MaxRetainedEntries = 21;

    /// <summary>Represents an omitted <c>meta.golden_queries</c> declaration.</summary>
    public static DbtGoldenQueryMeta None { get; } = new(false, 0, [], HasDeclaration: false);

    /// <summary>Represents an explicit empty or <c>null</c> <c>meta.golden_queries</c> declaration.</summary>
    public static DbtGoldenQueryMeta Empty { get; } = new(false, 0, [], HasDeclaration: true);
}
