namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 A1-3 / A1-4 / A1-5, SEC-112-13/-14/-16/-17, M-4: import order, tenancy and virtual-table schema.</summary>
public sealed class SemanticManifestImportTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private const string ManifestWithFilter = """
    { "version": "v1",
      "semantic_models": [ { "name": "orders", "description": "d", "node_relation": { "relation_name": "analytics.public.orders" },
        "entities": [ { "name": "id", "type": "primary" } ],
        "measures": [ { "name": "order_total", "agg": "sum", "expr": "amount" } ],
        "dimensions": [ { "name": "status", "type": "categorical" } ] } ],
      "metrics": [ { "name": "revenue", "type": "simple", "filter": "FILTER", "dimensions": ["status"], "type_params": { "measure": "order_total" } } ] }
    """;

    private sealed class Rig
    {
        public ISemanticCatalogService Catalog { get; init; } = null!;
        public ITableMetadataRepository Repository { get; init; } = null!;
        public IConsentRepository Consents { get; init; } = null!;
        public List<TableMetadata> Upserts { get; init; } = [];
        public DbtSemanticManifestIngestionService Service { get; init; } = null!;
    }

    private static Rig NewRig(TenancyMode mode, bool tenantsHoldConsent = true, ISemanticCatalogService? catalog = null)
    {
        var options = SemanticCatalogTestKit.Mode(mode);
        catalog ??= new InMemorySemanticCatalog(options);
        var repository = Substitute.For<ITableMetadataRepository>();
        var orders = SemanticManifestValidatorTests.Orders();
        repository.GetTableMetadataAsync(orders.Identifier, Arg.Any<CancellationToken>()).Returns(orders);
        repository.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([orders]);
        var consents = Substitute.For<IConsentRepository>();
        consents.GetTablesWithConsentsForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(tenantsHoldConsent ? [orders.Identifier] : []);
        var upserts = new List<TableMetadata>();
        repository.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(upserts.Add), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<TableMetadata>()));
        return new Rig
        {
            Catalog = catalog,
            Repository = repository,
            Consents = consents,
            Upserts = upserts,
            Service = new DbtSemanticManifestIngestionService(
                catalog, NullLogger<DbtSemanticManifestIngestionService>.Instance, repository,
                gatewayOptions: options, consentRepository: consents)
        };
    }

    private static string Manifest(string filter) => ManifestWithFilter.Replace("FILTER", filter);

    [Fact]
    public async Task Import_StoresManifestAndWritesVirtualTableInTenantSchema_ForTheDefaultTenant()
    {
        var rig = NewRig(TenancyMode.Single);

        await rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantId.LegacySingleTenant, "admin");

        (await rig.Catalog.GetMetricAsync("revenue", TenantId.LegacySingleTenant)).ShouldNotBeNull();
        var table = rig.Upserts.Single();
        table.Identifier.ShouldBe(new TableIdentifier("semantic", SemanticVirtualTables.TenantSchema(TenantId.LegacySingleTenant), "orders"));
        SemanticVirtualTables.IsVirtualTable(table).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VirtualTableSchema_DerivedFromTenantIdOnly()
    {
        var rig = NewRig(TenancyMode.Multi);

        await rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantA);

        var table = rig.Upserts.Single();
        table.Identifier.Domain.ShouldBe("semantic");
        table.Identifier.Schema.ShouldBe(SemanticVirtualTables.TenantSchema(TenantA));
        table.Identifier.Schema.ShouldMatch("^t_[0-9a-f]{16}$");
        table.Table.SchemaName.ShouldBe(table.Identifier.Schema);
        SemanticVirtualTables.TenantSchema(TenantA).ShouldNotBe(SemanticVirtualTables.TenantSchema(TenantB));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ManifestImport_MultiMode_TenantBCannotOverwriteTenantAVirtualTable()
    {
        var rig = NewRig(TenancyMode.Multi);

        await rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantA);
        await rig.Service.ParseAndIngestAsync(Manifest("status = 'y'"), TenantB);

        rig.Upserts.Count.ShouldBe(2);
        rig.Upserts[0].Identifier.ShouldNotBe(rig.Upserts[1].Identifier);
        rig.Upserts[0].Identifier.Schema.ShouldBe(SemanticVirtualTables.TenantSchema(TenantA));
        rig.Upserts[1].Identifier.Schema.ShouldBe(SemanticVirtualTables.TenantSchema(TenantB));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Import_UnsetTenant_IsForbidden_BeforeAnySideEffect()
    {
        var rig = NewRig(TenancyMode.Multi);

        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), default));

        rig.Upserts.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ManifestImport_OneInvalidTemplate_NothingPersisted()
    {
        var rig = NewRig(TenancyMode.Multi);
        await rig.Service.ParseAndIngestAsync(Manifest("status = 'old'"), TenantA);
        rig.Upserts.Clear();

        await Should.ThrowAsync<SemanticManifestInvalidException>(() =>
            rig.Service.ParseAndIngestAsync(Manifest("status = 'x'; DROP TABLE orders"), TenantA));

        rig.Upserts.ShouldBeEmpty();
        (await rig.Catalog.GetManifestVersionAsync(TenantA)).ShouldBe("v1");
        (await rig.Catalog.GetMetricAsync("revenue", TenantA))!.Filter.ShouldBe("status = 'old'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ManifestImport_UnresolvedOrForeignBaseTable_GenericErrorNoNameEcho()
    {
        // The tenant holds no consent on the base table (and it is not shared): the importer's scope cannot see it.
        var rig = NewRig(TenancyMode.Multi, tenantsHoldConsent: false);

        var ex = await Should.ThrowAsync<SemanticManifestInvalidException>(() =>
            rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantA));

        ex.Message.ShouldNotContain("orders");
        ex.Message.ShouldNotContain("analytics");
        string.Join(' ', ex.ErrorCodes).ShouldNotContain("orders");
        rig.Upserts.ShouldBeEmpty();
        (await rig.Catalog.GetMetricAsync("revenue", TenantA)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ManifestImport_MultiMode_VirtualTableIsNeverTheBaseTable()
    {
        var rig = NewRig(TenancyMode.Multi);
        await rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantA);
        var virtualTable = rig.Upserts.Single();
        // Only the tenant's own virtual table is in the repository now; a second import must not resolve it as the base table.
        var onlyVirtual = Substitute.For<ITableMetadataRepository>();
        onlyVirtual.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([virtualTable]);
        var resolver = new SemanticBaseTableResolver(onlyVirtual);

        var resolved = await resolver.TryResolveAsync(new DbtSemanticModel("orders", "d", "orders", [], [], []));

        resolved.ShouldBeNull();
    }

    [Fact]
    public async Task VirtualTableUpsertFailure_AfterSwap_IsPartial_ManifestStaysStored_ReImportRepairs()
    {
        var rig = NewRig(TenancyMode.Multi);
        var calls = 0;
        rig.Repository.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci => ++calls == 1 ? throw new InvalidOperationException("boom") : Task.FromResult(ci.Arg<TableMetadata>()));

        await Should.ThrowAsync<SemanticManifestPartialException>(() =>
            rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantA));

        (await rig.Catalog.GetMetricAsync("revenue", TenantA)).ShouldNotBeNull();
        await rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantA);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task Import_PassesTheImporterToTheCatalog()
    {
        var catalog = Substitute.For<ISemanticCatalogService>();
        var rig = NewRig(TenancyMode.Single, catalog: catalog);

        await rig.Service.ParseAndIngestAsync(Manifest("status = 'x'"), TenantId.LegacySingleTenant, "alice");

        await catalog.Received(1).RegisterManifestAsync(Arg.Any<DbtSemanticManifest>(), TenantId.LegacySingleTenant, "alice", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("name with space")]
    [InlineData("a.b")]
    [InlineData("a\"b")]
    [Trait("Category", "Security")]
    public async Task ManifestImport_ModelNameWithDotOrQuote_Rejected_NothingPersisted(string name)
    {
        var rig = NewRig(TenancyMode.Single);
        var json = Manifest("status = 'x'").Replace("\"name\": \"orders\"", $"\"name\": \"{name.Replace("\"", "\\\"")}\"");

        await Should.ThrowAsync<SemanticManifestInvalidException>(() => rig.Service.ParseAndIngestAsync(json, TenantId.LegacySingleTenant));

        rig.Upserts.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UserSchemaNamedLikeTenantHash_NotTreatedAsVirtualView()
    {
        var real = SemanticManifestValidatorTests.Orders();
        var lookalike = real with
        {
            Identifier = new TableIdentifier("default", SemanticVirtualTables.TenantSchema(TenantA), "orders")
        };
        var otherDomain = lookalike with { Identifier = new TableIdentifier("analytics", SemanticVirtualTables.TenantSchema(TenantA), "orders") };
        var semanticDomainNoMarker = real with
        {
            Identifier = new TableIdentifier("semantic", SemanticVirtualTables.TenantSchema(TenantA), "orders")
        };

        SemanticVirtualTables.IsVirtualTable(lookalike).ShouldBeFalse();
        SemanticVirtualTables.IsVirtualTable(otherDomain).ShouldBeFalse();
        SemanticVirtualTables.IsVirtualTable(semanticDomainNoMarker).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- A1-3 principal rules

    private static ClaimsPrincipal Principal(bool withSid, string? tenant, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "user") };
        if (withSid) claims.Add(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"));
        if (tenant != null) claims.Add(new Claim("tenant_id", tenant));
        if (role != null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Theory]
    [InlineData(TenancyMode.Single)]
    [InlineData(TenancyMode.Multi)]
    [Trait("Category", "Security")]
    public void SemanticStore_NoSid_EmptyInBothModes(TenancyMode mode)
    {
        new SemanticTenancy(mode.Owner()).ResolveCallerTenant(Principal(withSid: false, tenant: "tenant-a")).ShouldBeNull();
        new SemanticTenancy(mode.Owner()).ResolveCallerTenant(null).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SemanticStore_NoSid_Multi_NoAccess() =>
        new SemanticTenancy(TenancyMode.Multi.Owner()).ResolveCallerTenant(Principal(false, "tenant-a", "GovernanceAdmin")).ShouldBeNull();

    [Fact]
    [Trait("Category", "Security")]
    public void SemanticStore_MultiMode_ClaimlessImport_403()
    {
        new SemanticTenancy(TenancyMode.Multi.Owner()).ResolveCallerTenant(Principal(true, null)).ShouldBeNull();
        new SemanticTenancy(TenancyMode.Multi.Owner()).ResolveCallerTenant(Principal(true, null, "GovernanceAdmin")).ShouldBeNull();
        new SemanticTenancy(TenancyMode.Multi.Owner()).ResolveCallerTenant(Principal(true, "tenant-a")).ShouldBe(TenantA);
    }

    [Fact]
    public void SemanticStore_SingleMode_ClaimlessCallerHasStanding() =>
        new SemanticTenancy(TenancyMode.Single.Owner()).ResolveCallerTenant(Principal(true, null)).ShouldBe(TenantId.LegacySingleTenant);
}
