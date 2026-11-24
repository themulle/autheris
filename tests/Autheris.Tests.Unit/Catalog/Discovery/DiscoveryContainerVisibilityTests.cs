namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.External;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Discovery single-object reads (REST <c>/api/v1/catalog/objects/{id}</c>, <c>/api/v1/catalog/datasets/{id}</c>) apply the container
/// visibility rule on top of the tenant scope: a table outside the caller's tenant scope is still found when the tenant's own container it
/// is assigned to is <c>Open</c>, catalog-granted to the caller or owned by the caller. Anything else answers exactly like a missing table.
/// </summary>
[Trait("Category", "Security")]
public sealed class DiscoveryContainerVisibilityTests
{
    private const string Caller = "S-1-5-21-7001-1001";
    private const string Other = "S-1-5-21-7001-1002";
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly ContainerRef OpenDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef ClosedDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef GrantedDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef OwnedDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef UnassignedDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef ForeignDomain = new(ContainerKind.Domain, Guid.NewGuid());

    [Fact]
    public async Task Open_container_table_outside_the_tenant_scope_is_found()
    {
        var (service, table) = Build("in_open", OpenDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldNotBeNull();
    }

    [Fact]
    public async Task OnRequest_container_table_without_a_grant_answers_like_a_missing_one()
    {
        var (service, table) = Build("in_closed", ClosedDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
        (await service.GetAsync(new TableIdentifier("dom", "dbo", "missing"), CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Catalog_grant_on_the_container_finds_the_table_for_the_grantee_only()
    {
        var (service, table) = Build("in_granted", GrantedDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldNotBeNull();
        (await service.GetAsync(table, CallerOf(Other))).ShouldBeNull();
    }

    [Fact]
    public async Task Open_system_unassigned_container_does_not_widen_the_tenant_scope()
    {
        var (service, table) = Build("in_unassigned", UnassignedDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Container_of_another_tenant_does_not_widen_the_tenant_scope()
    {
        var (service, table) = Build("in_foreign", ForeignDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Table_owned_by_another_tenant_stays_out_of_scope_in_an_open_container()
    {
        var (service, table) = Build("foreign_owned", OpenDomain, ownerTenant: "tenant-b");

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Unavailable_snapshot_admits_nothing()
    {
        var (service, table) = Build("in_open", OpenDomain, snapshotAvailable: false);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Protection_failure_admits_nothing()
    {
        var (service, table) = Build("in_open", OpenDomain, protectionFails: true);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tenant-a")]
    public async Task Owner_and_non_owner_tenant_hide_on_request_unassigned_shared_and_other_user_grants_across_all_surfaces(
        string? defaultTenantId)
    {
        var tables = new FakeDiscoveryTableRepository();
        var consents = new FakeDiscoveryConsentRepository();
        var closed = DiscoveryTestBuilder.MakeTable("dom", "in_closed", ownerTenantId: null);
        var closedOwnTenant = DiscoveryTestBuilder.MakeTable("dom", "in_closed_own_tenant", ownerTenantId: TenantA.Value);
        var closedShared = DiscoveryTestBuilder.MakeTable("dom", "in_closed_shared", ownerTenantId: null);
        var closedOtherGrant = DiscoveryTestBuilder.MakeTable("dom", "in_closed_other", ownerTenantId: null);
        var openUnassigned = DiscoveryTestBuilder.MakeTable("dom", "in_unassigned_open", ownerTenantId: null);
        var foreignOwnedInOpen = DiscoveryTestBuilder.MakeTable("dom", "foreign_owned", ownerTenantId: "tenant-b");
        var hiddenInOpen = DiscoveryTestBuilder.MakeTable("dom", "hidden_in_open", ownerTenantId: null, hidden: true);
        tables.Tables.AddRange([closed, closedOwnTenant, closedShared, closedOtherGrant, openUnassigned, foreignOwnedInOpen, hiddenInOpen]);

        consents.Active.Add(AllowRead(closedOtherGrant, Other, TenantA));

        var protections = new StubProtection(
            new Dictionary<TableIdentifier, EffectiveProtection>
            {
                [closed.Identifier] = ProtectionOf(ClosedDomain),
                [closedOwnTenant.Identifier] = ProtectionOf(ClosedDomain),
                [closedShared.Identifier] = ProtectionOf(ClosedDomain),
                [closedOtherGrant.Identifier] = ProtectionOf(ClosedDomain),
                [openUnassigned.Identifier] = ProtectionOf(UnassignedDomain),
                [foreignOwnedInOpen.Identifier] = ProtectionOf(OpenDomain),
                [hiddenInOpen.Identifier] = ProtectionOf(OpenDomain),
            },
            ClosedDomain);

        var service = CreateService(
            tables,
            consents,
            new FakeDiscoveryOwnershipRepository(),
            protections,
            new StubContainers(available: true),
            defaultTenantId,
            sharedTables: [closedShared.Identifier.ToString()]);

        var caller = CallerOf(Caller);
        await AssertVisibleSetAcrossSurfacesAsync(service, caller, [closed, closedOwnTenant, closedShared, closedOtherGrant, openUnassigned, foreignOwnedInOpen, hiddenInOpen], []);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tenant-a")]
    public async Task Single_read_and_listings_agree_across_container_consent_ownership_unmask_and_hidden_rules(
        string? defaultTenantId)
    {
        var tables = new FakeDiscoveryTableRepository();
        var consents = new FakeDiscoveryConsentRepository();
        var owners = new FakeDiscoveryOwnershipRepository();

        var inOpen = DiscoveryTestBuilder.MakeTable("dom", "in_open", ownerTenantId: null);
        var inGranted = DiscoveryTestBuilder.MakeTable("dom", "in_granted", ownerTenantId: null);
        var inOwnedDomain = DiscoveryTestBuilder.MakeTable("dom", "in_owned_domain", ownerTenantId: null);
        var inClosedOwnedTable = DiscoveryTestBuilder.MakeTable("dom", "in_closed_owned_table", ownerTenantId: null);
        var inClosedReadGrant = DiscoveryTestBuilder.MakeTable("dom", "in_closed_read", ownerTenantId: null);
        var inClosedWriteGrant = DiscoveryTestBuilder.MakeTable("dom", "in_closed_write", ownerTenantId: null);
        var inClosedPattern = DiscoveryTestBuilder.MakeTable("pat", "match_table", ownerTenantId: null);
        var inClosedUnmask = DiscoveryTestBuilder.MakeTable("dom", "in_closed_unmask", ownerTenantId: null);
        var inUnassignedUnmask = DiscoveryTestBuilder.MakeTable("dom", "in_unassigned_unmask", ownerTenantId: null);
        var hiddenOwned = DiscoveryTestBuilder.MakeTable("dom", "hidden_owned", ownerTenantId: null, hidden: true);
        var inClosedHiddenOwned = DiscoveryTestBuilder.MakeTable("dom", "in_closed_hidden_owned", ownerTenantId: null, hidden: true);
        var inClosed = DiscoveryTestBuilder.MakeTable("dom", "in_closed", ownerTenantId: null);
        var inUnassignedOpen = DiscoveryTestBuilder.MakeTable("dom", "in_unassigned", ownerTenantId: null);
        var inForeignDomain = DiscoveryTestBuilder.MakeTable("dom", "in_foreign_domain", ownerTenantId: null);
        var foreignOwned = DiscoveryTestBuilder.MakeTable("dom", "foreign_owned", ownerTenantId: "tenant-b");
        var hiddenNotOwned = DiscoveryTestBuilder.MakeTable("dom", "hidden_not_owned", ownerTenantId: null, hidden: true);

        tables.Tables.AddRange([
            inOpen, inGranted, inOwnedDomain, inClosedOwnedTable, inClosedReadGrant, inClosedWriteGrant,
            inClosedPattern, inClosedUnmask, inUnassignedUnmask, hiddenOwned, inClosedHiddenOwned,
            inClosed, inUnassignedOpen, inForeignDomain, foreignOwned, hiddenNotOwned
        ]);

        owners.OwnedTables.Add(inClosedOwnedTable.Identifier);
        owners.OwnedTables.Add(hiddenOwned.Identifier);
        owners.OwnedTables.Add(inClosedHiddenOwned.Identifier);

        consents.Active.Add(AllowRead(inClosedReadGrant, Caller, TenantA));
        consents.Active.Add(AllowWrite(inClosedWriteGrant, Caller, TenantA));
        consents.Active.Add(AllowPattern("pat.dbo.match_*", Caller, TenantA));
        consents.Active.Add(AllowRead(hiddenNotOwned, Caller, TenantA));

        var protections = new StubProtection(
            new Dictionary<TableIdentifier, EffectiveProtection>
            {
                [inOpen.Identifier] = ProtectionOf(OpenDomain),
                [inGranted.Identifier] = ProtectionOf(GrantedDomain),
                [inOwnedDomain.Identifier] = ProtectionOf(OwnedDomain),
                [inClosedOwnedTable.Identifier] = ProtectionOf(ClosedDomain),
                [inClosedReadGrant.Identifier] = ProtectionOf(ClosedDomain),
                [inClosedWriteGrant.Identifier] = ProtectionOf(ClosedDomain),
                [inClosedPattern.Identifier] = ProtectionOf(ClosedDomain),
                [inClosedUnmask.Identifier] = ProtectionOf(ClosedDomain, tableMaskCategory: "PII"),
                [inUnassignedUnmask.Identifier] = ProtectionOf(UnassignedDomain, tableMaskCategory: "PII"),
                [hiddenOwned.Identifier] = ProtectionOf(OpenDomain),
                [inClosedHiddenOwned.Identifier] = ProtectionOf(ClosedDomain),
                [inClosed.Identifier] = ProtectionOf(ClosedDomain),
                [inUnassignedOpen.Identifier] = ProtectionOf(UnassignedDomain),
                [inForeignDomain.Identifier] = ProtectionOf(ForeignDomain),
                [foreignOwned.Identifier] = ProtectionOf(OpenDomain),
                [hiddenNotOwned.Identifier] = ProtectionOf(OpenDomain),
            },
            ClosedDomain);

        var unmask = new StubUnmaskResolver(new HashSet<TableIdentifier> { inClosedUnmask.Identifier, inUnassignedUnmask.Identifier });
        var service = CreateService(
            tables,
            consents,
            owners,
            protections,
            new StubContainers(available: true),
            defaultTenantId,
            unmaskResolver: unmask);

        TableMetadata[] expectedVisible = [
            inOpen, inGranted, inOwnedDomain, inClosedOwnedTable, inClosedReadGrant,
            inClosedWriteGrant, inClosedPattern, inClosedUnmask, hiddenOwned, inClosedHiddenOwned
        ];
        TableMetadata[] expectedHidden = [inClosed, inUnassignedOpen, inUnassignedUnmask, inForeignDomain, foreignOwned, hiddenNotOwned];

        await AssertVisibleSetAcrossSurfacesAsync(service, CallerOf(Caller), expectedHidden, expectedVisible);
    }

    [Fact]
    public async Task SeesEverything_subject_sees_closed_own_domain_but_still_excludes_unassigned_and_foreign_owned()
    {
        var tables = new FakeDiscoveryTableRepository();
        var inClosed = DiscoveryTestBuilder.MakeTable("dom", "in_closed", ownerTenantId: null);
        var inUnassigned = DiscoveryTestBuilder.MakeTable("dom", "in_unassigned", ownerTenantId: null);
        var foreignOwned = DiscoveryTestBuilder.MakeTable("dom", "foreign_owned", ownerTenantId: "tenant-b");
        var hiddenTable = DiscoveryTestBuilder.MakeTable("dom", "hidden_in_closed", ownerTenantId: null, hidden: true);
        tables.Tables.AddRange([inClosed, inUnassigned, foreignOwned, hiddenTable]);

        var protections = new StubProtection(
            new Dictionary<TableIdentifier, EffectiveProtection>
            {
                [inClosed.Identifier] = ProtectionOf(ClosedDomain),
                [inUnassigned.Identifier] = ProtectionOf(UnassignedDomain),
                [foreignOwned.Identifier] = ProtectionOf(OpenDomain),
                [hiddenTable.Identifier] = ProtectionOf(ClosedDomain),
            },
            ClosedDomain);

        var service = CreateService(
            tables,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryOwnershipRepository(),
            protections,
            new StubContainers(available: true, isItAdmin: true),
            defaultTenantId: TenantA.Value);

        await AssertVisibleSetAcrossSurfacesAsync(service, CallerOf(Caller), [inUnassigned, foreignOwned, hiddenTable], [inClosed]);
    }

    [Fact]
    public async Task Unavailable_snapshot_hides_open_and_unassigned_across_all_surfaces()
    {
        var tables = new FakeDiscoveryTableRepository();
        var inOpen = DiscoveryTestBuilder.MakeTable("dom", "in_open", ownerTenantId: null);
        var inUnassigned = DiscoveryTestBuilder.MakeTable("dom", "in_unassigned", ownerTenantId: null);
        tables.Tables.AddRange([inOpen, inUnassigned]);

        var service = CreateService(
            tables,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new StubProtection(
                new Dictionary<TableIdentifier, EffectiveProtection>
                {
                    [inOpen.Identifier] = ProtectionOf(OpenDomain),
                    [inUnassigned.Identifier] = ProtectionOf(UnassignedDomain)
                },
                OpenDomain),
            new StubContainers(available: false, isItAdmin: true),
            defaultTenantId: TenantA.Value);

        await AssertVisibleSetAcrossSurfacesAsync(service, CallerOf(Caller), [inOpen, inUnassigned], []);
    }

    [Fact]
    public async Task CatalogChangeIndexer_indexes_own_non_unassigned_domain_tables_and_ignores_unassigned_and_foreign_owned()
    {
        var tables = new FakeDiscoveryTableRepository();
        var inOpen = DiscoveryTestBuilder.MakeTable("dom", "in_open", ownerTenantId: null);
        var inUnassigned = DiscoveryTestBuilder.MakeTable("dom", "in_unassigned", ownerTenantId: null);
        var foreignOwned = DiscoveryTestBuilder.MakeTable("dom", "foreign_owned", ownerTenantId: "tenant-b");
        tables.Tables.AddRange([inOpen, inUnassigned, foreignOwned]);

        var domains = new StubDomainRepo(
        [
            new GovernanceDomain(OpenDomain.Id, TenantA, "open-dom", null, null, null, ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new GovernanceDomain(UnassignedDomain.Id, TenantA, "Unassigned", null, null, null, ProtectionClass.Internal, CatalogVisibilityMode.Open, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        var protections = new StubProtection(
            new Dictionary<TableIdentifier, EffectiveProtection>
            {
                [inOpen.Identifier] = ProtectionOf(OpenDomain),
                [inUnassigned.Identifier] = ProtectionOf(UnassignedDomain),
                [foreignOwned.Identifier] = ProtectionOf(OpenDomain),
            },
            ClosedDomain);
        var changeStore = new InMemoryChangeStore();

        var indexer = new CatalogChangeIndexerHostedService(
            tables,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryOwnershipRepository(),
            changeStore,
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            protectionProvider: protections,
            domainRepository: domains);

        var written = await indexer.IndexTenantAsync(TenantA);
        written.ShouldBe(1);
        changeStore.Versions.Keys.ShouldBe([inOpen.Identifier.ToString()]);
    }

    private static async Task AssertVisibleSetAcrossSurfacesAsync(
        DiscoveryService service,
        CatalogCaller caller,
        IReadOnlyCollection<TableMetadata> hidden,
        IReadOnlyCollection<TableMetadata> expected)
    {
        var expectedIds = expected.Select(static t => t.Identifier.ToString()).OrderBy(static x => x, StringComparer.Ordinal).ToList();

        var list = await service.ListAsync(new DiscoveryQuery(First: 100), caller);
        list.TotalCount.ShouldBe(expectedIds.Count);
        list.Items.Select(static i => $"{i.Domain}.{i.Schema}.{i.Name}").OrderBy(static x => x, StringComparer.Ordinal).ShouldBe(expectedIds);

        var keyset = await service.ListKeysetAsync(new DiscoveryKeysetQuery(First: 100), caller);
        keyset.TotalCount.ShouldBe(expectedIds.Count);
        keyset.Items.Select(static i => $"{i.Object.Domain}.{i.Object.Schema}.{i.Object.Name}").OrderBy(static x => x, StringComparer.Ordinal).ShouldBe(expectedIds);

        var facets = await service.FacetsAsync(null, caller);
        facets.TotalCount.ShouldBe(expectedIds.Count);

        var search = await service.SearchAsync(" ", new DiscoverySearchOptions(Limit: 100), caller);
        search.Select(static i => $"{i.Domain}.{i.Schema}.{i.Name}").OrderBy(static x => x, StringComparer.Ordinal).ShouldBe(expectedIds);

        var textSearch = await service.SearchAsync("dbo", new DiscoverySearchOptions(Limit: 100), caller);
        textSearch.Select(static i => $"{i.Domain}.{i.Schema}.{i.Name}").OrderBy(static x => x, StringComparer.Ordinal).ShouldBe(expectedIds);

        foreach (var visible in expected)
        {
            (await service.GetAsync(visible.Identifier, caller)).ShouldNotBeNull(visible.Identifier.ToString());
        }

        foreach (var invisible in hidden)
        {
            (await service.GetAsync(invisible.Identifier, caller)).ShouldBeNull(invisible.Identifier.ToString());
        }
    }

    private static CatalogCaller CallerOf(string sid) =>
        DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(sid, TenantA.Value), TenantA);

    private static DiscoveryService CreateService(
        ITableMetadataRepository tables,
        IConsentRepository consents,
        Autheris.Application.Governance.Ownership.IContainerOwnership owners,
        IProtectionSnapshotProvider protections,
        IContainerVisibilitySnapshotProvider containers,
        string? defaultTenantId = null,
        IReadOnlyList<string>? sharedTables = null,
        IUnmaskGrantResolver? unmaskResolver = null) =>
        new(
            tables,
            FakeAccessDecisionPoint.AllowAll(),
            consents,
            new FakeDiscoveryApprovalRepository(),
            owners,
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions
            {
                Tenancy = new TenancyOptions { DefaultTenantId = defaultTenantId },
                Catalog = new DataCatalogOptions { SharedTables = [.. sharedTables ?? []] }
            }),
            Options.Create(new CatalogDiscoveryOptions()),
            logger: NullLogger<DiscoveryService>.Instance,
            protectionProvider: protections,
            containerVisibility: containers,
            unmaskResolver: unmaskResolver);

    private static Consent AllowRead(TableMetadata table, string granteeSid, TenantId tenant) => new()
    {
        Id = Guid.NewGuid(),
        TableId = table.Table.Id,
        TableIdentifier = table.Identifier,
        TenantId = tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(granteeSid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
    };

    private static Consent AllowWrite(TableMetadata table, string granteeSid, TenantId tenant) => new()
    {
        Id = Guid.NewGuid(),
        TableId = table.Table.Id,
        TableIdentifier = table.Identifier,
        TenantId = tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(granteeSid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows)]
    };

    private static Consent AllowPattern(string pattern, string granteeSid, TenantId tenant) => new()
    {
        Id = Guid.NewGuid(),
        ObjectPattern = pattern,
        TenantId = tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(granteeSid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
    };

    private static (DiscoveryService Service, TableIdentifier Table) Build(
        string name, ContainerRef container, string? ownerTenant = null, bool snapshotAvailable = true, bool protectionFails = false)
    {
        var tables = new FakeDiscoveryTableRepository();
        var meta = DiscoveryTestBuilder.MakeTable("dom", name, ownerTenantId: ownerTenant);
        tables.Tables.Add(meta);
        var service = CreateService(
            tables,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new StubProtection(container, protectionFails),
            new StubContainers(snapshotAvailable));
        return (service, meta.Identifier);
    }

    private static EffectiveProtection ProtectionOf(ContainerRef container, string? tableMaskCategory = null) => new(
        container,
        OwnerGroupSid: null,
        ProtectionClass.Internal,
        ProtectionClass.Internal,
        TableMaskCategory: tableMaskCategory,
        new Dictionary<string, ColumnProtection>(),
        CatalogVisibilityMode.OnRequest);

    private sealed class StubProtection : IProtectionSnapshotProvider
    {
        private readonly Dictionary<TableIdentifier, EffectiveProtection>? _byTable;
        private readonly ContainerRef _fallback;
        private readonly bool _fails;

        public StubProtection(ContainerRef container, bool fails)
        {
            _fallback = container;
            _fails = fails;
        }

        public StubProtection(Dictionary<TableIdentifier, EffectiveProtection> byTable, ContainerRef fallback)
        {
            _byTable = byTable;
            _fallback = fallback;
        }

        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            _fails
                ? throw new InvalidOperationException("protection store down")
                : ValueTask.FromResult(_byTable is not null && _byTable.TryGetValue(table, out var p) ? p : ProtectionOf(_fallback));
    }

    private sealed class StubContainers(bool available, bool isItAdmin = false) : IContainerVisibilitySnapshotProvider
    {
        public ValueTask<ContainerVisibilitySnapshot> GetAsync(TenantId tenant, ClaimsPrincipal principal, CancellationToken ct = default)
        {
            if (!available || tenant != TenantA)
            {
                return ValueTask.FromResult(ContainerVisibilitySnapshot.Unavailable());
            }

            var containers = new Dictionary<ContainerRef, ContainerInfo>
            {
                [OpenDomain] = new(OpenDomain, "open", null, null, CatalogVisibilityMode.Open, null),
                [ClosedDomain] = new(ClosedDomain, "closed", null, null, CatalogVisibilityMode.OnRequest, null),
                [GrantedDomain] = new(GrantedDomain, "granted", null, null, CatalogVisibilityMode.OnRequest, null),
                [OwnedDomain] = new(OwnedDomain, "owned", null, null, CatalogVisibilityMode.OnRequest, new Sid(Caller)),
                [UnassignedDomain] = new(UnassignedDomain, "Unassigned", null, null, CatalogVisibilityMode.Open, null, IsSystemUnassigned: true),
            };
            var granted = new HashSet<ContainerRef>();
            var owned = new HashSet<ContainerRef>();
            if (principal.GetUserSid() is { } sid && sid.Value == Caller)
            {
                granted.Add(GrantedDomain);
                owned.Add(OwnedDomain);
            }

            return ValueTask.FromResult(new ContainerVisibilitySnapshot(
                containers, granted, owned, new HashSet<TableIdentifier>(), isGovernance: false, isItAdmin: isItAdmin, groupsComplete: true));
        }
    }

    private sealed class StubUnmaskResolver(IReadOnlySet<TableIdentifier> tables) : IUnmaskGrantResolver
    {
        public ValueTask<UnmaskCoverage> ResolveAsync(
            TenantId tenant, IReadOnlyCollection<Sid> subjects, bool groupsComplete, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(groupsComplete && tables.Contains(table)
                ? new UnmaskCoverage(wholeTable: true, columns: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                : UnmaskCoverage.None);
    }

    private sealed class StubDomainRepo(IReadOnlyList<GovernanceDomain> domains) : IGovernanceDomainRepository
    {
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            Task.FromResult(domains);
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

    private sealed class InMemoryChangeStore : ICatalogChangeStore
    {
        public Dictionary<string, CatalogObjectVersionRow> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, CatalogObjectVersionRow>> GetObjectVersionsAsync(TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CatalogObjectVersionRow>>(Versions);

        public Task<long> ApplyVersionChangesAsync(TenantId tenantId, IReadOnlyList<CatalogVersionUpsert> upserts, DateTimeOffset changedAt, CancellationToken ct = default)
        {
            long seq = Versions.Count;
            foreach (var upsert in upserts)
            {
                seq++;
                Versions[upsert.ObjectId] = new CatalogObjectVersionRow(upsert.ObjectId, upsert.ContentHash, seq, upsert.Removed);
            }

            return Task.FromResult(seq);
        }

        public Task<CatalogChangesFeedResult> ReadCatalogChangesAsync(TenantId tenantId, long sinceSeq, int limit, CancellationToken ct = default) =>
            Task.FromResult(new CatalogChangesFeedResult([], 0));

        public Task<IReadOnlyList<TenantId>> ListKnownTenantIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantId>>([TenantA]);
    }
}
