using Autheris.Tests.Common;

namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataSources;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Application.Common.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Application.OData;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #447 WP-4 (REQ-ATR-08, SEC-ATR-08): discovery hides a table whose source does not allow the ingress transport and never errors,
/// so a caller learns neither that the source exists nor which transports it allows. No ingress (admin context) leaves discovery unfiltered.
/// </summary>
[Trait("Category", "Security")]
public sealed class TransportDiscoveryTests
{
    private const string OpenSource = "open_src";
    private const string SecretSource = "secret_src";

    private static HierarchicalDataSourceOptionsResolver NewResolver(DataSourceTransportFlags? defaults = null, DataSourceTransportFlags? secretAllowed = DataSourceTransportFlags.WebSql)
    {
        var options = new DataSourceConfigurationOptions { Defaults = new DataSourceProfileOptions { AllowedTransports = defaults } };
        if (secretAllowed is { } flags)
        {
            options.Profiles[SecretSource] = new DataSourceProfileOptions { AllowedTransports = flags };
        }

        return new HierarchicalDataSourceOptionsResolver(new DataSourceTransportGateTests.FixedMonitor<DataSourceConfigurationOptions>(options));
    }

    private static TableMetadata Row(string source, string name, string domain = "corp")
    {
        var row = Wp0.Table(domain, name, "Short " + name, ["id"]);
        return row with { Table = row.Table with { SourceName = source, SchemaName = "sales" }, Identifier = new TableIdentifier(domain, "sales", name) };
    }

    private static IIngressTransportContext Ingress(DataSourceTransportFlags flags)
    {
        var ingress = new IngressTransportContext();
        if (flags != DataSourceTransportFlags.None)
        {
            ingress.Set(flags);
        }

        return ingress;
    }

    // ----- shared filter -----

    [Fact]
    public void Filter_HidesSourceWhoseProfileDoesNotAllowTransport()
    {
        using var resolver = NewResolver();
        TransportDiscoveryFilter.IsVisible(resolver, DataSourceTransportFlags.GraphQL, Row(SecretSource, "a")).ShouldBeFalse();
        TransportDiscoveryFilter.IsVisible(resolver, DataSourceTransportFlags.WebSql, Row(SecretSource, "a")).ShouldBeTrue();
        TransportDiscoveryFilter.IsVisible(resolver, DataSourceTransportFlags.GraphQL, Row(OpenSource, "b")).ShouldBeTrue();
    }

    [Fact]
    public void Discovery_AdminCatalogUnfiltered()
    {
        using var resolver = NewResolver();
        var rows = new[] { Row(SecretSource, "a"), Row(OpenSource, "b") };
        TransportDiscoveryFilter.Apply(resolver, DataSourceTransportFlags.None, rows).Count.ShouldBe(2);
        TransportDiscoveryFilter.Apply(null, DataSourceTransportFlags.GraphQL, rows).Count.ShouldBe(2);
    }

    [Fact]
    public void Filter_UnprofiledSourceFollowsDefaults()
    {
        using var open = NewResolver(defaults: null, secretAllowed: null);
        TransportDiscoveryFilter.IsVisible(open, DataSourceTransportFlags.Mcp, Row("anything", "a")).ShouldBeTrue();

        using var restricted = NewResolver(defaults: DataSourceTransportFlags.WebSql, secretAllowed: null);
        TransportDiscoveryFilter.IsVisible(restricted, DataSourceTransportFlags.Mcp, Row("anything", "a")).ShouldBeFalse();
    }

    [Fact]
    public void Filter_ResolverFailureHides()
    {
        var resolver = Substitute.For<IDataSourceOptionsResolver>();
        resolver.CanonicalName(Arg.Any<string?>()).Returns(_ => throw new InvalidOperationException("boom"));
        TransportDiscoveryFilter.IsVisible(resolver, DataSourceTransportFlags.OData, Row(OpenSource, "a")).ShouldBeFalse();
    }

    [Fact]
    public void Filter_MultiBitTransportHides()
    {
        using var resolver = NewResolver();
        TransportDiscoveryFilter.IsVisible(resolver, DataSourceTransportFlags.GraphQL | DataSourceTransportFlags.Rest, Row(OpenSource, "a")).ShouldBeFalse();
    }

    // ----- catalog visibility (GraphQL Query.Catalog, CatalogApiEndpoints, MCP dataset list share this service) -----

    private static CatalogVisibilityService Visibility(ITableMetadataRepository tables, IIngressTransportContext? ingress, IDataSourceOptionsResolver? resolver)
    {
        var consents = Wp0.Consents(
            Wp0.Allow(new TableIdentifier("corp", "sales", "a"), "S-USER", Wp0.TenantA, "id"),
            Wp0.Allow(new TableIdentifier("corp", "sales", "b"), "S-USER", Wp0.TenantA, "id"));
        var options = new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Rebac = new RebacOptions { Enabled = false } };
        return new CatalogVisibilityService(
            tables, consents, Options.Create(options), CatalogVisibilityTestFactory.DisabledRebac(), NullLogger<CatalogVisibilityService>.Instance,
            ingress, resolver);
    }

    [Fact]
    public async Task Discovery_CatalogApiOmits()
    {
        using var resolver = NewResolver();
        var secret = Row(SecretSource, "a");
        var open = Row(OpenSource, "b");
        var service = Visibility(Wp0.Tables(secret, open), Ingress(DataSourceTransportFlags.GraphQL), resolver);
        var caller = CatalogVisibilityTestFactory.Caller(Wp0.User("S-USER", "tenant-a"));

        var set = await service.GetVisibleAsync(caller);
        set.Tables.Select(t => t.Identifier.TableName).ShouldBe(["b"]);

        // identical answer for a hidden table and one that does not exist (no oracle)
        (await service.FindVisibleAsync(caller, secret.Identifier)).ShouldBeNull();
        (await service.FindVisibleAsync(caller, new TableIdentifier("corp", "sales", "nope"))).ShouldBeNull();
        (await service.FilterVisibleAsync(caller, [secret.Identifier, open.Identifier])).ShouldBe([open.Identifier]);
    }

    [Fact]
    public async Task Discovery_McpDatasetListOmits_UsesCurrentIngress()
    {
        using var resolver = NewResolver();
        var tables = Wp0.Tables(Row(SecretSource, "a"), Row(OpenSource, "b"));
        var caller = CatalogVisibilityTestFactory.Caller(Wp0.User("S-USER", "tenant-a"));

        (await Visibility(tables, Ingress(DataSourceTransportFlags.Mcp), resolver).GetVisibleAsync(caller)).Tables.Select(t => t.Identifier.TableName).ShouldBe(["b"]);
        (await Visibility(tables, Ingress(DataSourceTransportFlags.WebSql), resolver).GetVisibleAsync(caller)).Tables.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Discovery_AdminCatalogUnfiltered_NoIngress()
    {
        using var resolver = NewResolver();
        var tables = Wp0.Tables(Row(SecretSource, "a"), Row(OpenSource, "b"));
        var caller = CatalogVisibilityTestFactory.Caller(Wp0.User("S-USER", "tenant-a"));

        (await Visibility(tables, Ingress(DataSourceTransportFlags.None), resolver).GetVisibleAsync(caller)).Tables.Count.ShouldBe(2);
        (await Visibility(tables, null, resolver).GetVisibleAsync(caller)).Tables.Count.ShouldBe(2);
    }

    // ----- OData $metadata, service document and OpenAPI -----

    private static ODataHandler ODataHandlerFor(IDataSourceOptionsResolver resolver, DataSourceTransportFlags ingress, params TableMetadata[] tables)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));
        var exec = Substitute.For<IGatewayExecutionService>();
        exec.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(TableAccessDecision.Allowed(
                ci.ArgAt<TableMetadata>(1).Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));
        return new ODataHandler(repo, exec, NullLogger<ODataHandler>.Instance, transportResolver: resolver, ingress: Ingress(ingress));
    }

    private static ClaimsPrincipal ODataUser() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("tenant_id", "corp")], "TestAuth"));

    [Fact]
    public async Task Discovery_ODataMetadataOmits()
    {
        using var resolver = NewResolver(secretAllowed: DataSourceTransportFlags.WebSql);
        var handler = ODataHandlerFor(resolver, DataSourceTransportFlags.OData, Row(SecretSource, "hidden_t"), Row(OpenSource, "visible_t"));

        var csdl = await handler.GetMetadataCsdlAsync(ODataUser());
        csdl.ShouldContain("visible_t");
        csdl.ShouldNotContain("hidden_t");
        csdl.ShouldNotContain(SecretSource);

        var doc = JsonSerializer.Serialize(await handler.GetServiceDocumentAsync("http://localhost/odata/v4", ODataUser()));
        doc.ShouldContain("visible_t");
        doc.ShouldNotContain("hidden_t");
    }

    [Fact]
    public async Task Discovery_ODataMetadata_SourceAllowingODataIsListed()
    {
        using var resolver = NewResolver(secretAllowed: DataSourceTransportFlags.OData);
        var handler = ODataHandlerFor(resolver, DataSourceTransportFlags.OData, Row(SecretSource, "hidden_t"));

        (await handler.GetMetadataCsdlAsync(ODataUser())).ShouldContain("hidden_t");
    }

    [Fact]
    public async Task Discovery_ODataOpenApiOmits()
    {
        using var resolver = NewResolver();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(
            [Row(SecretSource, "hidden_t"), Row(OpenSource, "visible_t")]));
        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance, transportResolver: resolver);

        var json = await generator.GenerateOpenApiJsonAsync(null);
        json.ShouldContain("visible_t");
        json.ShouldNotContain("hidden_t");

        var index = JsonSerializer.Serialize(await generator.GetIndexDocumentAsync("http://localhost"));
        index.ShouldNotContain(SecretSource);

        (await generator.GenerateEntitySchemaJsonAsync(new TableIdentifier("corp", "sales", "hidden_t"))).ShouldBeNull();
        (await generator.GenerateEntitySchemaJsonAsync(new TableIdentifier("corp", "sales", "visible_t"))).ShouldNotBeNull();
    }
}
