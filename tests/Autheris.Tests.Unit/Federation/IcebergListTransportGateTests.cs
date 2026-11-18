namespace Autheris.Tests.Unit.Federation;

using System.Security.Claims;
using Autheris.Application.Catalog;
using Autheris.Application.DataSources;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Iceberg namespace and table listings hide sources whose transport policy denies the ingress.</summary>
[Trait("Category", "Security")]
public sealed class IcebergListTransportGateTests
{
    private static TableMetadata Lake(string source, string schema, string table) => new()
    {
        Identifier = new TableIdentifier("tenant-1", schema, table),
        Table = new Table { SourceName = source, SchemaName = schema, TableName = table, DataSourceType = DataSourceType.LakehouseIceberg },
        Columns = [new TableColumn { ColumnName = "id" }]
    };

    internal static async Task AssertListHidesDeniedSourcesAsync()
    {
        var items = new[] { Lake("denied_src", "secret", "t1"), Lake("open_src", "open", "t2") };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(items));
        repo.QueryTablesAsync(Arg.Any<CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CatalogTablePage(items, null)));

        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.EnsureCatalogSourceAsync("denied_src", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TransportNotAllowedException("denied_src", DataSourceTransportFlags.Rest)));

        var options = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } });
        var consents = Substitute.For<IConsentRepository>();
        var service = new IcebergRestCatalogFederationService(
            Substitute.For<IIcebergMetadataReader>(), repo, options, NullLogger<IcebergRestCatalogFederationService>.Instance,
            Substitute.For<IAuditLogRepository>(),
            new Autheris.Application.Policy.Pdp.AccessDecisionPoint(options, null, consents),
            consentRepo: consents, transportGate: gate);

        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "admin@corp.com"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "Bearer"));

        var namespaces = await service.ListNamespacesAsync("tenant-1", admin);
        namespaces.ShouldNotContain("secret");
        var tables = await service.ListTablesAsync("tenant-1", "secret", admin);
        tables.ShouldBeEmpty();
    }

    [Fact]
    public Task List_hides_denied_sources() => AssertListHidesDeniedSourcesAsync();
}
