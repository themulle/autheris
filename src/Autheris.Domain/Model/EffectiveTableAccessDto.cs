namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Issue #35: Self-service effective access explanation for the authenticated caller.
/// </summary>
/// <param name="RestrictedScope">the caller holds a restricted scope, so catalog-sensitive columns need a scope-specific<c>Clear</c> (no scope name is given).</param>
/// <param name="ScopeClearRequiredColumns">the non-<c>Deny</c> columns the restriction masks (only set for a restricted caller; never lists denied columns).</param>
public sealed record EffectiveTableAccessDto(
    string Table,
    string Decision,
    IReadOnlyList<string> AllowedColumns,
    IReadOnlyList<string> MaskedColumns,
    IReadOnlyList<string> DeniedColumns,
    IReadOnlyList<string> ActiveRowFilters,
    string? ConsentDescription,
    DateTimeOffset? ValidTo,
    bool RestrictedScope = false,
    IReadOnlyList<string>? ScopeClearRequiredColumns = null
)
{
    /// <summary>INC1 decision 9: distinct owner-authored descriptions of the grants that contribute to an ALLOWED decision (most specific grantee first, at most 10). Empty otherwise.</summary>
    public IReadOnlyList<string> ConsentDescriptions { get; init; } = [];

    /// <summary>travels with the data so a client can treat<see cref="ConsentDescription"/> as owner-authored text and never as an instruction.</summary>
    public string ConsentDescriptionOrigin => "owner-authored";
}
