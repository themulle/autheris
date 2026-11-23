namespace Autheris.Tests.Unit.Audit;

using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Audit read scope: tenant-wide for Data Governance and Security Auditor, owner members only for objects of their containers, never cross-tenant.</summary>
[Trait("Category", "Security")]
public sealed class AuditReadScopeTests
{
    private static readonly TenantId Tenant = TestTenants.Default;
    private static readonly TenantId OtherTenant = new("other-tenant");
    private static readonly ContainerRef OwnContainer = new(ContainerKind.Domain, Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly ContainerRef ForeignContainer = new(ContainerKind.Domain, Guid.Parse("00000000-0000-0000-0000-0000000000b2"));

    private static SecurityPrincipalContext Caller(string sid = "S-1-5-21-OWNER-MEMBER") => new()
    {
        UserSid = new Sid(sid),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "test"
    };

    private static InMemoryAuditLogRepository Repo()
    {
        var repo = new InMemoryAuditLogRepository();
        void Add(string table, TenantId tenant, string eventType = AuditEventTypes.PermissionApproved, string? details = null) =>
            repo.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenant,
                TargetTable = table,
                EventType = eventType,
                ActorSid = new Sid("S-1-5-21-ACTOR"),
                DetailsJson = details ?? "{}"
            }).GetAwaiter().GetResult();

        Add("default.sales.orders", Tenant);
        Add("default.sales.orders", Tenant, AuditEventTypes.SelfGrant);
        Add("default.hr.salaries", Tenant);
        Add("default.sales.orders", OtherTenant);
        Add("default.sales.orders", Tenant, details: "{\"ActorKind\":\"ServicePrincipal\"}");
        Add("default.hr.salaries", Tenant, details: "{\"ActorKind\":\"ServicePrincipal\"}");
        return repo;
    }

    private static AuditReadService Service(InMemoryAuditLogRepository repo, int? maxPageSize = null) => new(
        repo,
        new Tables("default.sales.orders", "default.hr.salaries"),
        new Domains((OwnContainer.Id, true, ["default.sales.orders"]), (ForeignContainer.Id, true, ["default.hr.salaries"])),
        new Capabilities(OwnContainer),
        maxPageSize is null ? null : Options.Create(new AccessPolicyOptions { MaxPageSize = maxPageSize.Value }));

    private static AuditReadRequest Request(
        bool tenantWide,
        string? view = null,
        string? cursor = null,
        int? pageSize = null,
        string? table = null,
        TenantId? tenant = null,
        SecurityPrincipalContext? caller = null) =>
        new(caller ?? Caller(), tenant ?? Tenant, tenantWide, table, null, null, view, cursor, pageSize);

    [Fact]
    public async Task Owner_sees_only_own_containers()
    {
        var result = await Service(Repo()).ReadAsync(Request(tenantWide: false));

        result.Outcome.ShouldBe(AuditReadOutcome.Ok);
        result.Items.ShouldNotBeEmpty();
        result.Items.ShouldAllBe(e => e.TargetTable == "default.sales.orders");
    }

    [Fact]
    public async Task Owner_naming_a_foreign_object_is_refused()
    {
        var result = await Service(Repo()).ReadAsync(Request(tenantWide: false, table: "default.hr.salaries"));

        result.Outcome.ShouldBe(AuditReadOutcome.Forbidden);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Caller_without_ownership_is_refused()
    {
        var tables = new Tables("default.sales.orders");
        var service = new AuditReadService(
            Repo(),
            tables,
            new Domains((ForeignContainer.Id, true, ["default.sales.orders"])),
            new Capabilities(OwnContainer));

        (await service.ReadAsync(Request(tenantWide: false))).Outcome.ShouldBe(AuditReadOutcome.Forbidden);
        tables.IdLookups.ShouldBe(0);
    }

    [Fact]
    public async Task Owner_of_nothing_is_refused_without_any_table_lookup()
    {
        var tables = new Tables("default.hr.salaries");
        var domains = new Domains((ForeignContainer.Id, true, ["default.hr.salaries"]), (Guid.NewGuid(), false, ["default.x.y"]));
        var service = new AuditReadService(Repo(), tables, domains, new Capabilities(OwnContainer));

        (await service.ReadAsync(Request(tenantWide: false))).Outcome.ShouldBe(AuditReadOutcome.Forbidden);

        tables.IdLookups.ShouldBe(0);
        domains.TableListCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Owner_enumerates_only_the_tables_of_owned_containers()
    {
        var tables = new Tables("default.sales.orders", "default.hr.salaries", "default.hr.bonus");
        var domains = new Domains((OwnContainer.Id, true, ["default.sales.orders"]), (ForeignContainer.Id, true, ["default.hr.salaries", "default.hr.bonus"]));
        var service = new AuditReadService(Repo(), tables, domains, new Capabilities(OwnContainer));

        var result = await service.ReadAsync(Request(tenantWide: false));

        result.Outcome.ShouldBe(AuditReadOutcome.Ok);
        result.ScopeTruncated.ShouldBeFalse();
        tables.IdLookups.ShouldBe(1);
        domains.TableListCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Oversized_ownership_is_cut_deterministically_and_flagged()
    {
        var names = Enumerable.Range(0, AuditReadService.MaxScopedObjects + 5).Select(i => $"default.big.t{i:D5}").ToArray();
        var service = new AuditReadService(Repo(), new Tables(names), new Domains((OwnContainer.Id, true, names)), new Capabilities(OwnContainer));

        var first = await service.ReadAsync(Request(tenantWide: false));
        var second = await service.ReadAsync(Request(tenantWide: false));

        first.Outcome.ShouldBe(AuditReadOutcome.Ok);
        first.ScopeTruncated.ShouldBeTrue();
        second.ScopeTruncated.ShouldBeTrue();
        (await Service(Repo()).ReadAsync(Request(tenantWide: false))).ScopeTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Owner_read_with_failing_domain_store_is_refused()
    {
        var service = new AuditReadService(Repo(), new Tables(), new Domains(throwOnList: true), new Capabilities(OwnContainer));

        (await service.ReadAsync(Request(tenantWide: false))).Outcome.ShouldBe(AuditReadOutcome.Forbidden);
    }

    [Fact]
    public async Task Missing_ownership_collaborators_refuse_an_owner_read()
    {
        var service = new AuditReadService(Repo());

        (await service.ReadAsync(Request(tenantWide: false))).Outcome.ShouldBe(AuditReadOutcome.Forbidden);
    }

    [Fact]
    public async Task Dg_tenant_wide()
    {
        var result = await Service(Repo()).ReadAsync(Request(tenantWide: true));

        result.Items.Select(e => e.TargetTable).Distinct().Order().ShouldBe(["default.hr.salaries", "default.sales.orders"]);
        result.Items.ShouldAllBe(e => e.TenantId == Tenant);
    }

    [Fact]
    public async Task Other_tenant_never_visible()
    {
        var wide = await Service(Repo()).ReadAsync(Request(tenantWide: true));
        var owner = await Service(Repo()).ReadAsync(Request(tenantWide: false));

        wide.Items.ShouldAllBe(e => e.TenantId == Tenant);
        owner.Items.ShouldAllBe(e => e.TenantId == Tenant);
    }

    [Fact]
    public async Task View_filter_cannot_widen()
    {
        var owner = await Service(Repo()).ReadAsync(Request(tenantWide: false, view: AuditReadViews.DecisionsName));
        var selfGrants = await Service(Repo()).ReadAsync(Request(tenantWide: false, view: AuditReadViews.SelfGrantsName));

        owner.Items.ShouldNotBeEmpty();
        owner.Items.ShouldAllBe(e => e.TargetTable == "default.sales.orders" && e.EventType != AuditEventTypes.SelfGrant);
        selfGrants.Items.ShouldAllBe(e => e.TargetTable == "default.sales.orders" && e.EventType == AuditEventTypes.SelfGrant);
    }

    [Fact]
    public async Task Unknown_view_is_rejected_not_widened()
    {
        (await Service(Repo()).ReadAsync(Request(tenantWide: true, view: "everything"))).Outcome.ShouldBe(AuditReadOutcome.InvalidView);
    }

    [Fact]
    public async Task Sp_decisions_view()
    {
        var wide = await Service(Repo()).ReadAsync(Request(tenantWide: true, view: AuditReadViews.DecisionsByServicePrincipalsName));
        var owner = await Service(Repo()).ReadAsync(Request(tenantWide: false, view: AuditReadViews.DecisionsByServicePrincipalsName));

        wide.Items.Count.ShouldBe(2);
        wide.Items.ShouldAllBe(e => AuditActorKindReader.Read(e) == "ServicePrincipal");
        owner.Items.ShouldHaveSingleItem().TargetTable.ShouldBe("default.sales.orders");
    }

    [Fact]
    public async Task Cursor_bound_to_scope()
    {
        var service = Service(Repo());
        var first = await service.ReadAsync(Request(tenantWide: true, pageSize: 2));
        first.NextCursor.ShouldNotBeNull();

        var resumed = await service.ReadAsync(Request(tenantWide: true, pageSize: 2, cursor: first.NextCursor));
        var widenedView = await service.ReadAsync(Request(tenantWide: true, pageSize: 2, cursor: first.NextCursor, view: AuditReadViews.DecisionsName));
        var otherCaller = await service.ReadAsync(Request(tenantWide: true, pageSize: 2, cursor: first.NextCursor, caller: Caller("S-1-5-21-OTHER")));
        var forged = await service.ReadAsync(Request(tenantWide: true, cursor: PageCursor.Encode(3)));

        resumed.Outcome.ShouldBe(AuditReadOutcome.Ok);
        var lastSeen = first.Items[^1].ChainSequence;
        resumed.Items.ShouldAllBe(e => e.ChainSequence < lastSeen);
        widenedView.Outcome.ShouldBe(AuditReadOutcome.InvalidCursor);
        otherCaller.Outcome.ShouldBe(AuditReadOutcome.InvalidCursor);
        forged.Outcome.ShouldBe(AuditReadOutcome.InvalidCursor);
    }

    [Fact]
    public async Task Page_size_bounded()
    {
        var capped = await Service(Repo(), maxPageSize: 3).ReadAsync(Request(tenantWide: true, pageSize: 10_000));
        var floor = await Service(Repo()).ReadAsync(Request(tenantWide: true, pageSize: -5));

        capped.PageSize.ShouldBe(3);
        capped.Items.Count.ShouldBeLessThanOrEqualTo(3);
        floor.PageSize.ShouldBe(1);
        (await Service(Repo()).ReadAsync(Request(tenantWide: true, pageSize: 10_000))).PageSize.ShouldBe(AuditLogPageQuery.MaxPageSize);
    }
}

file static class TableIds
{
    public static Guid For(string name) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));
}

file sealed class Tables(params string[] names) : ITableMetadataRepository
{
    public int IdLookups { get; private set; }

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);

    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TableMetadata>>([]);

    public Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        IdLookups++;
        var name = names.FirstOrDefault(n => TableIds.For(n) == tableId);
        return Task.FromResult<TableIdentifier?>(name is null ? null : TableIdentifier.Parse(name));
    }

    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);

    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
}

file sealed class Domains : IGovernanceDomainRepository
{
    private readonly List<(Guid Id, bool HasOwner, string[] Tables)> _domains;
    private readonly bool _throwOnList;

    public Domains(params (Guid Id, bool HasOwner, string[] Tables)[] domains)
    {
        _domains = [.. domains];
    }

    public Domains(bool throwOnList)
    {
        _domains = [];
        _throwOnList = throwOnList;
    }

    public int TableListCalls { get; private set; }

    public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        if (_throwOnList)
        {
            throw new InvalidOperationException("store down");
        }

        var now = DateTimeOffset.UnixEpoch;
        return Task.FromResult<IReadOnlyList<GovernanceDomain>>(_domains.Skip(offset).Take(limit).Select(d => new GovernanceDomain(
            d.Id, tenant, "d", null, null, d.HasOwner ? new Sid("S-1-5-21-OWNERS") : (Sid?)null, ProtectionClass.Internal,
            CatalogVisibilityMode.Open, false, 1, now, now)).ToList());
    }

    public Task<IReadOnlyList<Guid>> ListDomainTableIdsAsync(TenantId tenant, Guid domainId, int limit, CancellationToken ct = default)
    {
        TableListCalls++;
        return Task.FromResult<IReadOnlyList<Guid>>(_domains.Where(d => d.Id == domainId).SelectMany(d => d.Tables).Order(StringComparer.Ordinal).Take(limit).Select(TableIds.For).ToList());
    }

    public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => throw new NotSupportedException();
}

file sealed class Capabilities(ContainerRef ownedContainer) : IGovernanceCapabilities
{
    public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => false;

    public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => false;

    public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct) =>
        ValueTask.FromResult(container == ownedContainer);

    public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) => false;
}
