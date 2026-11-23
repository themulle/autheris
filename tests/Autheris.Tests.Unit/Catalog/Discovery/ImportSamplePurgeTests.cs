namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Governance;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ImportSamplePurgeTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static SecurityPrincipalContext Caller(string sid, string role)
    {
        var claims = new Claim[]
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", Tenant.Value),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return SecurityPrincipalContext.FromPrincipal(principal, Tenant) with
        {
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
    }

    [Fact]
    public async Task Dg_can_purge_import()
    {
        var domains = new StubDomainRepo();
        var tables = new InMemoryTableMetadataRepository();
        var table = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: Tenant.Value);
        await tables.UpsertTableMetadataAsync(table);

        var audit = new FakeAuditWriter();
        var service = new GovernanceDomainService(
            domains,
            tables,
            new FakeConsentRepository(),
            audit,
            new RoleOnlyCapabilities(),
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));

        var store = new FakeDiscoveryArtifactStore();
        var actor = new Sid("S-1-5-21-SYS-1");
        await store.UpsertAsync(new DiscoveryArtifacts(table.Table.Id, Tenant.Value, """[{"id":1}]""", null, "sha-imp", "dbt", DateTimeOffset.UtcNow, 1, ArtifactProvenance.Import), actor);
        await store.UpsertAsync(new DiscoveryArtifacts(table.Table.Id, Tenant.Value, """[{"id":2}]""", null, "sha-up", "user", DateTimeOffset.UtcNow, 1, ArtifactProvenance.Upload), actor);

        var denied = await service.PurgeImportSamplesAsync(Caller("S-1-5-21-READER-1", "Reader"), Tenant, table.Identifier.ToString(), store);
        denied.Status.ShouldBe(DomainServiceStatus.Forbidden);
        (await store.GetAsync(table.Table.Id, ArtifactProvenance.Import)).ShouldNotBeNull();

        var purged = await service.PurgeImportSamplesAsync(Caller("S-1-5-21-DG-1", "GovernanceAdmin"), Tenant, table.Identifier.ToString(), store);
        purged.Status.ShouldBe(DomainServiceStatus.Ok);
        purged.Value.ShouldBeTrue();
        (await store.GetAsync(table.Table.Id, ArtifactProvenance.Import)).ShouldBeNull();
        (await store.GetAsync(table.Table.Id, ArtifactProvenance.Upload)).ShouldNotBeNull();
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ImportSamplesPurged && e.Decision == "ALLOW");
    }

    private sealed class StubDomainRepo : IGovernanceDomainRepository
    {
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GovernanceDomain>>([]);
        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) => Task.FromResult<GovernanceDomain?>(null);
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) => Task.FromResult(false);
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            Task.FromResult<(ObjectProtection?, IReadOnlyDictionary<string, ObjectProtection>)>((null, new Dictionary<string, ObjectProtection>()));
        public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => Task.FromResult(ContainerDefaults.Unassigned);
    }

    private sealed class RoleOnlyCapabilities : IGovernanceCapabilities
    {
        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) =>
            principal?.IsInRole("GovernanceAdmin") == true;

        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => false;
        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct) => ValueTask.FromResult(false);
        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) => ValueTask.FromResult(IsGovernance(principal.Principal, objectTenant));
        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) => IsGovernance(principal, objectTenant);
    }
}
