namespace Autheris.Tests.Unit.TestSupport;

using System.Data.Common;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>In-memory domain list and governance epoch for container visibility tests.</summary>
internal sealed class FakeContainerDomainRepository : IGovernanceDomainRepository, IGovernanceEpochStore
{
    private readonly List<GovernanceDomain> _domains = [];
    private long _epoch;

    public Exception? ListFailure { get; set; }
    public int ListCalls { get; private set; }

    public GovernanceDomain Add(
        TenantId tenant,
        string name,
        CatalogVisibilityMode visibility,
        Sid? owner = null,
        bool unassigned = false)
    {
        var now = DateTimeOffset.UtcNow;
        var domain = new GovernanceDomain(
            Guid.NewGuid(), tenant, name, $"{name} description", $"https://contact.example/{name}", owner,
            ProtectionClass.Internal, visibility, unassigned, 1, now, now);
        _domains.Add(domain);
        return domain;
    }

    public void BumpEpoch() => Interlocked.Increment(ref _epoch);

    public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(Interlocked.Read(ref _epoch));

    public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(Interlocked.Increment(ref _epoch));

    public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        ListCalls++;
        if (ListFailure is { } failure)
        {
            return Task.FromException<IReadOnlyList<GovernanceDomain>>(failure);
        }

        IReadOnlyList<GovernanceDomain> result = _domains.Where(d => d.Tenant == tenant).Skip(offset).Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) =>
        Task.FromResult(_domains.FirstOrDefault(d => d.Tenant == tenant && d.Id == domainId));

    public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(
        TenantId tenant, Guid tableId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task SetTableProtectionAsync(
        TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeCatalogGrantRepository : ICatalogVisibilityGrantRepository
{
    private readonly List<CatalogVisibilityGrant> _grants = [];

    public Exception? ListFailure { get; set; }
    public int ListCalls { get; private set; }

    public void Add(TenantId tenant, ContainerRef container, Sid grantee, DateTimeOffset? validTo = null)
    {
        var now = DateTimeOffset.UtcNow;
        _grants.Add(new CatalogVisibilityGrant
        {
            TenantId = tenant,
            ContainerKind = container.Kind,
            ContainerId = container.Id,
            GranteeSid = grantee,
            ValidFrom = now.AddDays(-1),
            ValidTo = validTo ?? now.AddDays(30),
            CreatedAt = now
        });
    }

    public void RevokeAll() => _grants.Clear();

    public Task<IReadOnlyList<CatalogVisibilityGrant>> ListActiveForSubjectsAsync(
        TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ListCalls++;
        if (ListFailure is { } failure)
        {
            return Task.FromException<IReadOnlyList<CatalogVisibilityGrant>>(failure);
        }

        var set = subjects.ToHashSet();
        IReadOnlyList<CatalogVisibilityGrant> result = _grants
            .Where(g => g.TenantId == tenantId && set.Contains(g.GranteeSid) && g.IsActive(atTime))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<CatalogVisibilityGrant> CreateAsync(CatalogVisibilityGrant grant, DbTransaction? transaction = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<CatalogVisibilityGrant?> GetByIdAsync(TenantId tenantId, Guid grantId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<bool> RevokeAsync(
        TenantId tenantId, Guid grantId, Sid revokedBySid, string reason, DateTimeOffset revokedAt, DbTransaction? transaction = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<int> RevokeAllForSubjectAsync(
        TenantId tenantId, IReadOnlyCollection<Sid> subjects, Sid revokedBySid, string reason, DateTimeOffset revokedAt, DbTransaction? transaction = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CatalogVisibilityGrant>> ListByContainerAsync(
        TenantId tenantId, ContainerKind containerKind, Guid containerId, bool includeInactive = false, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeGovernanceCapabilities(bool governance = false, bool itAdmin = false) : IGovernanceCapabilities
{
    public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => governance;

    public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => itAdmin;

    public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) => false;
}
