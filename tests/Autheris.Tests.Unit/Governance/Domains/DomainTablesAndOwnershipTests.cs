namespace Autheris.Tests.Unit.Governance.Domains;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Identity;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DomainTablesAndOwnershipTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid GovSid = new("S-1-5-21-1001-10");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-1001-512");
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static SecurityPrincipalContext MakeCaller(Sid sid, TenantId tenant, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("tenant_id", tenant.Value),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: tenant) with
        {
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
    }

    private static (GovernanceDomainService Service, IGovernanceDomainRepository Domains, InMemoryTableMetadataRepository Tables) BuildDomainService()
    {
        var domains = Substitute.For<IGovernanceDomainRepository>();
        var tables = new InMemoryTableMetadataRepository();
        var consents = Substitute.For<IConsentRepository>();
        var audit = new InMemoryAuditWriter();
        var caps = new GovernanceCapabilities(domainRepository: domains);
        var service = new GovernanceDomainService(domains, tables, consents, audit, caps, timeProvider: new FakeTimeProvider(T0));
        return (service, domains, tables);
    }

    [Fact]
    public async Task AssignTables_AcceptsDatasetIdentifiers_AndMergesWithTableIds()
    {
        var (service, domains, tables) = BuildDomainService();
        var domainId = Guid.NewGuid();
        var table1Id = Guid.NewGuid();
        var table2Id = Guid.NewGuid();
        var ds1 = new TableIdentifier("demo", "main", "auftraege");
        var ds2 = new TableIdentifier("demo", "main", "kunden");

        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "Demo", null, "Paul Neumann", OwnerGroupSid, ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 1, T0, T0));

        tables.Add(new TableMetadata
        {
            Identifier = ds1,
            OwnerTenantId = null,
            Table = new Table { Id = table1Id, SourceName = "demo", SchemaName = "main", TableName = "auftraege", IsActive = true }
        });
        tables.Add(new TableMetadata
        {
            Identifier = ds2,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = table2Id, SourceName = "demo", SchemaName = "main", TableName = "kunden", IsActive = true }
        });

        IReadOnlyList<Guid>? persisted = null;
        domains.When(d => d.AssignTablesAsync(TenantA, domainId, Arg.Any<IReadOnlyList<Guid>>(), GovSid, Arg.Any<CancellationToken>()))
            .Do(ci => persisted = ci.ArgAt<IReadOnlyList<Guid>>(2));

        var gov = MakeCaller(GovSid, TenantA, "GovernanceAdmin");
        var res = await service.AssignTablesAsync(
            gov,
            TenantA,
            domainId,
            tableIds: [table2Id],
            datasets: ["demo.main.auftraege", "DEMO.MAIN.KUNDEN"]);

        res.Status.ShouldBe(DomainServiceStatus.Ok);
        res.Value.ShouldBe(2);
        persisted.ShouldNotBeNull();
        persisted!.ShouldBe([table2Id, table1Id], ignoreOrder: true);
    }

    [Fact]
    public async Task AssignTables_RejectsInvalidDatasetFormat_AndCrossTenantDatasets()
    {
        var (service, domains, tables) = BuildDomainService();
        var domainId = Guid.NewGuid();
        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "Demo", null, null, null, ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 1, T0, T0));

        tables.Add(new TableMetadata
        {
            Identifier = new TableIdentifier("other", "main", "secret"),
            OwnerTenantId = TenantB.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "other", SchemaName = "main", TableName = "secret", IsActive = true }
        });

        var gov = MakeCaller(GovSid, TenantA, "GovernanceAdmin");

        // Malformed dataset id -> Invalid (422)
        var bad = await service.AssignTablesAsync(gov, TenantA, domainId, tableIds: null, datasets: ["not-a-three-part-name"]);
        bad.Status.ShouldBe(DomainServiceStatus.Invalid);
        bad.ErrorCode.ShouldBe("INVALID_TABLE");

        // Foreign tenant dataset -> NotFound (404), fail-closed
        var foreign = await service.AssignTablesAsync(gov, TenantA, domainId, tableIds: null, datasets: ["other.main.secret"]);
        foreign.Status.ShouldBe(DomainServiceStatus.NotFound);

        // Neither tableIds nor datasets -> Invalid
        var neither = await service.AssignTablesAsync(gov, TenantA, domainId, tableIds: null, datasets: null);
        neither.Status.ShouldBe(DomainServiceStatus.Invalid);
    }

    [Fact]
    public async Task GetDomainEndpoint_IncludesAssignedTablesAndTruncationFlag()
    {
        var (service, domains, _) = BuildDomainService();
        var domainId = Guid.NewGuid();
        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "Demo", "Demo domain", "Paul Neumann", OwnerGroupSid, ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 2, T0, T0));
        domains.ListDomainTablesAsync(TenantA, domainId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new TableIdentifier("demo", "main", "kunden"), new TableIdentifier("demo", "main", "auftraege")]);

        var services = new ServiceCollection();
        services.AddLogging();
        await using var sp = services.BuildServiceProvider();

        var sec = MakeCaller(GovSid, TenantA, "GovernanceAdmin");
        var http = new DefaultHttpContext
        {
            RequestServices = sp,
            User = sec.Principal!
        };
        http.Items[SecurityPrincipalContext.ItemKey] = sec;
        http.Items[SecurityPrincipalContext.TenantIdItemKey] = TenantA;
        http.Response.Body = new MemoryStream();

        var result = await GovernanceDomainEndpoints.GetDomainAsync(http, domainId, service, CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        http.Response.Body.Position = 0;
        var json = JsonDocument.Parse(await new StreamReader(http.Response.Body).ReadToEndAsync()).RootElement;
        json.GetProperty("tables").EnumerateArray().Select(x => x.GetString()).ToArray()
            .ShouldBe(["demo.main.auftraege", "demo.main.kunden"]);
        json.GetProperty("tablesTruncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ContainerOwnership_ResolvesDomainOwnerWithoutCatalogDefaultTenant_WhenCallerTenantSupplied()
    {
        var tableId = Guid.NewGuid();
        var tableRef = new TableIdentifier("demo", "main", "auftraege");
        var domainId = Guid.NewGuid();

        var meta = new TableMetadata
        {
            Identifier = tableRef,
            OwnerTenantId = null, // dbt-synced table with no owner tenant
            Table = new Table { Id = tableId, SourceName = "demo", SchemaName = "main", TableName = "auftraege", IsActive = true }
        };

        var domains = Substitute.For<IGovernanceDomainRepository>();
        domains.GetContainersForTablesAsync(TenantA, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ContainerDefaults>
            {
                [tableId] = new ContainerDefaults(
                    new ContainerRef(ContainerKind.Domain, domainId),
                    OwnerGroupSid,
                    ProtectionClass.Internal,
                    CatalogVisibilityMode.Open,
                    IsValid: true)
            });
        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "Demo", null, "Paul Neumann", OwnerGroupSid, ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 1, T0, T0));

        // No catalogOwnerTenant configured (null)
        var ownership = new ContainerOwnership(
            new InMemoryTableMetadataRepository(meta),
            domains,
            catalogOwnerTenant: null);

        // Without caller tenant, still fail-closed (empty)
        (await ownership.GetDataOwnersForTableAsync(tableRef)).ShouldBeEmpty();

        // With caller tenant T through the IContainerOwnership extension, domain owner and contact are resolved!
        IContainerOwnership asInterface = ownership;
        var single = await asInterface.GetDataOwnersForTableInTenantAsync(tableRef, TenantA);
        single.Count.ShouldBe(1);
        single[0].AdSid.ShouldBe(OwnerGroupSid);
        single[0].DisplayName.ShouldBe("Paul Neumann");

        var batchById = await asInterface.GetDataOwnersForTablesInTenantAsync([tableRef], TenantA);
        batchById[tableRef].Count.ShouldBe(1);
        batchById[tableRef][0].DisplayName.ShouldBe("Paul Neumann");

        var batchByMeta = await asInterface.GetDataOwnersForTableMetadataInTenantAsync([meta], TenantA);
        batchByMeta[tableRef].Count.ShouldBe(1);
        batchByMeta[tableRef][0].DisplayName.ShouldBe("Paul Neumann");
    }

    [Fact]
    public async Task ContainerOwnership_NeverLeaksExplicitOwnerTenantToDifferentCallerTenant()
    {
        var tableId = Guid.NewGuid();
        var tableRef = new TableIdentifier("sales", "main", "orders");
        var domainId = Guid.NewGuid();

        var meta = new TableMetadata
        {
            Identifier = tableRef,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = tableId, SourceName = "sales", SchemaName = "main", TableName = "orders", IsActive = true }
        };

        var domains = Substitute.For<IGovernanceDomainRepository>();
        domains.GetContainersForTablesAsync(TenantA, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ContainerDefaults>
            {
                [tableId] = new ContainerDefaults(
                    new ContainerRef(ContainerKind.Domain, domainId),
                    OwnerGroupSid,
                    ProtectionClass.Internal,
                    CatalogVisibilityMode.Open,
                    IsValid: true)
            });

        var ownership = new ContainerOwnership(new InMemoryTableMetadataRepository(meta), domains, catalogOwnerTenant: null);

        // Caller from TenantB must NOT see TenantA's owner
        (await ownership.GetDataOwnersForTableAsync(tableRef, TenantB)).ShouldBeEmpty();
        (await ownership.GetDataOwnersForTableMetadataAsync([meta], TenantB))[tableRef].ShouldBeEmpty();
        (await ownership.GetDataOwnersForTableAsync(tableRef, TenantA)).Count.ShouldBe(1);
    }
}
