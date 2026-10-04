namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class IcebergRestCatalogSecurityTests
{
    private readonly IIcebergMetadataReader _metadataReader = Substitute.For<IIcebergMetadataReader>();
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IOptions<GatewayOptions> _options = Options.Create(new GatewayOptions());

    [Fact]
    public async Task ListTables_EnforcesTenantIsolation()
    {
        // Arrange
        var table1 = new TableMetadata
        {
            Identifier = new TableIdentifier("tenant-1", "raw", "orders"),
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg }
        };
        var table2 = new TableMetadata
        {
            Identifier = new TableIdentifier("tenant-1", "raw", "customers"),
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "customers", DataSourceType = DataSourceType.LakehouseIceberg }
        };
        var otherTenantTable = new TableMetadata
        {
            Identifier = new TableIdentifier("tenant-2", "raw", "cross_tenant_leak"),
            Table = new Table { SourceName = "tenant-2", SchemaName = "raw", TableName = "cross_tenant_leak", DataSourceType = DataSourceType.LakehouseIceberg }
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { table1, table2, otherTenantTable }));

        var catalogService = new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            _options,
            NullLogger<IcebergRestCatalogFederationService>.Instance);

        // Act
        var tables = await catalogService.ListTablesAsync("tenant-1", "raw");

        // Assert (SEC H-3: Only tenant-1 tables are returned, never tenant-2)
        tables.Count.ShouldBe(2);
        tables.ShouldContain("orders");
        tables.ShouldContain("customers");
        tables.ShouldNotContain("cross_tenant_leak");
    }

    [Fact]
    public async Task LoadTable_WithUnauthorizedUser_ThrowsSecurityException()
    {
        // Arrange
        var catalogService = new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            _options,
            NullLogger<IcebergRestCatalogFederationService>.Instance);

        var unauthenticatedPrincipal = new ClaimsPrincipal(new ClaimsIdentity()); // no identity or permissions

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await catalogService.LoadTableAsync("tenant-1", "finance", "salaries", unauthenticatedPrincipal);
        });
    }

    [Fact]
    public async Task VendCredential_ThrowsNotSupportedException_UntilRealStsIntegrated()
    {
        // Arrange
        var catalogService = new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            _options,
            NullLogger<IcebergRestCatalogFederationService>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "analyst@corp.com"), new Claim(ClaimTypes.Role, "DataScientist") },
            "Bearer"));

        // Act & Assert (SEC H-3: Stop vending fake unverified credentials; returns 501 Not Supported)
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await catalogService.VendCredentialAsync("tenant-1", "sales", "orders", principal);
        });
    }

    [Fact]
    public async Task LoadTable_WithPathTraversalInLocation_ThrowsSecurityException()
    {
        // Arrange
        var tableWithTraversal = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "raw", "orders"),
            Table = new Table { SourceName = "sales", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg, Location = "../../etc/shadow" }
        };

        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(tableWithTraversal));

        var catalogService = new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            _options,
            NullLogger<IcebergRestCatalogFederationService>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "ClusterAdmin") },
            "Bearer"));

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await catalogService.LoadTableAsync("tenant-1", "raw", "orders", principal);
        });
    }
}
