namespace Autheris.Application.Common.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Persists and queries tenant-scoped data classification vocabulary, classification mask rules,
/// column classification assignments, and per-column mask overrides.
/// </summary>
public interface IClassificationRepository
{
    /// <summary>
    /// Returns classifications visible to <paramref name="tenantScope"/> (<c>*</c> and matching tenant rows,
    /// or strictly <c>*</c> when <paramref name="tenantScope"/> is <c>null</c> or <c>*</c>).
    /// </summary>
    Task<IReadOnlyList<DataClassification>> GetClassificationsAsync(
        string? tenantScope,
        bool includeInactive = false,
        CancellationToken ct = default);

    Task<DataClassification?> GetClassificationAsync(
        string scope,
        string code,
        CancellationToken ct = default);

    Task<DataClassification> UpsertClassificationAsync(
        DataClassification classification,
        CancellationToken ct = default);

    Task<IReadOnlyList<ClassificationMaskRule>> GetClassificationMaskRulesAsync(
        string? tenantScope,
        CancellationToken ct = default);

    Task<ClassificationMaskRule?> GetClassificationMaskRuleAsync(
        string scope,
        string code,
        CancellationToken ct = default);

    Task<ClassificationMaskRule> UpsertClassificationMaskRuleAsync(
        ClassificationMaskRule rule,
        CancellationToken ct = default);

    Task<IReadOnlyList<ColumnClassification>> GetColumnClassificationsAsync(
        string tableColumnId,
        string? tenantScope = null,
        CancellationToken ct = default);

    Task AssignColumnClassificationAsync(
        ColumnClassification assignment,
        CancellationToken ct = default);

    Task<bool> RemoveColumnClassificationAsync(
        string scope,
        string tableColumnId,
        string code,
        CancellationToken ct = default);

    Task<IReadOnlyList<ColumnMaskOverride>> GetColumnMaskOverridesAsync(
        string tableColumnId,
        string? tenantScope = null,
        CancellationToken ct = default);

    /// <summary>
    /// Issue #1043: the classifications of the columns <paramref name="tableColumnIds"/> in one read (instead of one read per column),
    /// keyed by table column id, each list with the rows and order of <see cref="GetColumnClassificationsAsync"/> for that column.
    /// A column without rows has no entry. The default reads column by column.
    /// </summary>
    async Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>>> GetColumnClassificationsBatchAsync(
        IReadOnlyCollection<string> tableColumnIds,
        string? tenantScope = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableColumnIds);
        var result = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in tableColumnIds)
        {
            var rows = await GetColumnClassificationsAsync(id, tenantScope, ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                result[id] = rows;
            }
        }

        return result;
    }

    /// <summary>
    /// Issue #1043: the mask overrides of the columns <paramref name="tableColumnIds"/> in one read (instead of one read per column),
    /// keyed by table column id, each list with the rows and order of <see cref="GetColumnMaskOverridesAsync"/> for that column.
    /// A column without rows has no entry. The default reads column by column.
    /// </summary>
    async Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>>> GetColumnMaskOverridesBatchAsync(
        IReadOnlyCollection<string> tableColumnIds,
        string? tenantScope = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableColumnIds);
        var result = new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in tableColumnIds)
        {
            var rows = await GetColumnMaskOverridesAsync(id, tenantScope, ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                result[id] = rows;
            }
        }

        return result;
    }

    Task<ColumnMaskOverride> UpsertColumnMaskOverrideAsync(
        ColumnMaskOverride maskOverride,
        CancellationToken ct = default);

    Task<bool> DeleteColumnMaskOverrideAsync(
        string scope,
        string tableColumnId,
        string? code,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the table that owns <paramref name="tableColumnId"/>, or <c>null</c> when the column is unknown.
    /// Callers treat <c>null</c> as not found (fail closed).
    /// </summary>
    Task<TableIdentifier?> GetTableIdentifierByColumnIdAsync(
        string tableColumnId,
        CancellationToken ct = default)
        => Task.FromResult<TableIdentifier?>(null);

    /// <summary>
    /// True when <paramref name="code"/> of <paramref name="scope"/> is still referenced by a column classification (for <c>*</c>:
    /// in any tenant; for a tenant scope: global and that tenant's rows). A store failure throws; callers refuse the change (fail closed).
    /// </summary>
    Task<bool> IsClassificationReferencedAsync(
        string scope,
        string code,
        CancellationToken ct = default);
}
