namespace Autheris.Application.Sql.Masking;

using System;
using System.Collections.Generic;
using System.Security;
using Autheris.Application.Policy;
using TrinoSqlEngine.Governance;

/// <summary>
/// <see cref="IColumnMaskProvider"/> over the typed masks of access decisions (WP-X0f): the compiler path reads masks from the
/// decision instead of from the catalog. A column without a typed mask has no mask here, and <see cref="GetMask"/> never invents one.
/// </summary>
public sealed class DecisionColumnMaskProvider : IColumnMaskProvider
{
    private readonly IReadOnlyDictionary<TableIdentity, ColumnMaskPolicy> _policies;

    public DecisionColumnMaskProvider(IReadOnlyDictionary<TableIdentity, ColumnMaskPolicy> policies)
    {
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    public bool HasMask(TableIdentity table, string column) =>
        _policies.TryGetValue(table, out var policy) && policy.HasMask(column);

    public MaskSpec GetMask(TableIdentity table, string column) =>
        _policies.TryGetValue(table, out var policy) && policy.TryGetSpec(column, out var spec)
            ? spec
            : throw new SecurityException("No typed mask exists for the requested column (fail-closed).");
}
