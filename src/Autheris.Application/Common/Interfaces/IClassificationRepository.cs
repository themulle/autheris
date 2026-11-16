namespace Autheris.Application.Common.Interfaces;

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

    Task<ColumnMaskOverride> UpsertColumnMaskOverrideAsync(
        ColumnMaskOverride maskOverride,
        CancellationToken ct = default);

    Task<bool> DeleteColumnMaskOverrideAsync(
        string scope,
        string tableColumnId,
        string? code,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessAsync(
        string tenantId,
        Guid consentId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessBatchAsync(
        string tenantId,
        IReadOnlyList<Guid> consentIds,
        CancellationToken ct = default);

    Task<ConsentClassificationAccess> UpsertConsentClassificationAccessAsync(
        ConsentClassificationAccess entry,
        CancellationToken ct = default);
}
