using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

public enum GovernanceDomainWriteStatus { Created = 0, Updated = 1, Conflict = 2, NotFound = 3 }

public sealed record GovernanceDomainUpsertResult(GovernanceDomainWriteStatus Status, GovernanceDomain? Domain = null)
{
    public bool Succeeded => Status is GovernanceDomainWriteStatus.Created or GovernanceDomainWriteStatus.Updated;
}

/// <summary>Tenant-isolated persistence for governance domains, domain table assignments, and object protection metadata.</summary>
public interface IGovernanceDomainRepository
{
    public const int MaxPageSize = 500;
    public const int MaxBatchAssignCount = 1000;

    Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default);
    Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default);
    Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default);
    Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default);
    Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default);
    Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default);
    Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default);
    Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default);
    Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default);

    /// <summary>
    /// True when an object protection row names <paramref name="category"/> as its mask category, in <paramref name="tenant"/> or, when
    /// <paramref name="tenant"/> is <c>null</c>, in any tenant. A store without the check throws, so the caller refuses (fail closed).
    /// </summary>
    Task<bool> IsMaskCategoryReferencedAsync(TenantId? tenant, string category, CancellationToken ct = default) =>
        throw new NotSupportedException("The mask category reference check is not available.");

    /// <summary>
    /// <see cref="GetProtectionAsync"/> for several tables of one tenant (every requested id is present in the result). Implementations
    /// read set-based; the default reads table by table.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, (ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)>> GetProtectionBatchAsync(
        TenantId tenant, IReadOnlyCollection<Guid> tableIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableIds);
        var result = new Dictionary<Guid, (ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)>(tableIds.Count);
        foreach (var id in tableIds)
        {
            result[id] = await GetProtectionAsync(tenant, id, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// <see cref="GetContainerForTableAsync"/> for several tables of one tenant (every requested id is present in the result). Implementations
    /// read set-based; the default reads table by table.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, ContainerDefaults>> GetContainersForTablesAsync(
        TenantId tenant, IReadOnlyCollection<Guid> tableIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableIds);
        var result = new Dictionary<Guid, ContainerDefaults>(tableIds.Count);
        foreach (var id in tableIds)
        {
            result[id] = await GetContainerForTableAsync(tenant, id, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Table ids explicitly assigned to <paramref name="domainId"/>, ordered by id and bounded by <paramref name="limit"/> (clamped to <see cref="MaxPageSize"/> * 10).</summary>
    Task<IReadOnlyList<Guid>> ListDomainTableIdsAsync(TenantId tenant, Guid domainId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    /// <summary>Identifiers of the tables explicitly assigned to <paramref name="domainId"/>, ordered by table id and bounded like <see cref="ListDomainTableIdsAsync"/>.</summary>
    Task<IReadOnlyList<TableIdentifier>> ListDomainTablesAsync(TenantId tenant, Guid domainId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TableIdentifier>>([]);
}
