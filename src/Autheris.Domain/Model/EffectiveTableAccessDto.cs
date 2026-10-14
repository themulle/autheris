namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Issue #35: Self-service effective access explanation for the authenticated caller.
/// </summary>
public sealed record EffectiveTableAccessDto(
    string Table,
    string Decision,
    IReadOnlyList<string> AllowedColumns,
    IReadOnlyList<string> MaskedColumns,
    IReadOnlyList<string> DeniedColumns,
    IReadOnlyList<string> ActiveRowFilters,
    string? ConsentDescription,
    DateTimeOffset? ValidTo
);
