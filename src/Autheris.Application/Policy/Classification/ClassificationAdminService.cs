namespace Autheris.Application.Policy.Classification;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;

/// <summary>Outcome class of a classification administration use case.</summary>
public enum ClassificationAdminStatus
{
    Ok,
    NotFound,
    Forbidden,
    Invalid,
    Conflict
}

/// <summary>Result of a classification administration use case; failures carry a stable error code.</summary>
public sealed record ClassificationAdminResult<T>(
    ClassificationAdminStatus Status,
    T? Value = default,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool IsOk => Status == ClassificationAdminStatus.Ok;
}

/// <summary>Factories of <see cref="ClassificationAdminResult{T}"/>.</summary>
public static class ClassificationAdminResults
{
    public static ClassificationAdminResult<T> Ok<T>(T value) => new(ClassificationAdminStatus.Ok, value);

    public static ClassificationAdminResult<T> Fail<T>(ClassificationAdminStatus status, string code, string message) =>
        new(status, default, code, message);
}

/// <summary>Stable error codes of the classification administration use cases.</summary>
public static class ClassificationAdminErrorCodes
{
    public const string ScopeForbidden = "CLASSIFICATION_SCOPE_FORBIDDEN";
    public const string AuthorityRequired = "CLASSIFICATION_AUTHORITY_REQUIRED";
    public const string NotFound = "CLASSIFICATION_NOT_FOUND";
    public const string ColumnNotFound = "CLASSIFICATION_COLUMN_NOT_FOUND";
    public const string UnknownClassification = "CLASSIFICATION_UNKNOWN";
    public const string Invalid = "CLASSIFICATION_INVALID";
    public const string InUse = "CLASSIFICATION_IN_USE";
}

/// <summary>Authenticated caller of a classification administration use case.</summary>
/// <param name="Tenant">Tenant resolved for the request.</param>
/// <param name="ActorSid">Authenticated actor identifier (never client supplied).</param>
/// <param name="IsClusterAdmin">Canonical cluster administrator (the only principal that may write <c>*</c> scope).</param>
/// <param name="IsGovernanceAdmin">Governance administrator of <paramref name="Tenant"/>.</param>
public sealed record ClassificationAdminCaller(
    TenantId Tenant,
    string ActorSid,
    bool IsClusterAdmin,
    bool IsGovernanceAdmin,
    bool IsContainerOwnerAuthorized = false);

/// <summary>Whether an administrative write targets the caller's tenant scope or the global <c>*</c> scope.</summary>
public enum ClassificationAdminScope
{
    Tenant,
    Global
}

public sealed record ClassificationDefinitionInput(
    string DisplayName,
    string? Description,
    string? ParentCode,
    int RestrictionRank,
    bool IsActive = true);

public sealed record MaskRuleInput(
    MaskFunction MaskFunction,
    string? ParamsJson,
    string? KeyRef,
    CatalogTextVisibility CatalogText = CatalogTextVisibility.Visible);

/// <summary>Administers the classification vocabulary, mask rules, column assignments and per-column overrides.</summary>
public interface IClassificationAdminService
{
    Task<ClassificationAdminResult<DataClassification>> GetClassificationAsync(
        ClassificationAdminCaller caller, string code, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default);

    Task<ClassificationAdminResult<DataClassification>> UpsertClassificationAsync(
        ClassificationAdminCaller caller, string code, ClassificationDefinitionInput input, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default);

    Task<ClassificationAdminResult<ClassificationMaskRule>> GetMaskRuleAsync(
        ClassificationAdminCaller caller, string code, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default);

    Task<ClassificationAdminResult<ClassificationMaskRule>> UpsertMaskRuleAsync(
        ClassificationAdminCaller caller, string code, MaskRuleInput input, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default);

    Task<ClassificationAdminResult<ColumnClassification>> AssignColumnClassificationAsync(
        ClassificationAdminCaller caller, string columnId, string code, ClassificationAdminScope scope, CancellationToken ct = default);

    Task<ClassificationAdminResult<bool>> RemoveColumnClassificationAsync(
        ClassificationAdminCaller caller, string columnId, string code, ClassificationAdminScope scope, CancellationToken ct = default);

    /// <param name="code">Classification code, or <c>null</c> for the column-wide override (<c>_all</c>).</param>
    Task<ClassificationAdminResult<ColumnMaskOverride>> UpsertMaskOverrideAsync(
        ClassificationAdminCaller caller, string columnId, string? code, MaskRuleInput input, ClassificationAdminScope scope, CancellationToken ct = default);

    Task<ClassificationAdminResult<bool>> DeleteMaskOverrideAsync(
        ClassificationAdminCaller caller, string columnId, string? code, ClassificationAdminScope scope, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IClassificationAdminService"/>. Scope isolation: the global <c>*</c> scope is writable by a
/// cluster administrator only, a tenant scope by that tenant's governance administrator, another tenant is invisible
/// (not found). Column assignment, removal and overrides additionally allow the owner or delegate of the column's table.
/// All writes go through <see cref="IClassificationRepository"/>, which flushes the catalog version immediately.
/// </summary>
public sealed class ClassificationAdminService(
    IClassificationRepository repository,
    ITableMetadataRepository tableMetadataRepository,
    IContainerOwnership? ownershipRepository = null,
    TimeProvider? timeProvider = null,
    IGovernanceDomainRepository? protectionRepository = null) : IClassificationAdminService
{
    private const string ColumnWideOverride = "_all";

    private readonly IClassificationRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ITableMetadataRepository _tables = tableMetadataRepository ?? throw new ArgumentNullException(nameof(tableMetadataRepository));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public static string? ParseOverrideCode(string codeOrAll) =>
        string.Equals(codeOrAll, ColumnWideOverride, StringComparison.Ordinal) ? null : codeOrAll;

    public async Task<ClassificationAdminResult<DataClassification>> GetClassificationAsync(
        ClassificationAdminCaller caller, string code, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default)
    {
        var resolved = ResolveVocabularyScope<DataClassification>(caller, scope, tenant, write: false);
        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        if (!ClassificationHierarchyValidator.IsValidClassificationCode(code))
        {
            return Invalid<DataClassification>("The classification code is not valid.");
        }

        var row = await _repository.GetClassificationAsync(resolved.Scope!, code, ct).ConfigureAwait(false);
        return row is null
            ? NotFound<DataClassification>()
            : ClassificationAdminResults.Ok(row);
    }

    public async Task<ClassificationAdminResult<DataClassification>> UpsertClassificationAsync(
        ClassificationAdminCaller caller, string code, ClassificationDefinitionInput input, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resolved = ResolveVocabularyScope<DataClassification>(caller, scope, tenant, write: true);
        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        if (!ClassificationHierarchyValidator.IsValidClassificationCode(code))
        {
            return Invalid<DataClassification>("The classification code is not valid.");
        }

        // A tenant may adjust the texts and the active flag of a built-in code, never its rank or parent.
        if (resolved.Scope != ClassificationScopes.Global &&
            ClassificationAccessResolver.BuiltInCatalog(resolved.Scope!).Definitions.TryGetValue(code, out var builtIn) &&
            (builtIn.RestrictionRank != input.RestrictionRank ||
             !string.Equals(builtIn.ParentCode, input.ParentCode, StringComparison.Ordinal)))
        {
            return Invalid<DataClassification>("A tenant may not change the rank or parent of a built-in classification.");
        }

        var existing = await _repository.GetClassificationAsync(resolved.Scope!, code, ct).ConfigureAwait(false);

        // Retiring (deactivating) a category that a column classification or a mask category still names is refused: the columns
        // would otherwise be denied as unknown. A failing reference check throws, so the change is refused as well (fail closed).
        if (!input.IsActive && (existing is null || existing.IsActive) &&
            await IsReferencedAsync(resolved.Scope!, code, ct).ConfigureAwait(false))
        {
            return ClassificationAdminResults.Fail<DataClassification>(
                ClassificationAdminStatus.Conflict, ClassificationAdminErrorCodes.InUse, "The classification is still referenced by columns.");
        }

        var candidate = new DataClassification
        {
            Scope = resolved.Scope!,
            Code = code,
            DisplayName = input.DisplayName,
            Description = input.Description,
            ParentCode = input.ParentCode,
            RestrictionRank = input.RestrictionRank,
            IsActive = input.IsActive,
            Version = (existing?.Version ?? 0) + 1,
            UpdatedBy = caller.ActorSid,
            UpdatedAt = _time.GetUtcNow()
        };

        try
        {
            var stored = await _repository.UpsertClassificationAsync(candidate, ct).ConfigureAwait(false);
            return ClassificationAdminResults.Ok(stored);
        }
        catch (ArgumentException ex)
        {
            return Invalid<DataClassification>(ex.Message);
        }
    }

    public async Task<ClassificationAdminResult<ClassificationMaskRule>> GetMaskRuleAsync(
        ClassificationAdminCaller caller, string code, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default)
    {
        var resolved = ResolveVocabularyScope<ClassificationMaskRule>(caller, scope, tenant, write: false);
        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        if (!ClassificationHierarchyValidator.IsValidMaskRuleCode(code))
        {
            return Invalid<ClassificationMaskRule>("The classification code is not valid.");
        }

        var row = await _repository.GetClassificationMaskRuleAsync(resolved.Scope!, code, ct).ConfigureAwait(false);
        return row is null
            ? NotFound<ClassificationMaskRule>()
            : ClassificationAdminResults.Ok(row);
    }

    public async Task<ClassificationAdminResult<ClassificationMaskRule>> UpsertMaskRuleAsync(
        ClassificationAdminCaller caller, string code, MaskRuleInput input, ClassificationAdminScope scope, string? tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resolved = ResolveVocabularyScope<ClassificationMaskRule>(caller, scope, tenant, write: true);
        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        if (!ClassificationHierarchyValidator.IsValidMaskRuleCode(code))
        {
            return Invalid<ClassificationMaskRule>("The classification code is not valid.");
        }

        var existing = await _repository.GetClassificationMaskRuleAsync(resolved.Scope!, code, ct).ConfigureAwait(false);
        try
        {
            var stored = await _repository.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
            {
                Scope = resolved.Scope!,
                Code = code,
                MaskFunction = input.MaskFunction,
                ParamsJson = input.ParamsJson,
                KeyRef = input.KeyRef,
                CatalogText = input.CatalogText,
                Version = (existing?.Version ?? 0) + 1,
                UpdatedBy = caller.ActorSid,
                UpdatedAt = _time.GetUtcNow()
            }, ct).ConfigureAwait(false);
            return ClassificationAdminResults.Ok(stored);
        }
        catch (ArgumentException ex)
        {
            return Invalid<ClassificationMaskRule>(ex.Message);
        }
    }

    public async Task<ClassificationAdminResult<ColumnClassification>> AssignColumnClassificationAsync(
        ClassificationAdminCaller caller, string columnId, string code, ClassificationAdminScope scope, CancellationToken ct = default)
    {
        var auth = await AuthorizeColumnAsync<ColumnClassification>(caller, columnId, scope, ct).ConfigureAwait(false);
        if (auth.Failure is { } failure)
        {
            return failure;
        }

        if (!await IsKnownActiveCodeAsync(auth.EffectiveTenant!, code, ct).ConfigureAwait(false))
        {
            return ClassificationAdminResults.Fail<ColumnClassification>(
                ClassificationAdminStatus.Invalid, ClassificationAdminErrorCodes.UnknownClassification, "The classification is unknown or inactive.");
        }

        var assignment = new ColumnClassification
        {
            Scope = auth.Scope!,
            TableColumnId = columnId,
            Code = code,
            Source = caller.IsClusterAdmin || caller.IsGovernanceAdmin ? "ADMIN" : "OWNER",
            AssignedBy = caller.ActorSid,
            AssignedAt = _time.GetUtcNow()
        };

        try
        {
            await _repository.AssignColumnClassificationAsync(assignment, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Invalid<ColumnClassification>(ex.Message);
        }

        return ClassificationAdminResults.Ok(assignment);
    }

    public async Task<ClassificationAdminResult<bool>> RemoveColumnClassificationAsync(
        ClassificationAdminCaller caller, string columnId, string code, ClassificationAdminScope scope, CancellationToken ct = default)
    {
        // Removing a classification lifts its classification mask: it needs owner or governance authority over the column.
        var auth = await AuthorizeColumnAsync<bool>(caller, columnId, scope, ct).ConfigureAwait(false);
        if (auth.Failure is { } failure)
        {
            return failure;
        }

        if (!ClassificationHierarchyValidator.IsValidClassificationCode(code))
        {
            return Invalid<bool>("The classification code is not valid.");
        }

        var removed = await _repository.RemoveColumnClassificationAsync(auth.Scope!, columnId, code, ct).ConfigureAwait(false);
        return removed
            ? ClassificationAdminResults.Ok(true)
            : NotFound<bool>();
    }

    public async Task<ClassificationAdminResult<ColumnMaskOverride>> UpsertMaskOverrideAsync(
        ClassificationAdminCaller caller, string columnId, string? code, MaskRuleInput input, ClassificationAdminScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var auth = await AuthorizeColumnAsync<ColumnMaskOverride>(caller, columnId, scope, ct).ConfigureAwait(false);
        if (auth.Failure is { } failure)
        {
            return failure;
        }

        if (code is not null && !await IsKnownActiveCodeAsync(auth.EffectiveTenant!, code, ct).ConfigureAwait(false))
        {
            return ClassificationAdminResults.Fail<ColumnMaskOverride>(
                ClassificationAdminStatus.Invalid, ClassificationAdminErrorCodes.UnknownClassification, "The classification is unknown or inactive.");
        }

        try
        {
            var stored = await _repository.UpsertColumnMaskOverrideAsync(new ColumnMaskOverride
            {
                Scope = auth.Scope!,
                TableColumnId = columnId,
                Code = code,
                MaskFunction = input.MaskFunction,
                ParamsJson = input.ParamsJson,
                KeyRef = input.KeyRef,
                CatalogText = input.CatalogText,
                UpdatedBy = caller.ActorSid,
                UpdatedAt = _time.GetUtcNow()
            }, ct).ConfigureAwait(false);
            return ClassificationAdminResults.Ok(stored);
        }
        catch (ArgumentException ex)
        {
            return Invalid<ColumnMaskOverride>(ex.Message);
        }
    }

    public async Task<ClassificationAdminResult<bool>> DeleteMaskOverrideAsync(
        ClassificationAdminCaller caller, string columnId, string? code, ClassificationAdminScope scope, CancellationToken ct = default)
    {
        var auth = await AuthorizeColumnAsync<bool>(caller, columnId, scope, ct).ConfigureAwait(false);
        if (auth.Failure is { } failure)
        {
            return failure;
        }

        if (code is not null && !ClassificationHierarchyValidator.IsValidClassificationCode(code))
        {
            return Invalid<bool>("The classification code is not valid.");
        }

        var removed = await _repository.DeleteColumnMaskOverrideAsync(auth.Scope!, columnId, code, ct).ConfigureAwait(false);
        return removed
            ? ClassificationAdminResults.Ok(true)
            : NotFound<bool>();
    }

    private async ValueTask<bool> IsReferencedAsync(string scope, string code, CancellationToken ct)
    {
        if (await _repository.IsClassificationReferencedAsync(scope, code, ct).ConfigureAwait(false))
        {
            return true;
        }

        return protectionRepository is not null &&
               await protectionRepository.IsMaskCategoryReferencedAsync(
                   ClassificationScopes.IsGlobal(scope) ? (TenantId?)null : new TenantId(scope), code, ct).ConfigureAwait(false);
    }

    private async ValueTask<bool> IsKnownActiveCodeAsync(string tenant, string code, CancellationToken ct)
    {
        if (!ClassificationHierarchyValidator.IsValidClassificationCode(code))
        {
            return false;
        }

        var defs = await _repository.GetClassificationsAsync(tenant, includeInactive: true, ct).ConfigureAwait(false);
        var catalog = defs.Count == 0
            ? ClassificationAccessResolver.BuiltInCatalog(tenant)
            : ClassificationAccessResolver.BuildCatalog(tenant, defs, []);
        return catalog.Definitions.TryGetValue(code, out var def) && def.IsActive;
    }

    private static (string? Scope, string? EffectiveTenant, ClassificationAdminResult<T>? Failure) ResolveVocabularyScope<T>(
        ClassificationAdminCaller caller, ClassificationAdminScope scope, string? tenant, bool write)
    {
        if (!caller.IsClusterAdmin && !caller.IsGovernanceAdmin)
        {
            return (null, null, Forbidden<T>(ClassificationAdminErrorCodes.AuthorityRequired, "Governance administrator authority is required."));
        }

        if (scope == ClassificationAdminScope.Global)
        {
            return write && !caller.IsClusterAdmin
                ? (null, null, Forbidden<T>(ClassificationAdminErrorCodes.ScopeForbidden, "Only a cluster administrator may write the global scope."))
                : (ClassificationScopes.Global, caller.Tenant.Value, null);
        }

        var target = ResolveTargetTenant<T>(caller, tenant);
        return target.Failure is not null
            ? (null, null, target.Failure)
            : (target.Tenant, target.Tenant, null);
    }

    private static (string? Tenant, ClassificationAdminResult<T>? Failure) ResolveTargetTenant<T>(
        ClassificationAdminCaller caller, string? tenant)
    {
        var own = caller.Tenant.Value;
        if (string.IsNullOrWhiteSpace(tenant) || string.Equals(tenant, own, StringComparison.Ordinal))
        {
            return ClassificationScopes.IsValidTenantScope(own)
                ? (own, null)
                : (null, Invalid<T>("The tenant is not valid."));
        }

        // Another tenant is invisible to a tenant administrator (no existence oracle).
        if (!caller.IsClusterAdmin)
        {
            return (null, NotFound<T>());
        }

        return ClassificationScopes.IsValidTenantScope(tenant)
            ? (tenant, null)
            : (null, Invalid<T>("The tenant is not valid."));
    }

    private async ValueTask<(string? Scope, string? EffectiveTenant, ClassificationAdminResult<T>? Failure)> AuthorizeColumnAsync<T>(
        ClassificationAdminCaller caller, string columnId, ClassificationAdminScope scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(columnId))
        {
            return (null, null, Invalid<T>("The column id is required."));
        }

        if (scope == ClassificationAdminScope.Global && !caller.IsClusterAdmin)
        {
            return (null, null, Forbidden<T>(ClassificationAdminErrorCodes.ScopeForbidden, "Only a cluster administrator may write the global scope."));
        }

        var table = await _repository.GetTableIdentifierByColumnIdAsync(columnId, ct).ConfigureAwait(false);
        if (table is null)
        {
            return (null, null, ColumnNotFound<T>());
        }

        if (!caller.IsClusterAdmin)
        {
            var meta = await _tables.GetTableMetadataAsync(table.Value, ct).ConfigureAwait(false);
            if (meta is null)
            {
                return (null, null, ColumnNotFound<T>());
            }

            var owner = meta.OwnerTenantId;
            if (!string.IsNullOrWhiteSpace(owner)
                && !ClassificationScopes.IsGlobal(owner)
                && !string.Equals(owner, caller.Tenant.Value, StringComparison.Ordinal))
            {
                return (null, null, ColumnNotFound<T>());
            }
        }

        if (!caller.IsClusterAdmin && !caller.IsGovernanceAdmin && !caller.IsContainerOwnerAuthorized)
        {
            var authorized = ownershipRepository is not null
                && !string.IsNullOrWhiteSpace(caller.ActorSid)
                && await ownershipRepository.IsAuthorizedApproverForTableAsync(table.Value, new Sid(caller.ActorSid), ct).ConfigureAwait(false);
            if (!authorized)
            {
                return (null, null, Forbidden<T>(ClassificationAdminErrorCodes.AuthorityRequired, "Data owner, delegate or governance administrator authority is required."));
            }
        }

        var tenant = caller.Tenant.Value;
        if (scope == ClassificationAdminScope.Tenant && !ClassificationScopes.IsValidTenantScope(tenant))
        {
            return (null, null, Invalid<T>("The tenant is not valid."));
        }

        return (scope == ClassificationAdminScope.Global ? ClassificationScopes.Global : tenant, tenant, null);
    }

    private static ClassificationAdminResult<T> Forbidden<T>(string code, string message) =>
        ClassificationAdminResults.Fail<T>(ClassificationAdminStatus.Forbidden, code, message);

    private static ClassificationAdminResult<T> NotFound<T>() =>
        ClassificationAdminResults.Fail<T>(ClassificationAdminStatus.NotFound, ClassificationAdminErrorCodes.NotFound, "The resource was not found.");

    private static ClassificationAdminResult<T> ColumnNotFound<T>() =>
        ClassificationAdminResults.Fail<T>(ClassificationAdminStatus.NotFound, ClassificationAdminErrorCodes.ColumnNotFound, "The column was not found.");

    private static ClassificationAdminResult<T> Invalid<T>(string message) =>
        ClassificationAdminResults.Fail<T>(ClassificationAdminStatus.Invalid, ClassificationAdminErrorCodes.Invalid, message);
}
