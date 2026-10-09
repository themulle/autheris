namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
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

    private static readonly TableIdentifier OrdersId = new("tenant-1", "raw", "orders");

    private static ClaimsPrincipal Analyst() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "analyst@corp.com"), new Claim(ClaimTypes.Role, "DataScientist") },
        "Bearer"));

    private static TableMetadata Orders(params string[] columns) => new()
    {
        Identifier = OrdersId,
        Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static Consent AllowFor(string sid, string tenant = "tenant-1", DateTimeOffset? validTo = null,
        IReadOnlyList<ConsentColumnRule>? rules = null) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = new TenantId(tenant),
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(sid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = validTo ?? DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = rules ?? Array.Empty<ConsentColumnRule>()
    };

    private IcebergRestCatalogFederationService ServiceWith(TableMetadata meta, params Consent[] consents)
    {
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return new IcebergRestCatalogFederationService(_metadataReader, _metadataRepo, _options,
            NullLogger<IcebergRestCatalogFederationService>.Instance, new ConsentResolutionService(), repo);
    }

    [Fact]
    public async Task LoadTable_WithoutConsentInfrastructure_FailsClosed()
    {
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(Orders("id")));
        var service = new IcebergRestCatalogFederationService(_metadataReader, _metadataRepo, _options,
            NullLogger<IcebergRestCatalogFederationService>.Instance);

        await Should.ThrowAsync<SecurityException>(() => service.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
    }

    [Fact]
    public async Task LoadTable_WithFullActiveConsent_ReturnsMetadata()
    {
        var service = ServiceWith(Orders("id", "name"), AllowFor("analyst@corp.com"));

        var response = await service.LoadTableAsync("tenant-1", "raw", "orders", Analyst());

        response.ShouldNotBeNull();
    }

    [Fact]
    public async Task LoadTable_WithConsentOfOtherTenant_IsDenied()
    {
        var service = ServiceWith(Orders("id"), AllowFor("analyst@corp.com", tenant: "tenant-2"));

        await Should.ThrowAsync<SecurityException>(() => service.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
    }

    [Fact]
    public async Task LoadTable_WithExpiredConsent_IsDenied()
    {
        var service = ServiceWith(Orders("id"), AllowFor("analyst@corp.com", validTo: DateTimeOffset.UtcNow.AddMinutes(-1)));

        await Should.ThrowAsync<SecurityException>(() => service.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
    }

    [Fact]
    public async Task LoadTable_WithColumnLimitedConsent_IsDeniedBecauseUnmentionedColumnsAreNotClear()
    {
        // The consent only releases 'id' in clear; 'ssn' is not mentioned and must not leak through raw metadata.
        var rules = new[] { new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear } };
        var service = ServiceWith(Orders("id", "ssn"), AllowFor("analyst@corp.com", rules: rules));

        await Should.ThrowAsync<SecurityException>(() => service.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
    }

    [Fact]
    public async Task LoadTable_WithCatalogSensitiveColumnWithoutExplicitClear_IsDenied()
    {
        var meta = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg },
            Columns = new[] { new TableColumn { ColumnName = "ssn", IsSensitive = true } }
        };
        var service = ServiceWith(meta, AllowFor("analyst@corp.com"));

        await Should.ThrowAsync<SecurityException>(() => service.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
    }

    [Fact]
    public async Task VendCredential_WithoutConsent_IsDeniedBeforeNotSupported()
    {
        var service = ServiceWith(Orders("id"));

        await Should.ThrowAsync<SecurityException>(() => service.VendCredentialAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
    }

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
        // Wunsch 9: a GovernanceAdmin sees the whole catalog of the tenant, but still never another tenant's tables
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "admin@corp.com"), new Claim(ClaimTypes.Role, "GovernanceAdmin") }, "Bearer"));
        var tables = await catalogService.ListTablesAsync("tenant-1", "raw", admin);

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
        // Arrange: a fully consented caller, so that the consent check passes and only the missing STS integration remains
        var catalogService = ServiceWith(Orders("id"), AllowFor("analyst@corp.com"));

        // Act & Assert (SEC H-3: Stop vending fake unverified credentials; returns 501 Not Supported)
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await catalogService.VendCredentialAsync("tenant-1", "raw", "orders", Analyst());
        });
    }

    [Fact]
    public async Task LoadTable_WithPathTraversalInLocation_ThrowsSecurityException()
    {
        // Arrange: consent is granted, so the location guard is what rejects the request
        var tableWithTraversal = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg, Location = "../../etc/shadow" }
        };
        var catalogService = ServiceWith(tableWithTraversal, AllowFor("analyst@corp.com"));

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await catalogService.LoadTableAsync("tenant-1", "raw", "orders", Analyst());
        });
    }

    [Fact]
    public async Task LoadTable_WhenAllowed_AuditsAllow()
    {
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var meta = Orders("id", "name");
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([AllowFor("analyst@corp.com")]));

        var service = new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            _options,
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            new ConsentResolutionService(),
            consentRepo,
            auditRepository: auditRepo);

        var response = await service.LoadTableAsync("tenant-1", "raw", "orders", Analyst());
        response.ShouldNotBeNull();

        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "Iceberg.LoadTable" &&
                e.Decision == "ALLOW" &&
                e.TargetTable == "tenant-1.raw.orders" &&
                e.TenantId.Value == "tenant-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadTable_WhenRebacEnforced_AndRebacDenies_ThrowsSecurityExceptionAndAuditsDeny()
    {
        var optionsWithRebac = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true, EnforceOnQueryPaths = true }
        });

        var rebac = Substitute.For<Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator>();
        rebac.IsEnabled.Returns(true);
        rebac.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(new RebacCheckResult(false)));

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var meta = Orders("id", "name");
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([AllowFor("analyst@corp.com")]));

        var service = new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            optionsWithRebac,
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            new ConsentResolutionService(),
            consentRepo,
            rebacEvaluator: rebac,
            auditRepository: auditRepo);

        await Should.ThrowAsync<SecurityException>(() =>
            service.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());

        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "Iceberg.LoadTable" &&
                e.Decision == "DENY" &&
                e.TargetTable.Contains("orders")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListTables_WhenRebacEnforced_FiltersOutDeniedTables_ConsistentlyWithLoadTable()
    {
        var optionsWithRebac = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true, EnforceOnQueryPaths = true }
        });

        var ordersMeta = Orders("id", "name");
        var customersId = new TableIdentifier("tenant-1", "raw", "customers");
        var customersMeta = new TableMetadata
        {
            Identifier = customersId,
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "customers", DataSourceType = DataSourceType.LakehouseIceberg },
            Columns = [new TableColumn { ColumnName = "id" }]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([ordersMeta, customersMeta]));
        _metadataRepo.GetTableMetadataAsync(OrdersId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(ordersMeta));
        _metadataRepo.GetTableMetadataAsync(customersId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(customersMeta));

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([
                AllowFor("analyst@corp.com", tenant: "tenant-1"),
                new Consent
                {
                    TableIdentifier = customersId,
                    TenantId = new TenantId("tenant-1"),
                    GranteeType = GranteeType.User,
                    GranteeSid = new Sid("analyst@corp.com"),
                    Effect = ConsentEffect.Allow,
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1)
                }
            ]));
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Is<TableIdentifier>(t => t == OrdersId), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([AllowFor("analyst@corp.com", tenant: "tenant-1")]));
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Is<TableIdentifier>(t => t == customersId), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([
                new Consent
                {
                    TableIdentifier = customersId,
                    TenantId = new TenantId("tenant-1"),
                    GranteeType = GranteeType.User,
                    GranteeSid = new Sid("analyst@corp.com"),
                    Effect = ConsentEffect.Allow,
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1)
                }
            ]));

        // ReBAC: allow orders, deny customers
        var rebac = Substitute.For<Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator>();
        rebac.IsEnabled.Returns(true);
        rebac.CheckAsync(Arg.Is<RebacCheckRequest>(r => r.Object.Contains("orders")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(RebacCheckResult.Permitted));
        rebac.CheckAsync(Arg.Is<RebacCheckRequest>(r => r.Object.Contains("customers")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(RebacCheckResult.Denied));

        var service = new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            optionsWithRebac,
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            new ConsentResolutionService(),
            consentRepo,
            rebacEvaluator: rebac);

        // 1. ListTablesAsync should only list 'orders' because 'customers' is denied by ReBAC
        var listed = await service.ListTablesAsync("tenant-1", "raw", Analyst());
        listed.ShouldContain("orders");
        listed.ShouldNotContain("customers");

        // 2. LoadTableAsync for 'orders' succeeds (consistent with listing)
        var loaded = await service.LoadTableAsync("tenant-1", "raw", "orders", Analyst());
        loaded.ShouldNotBeNull();

        // 3. LoadTableAsync for 'customers' fails closed with 403 (consistent with exclusion from listing)
        await Should.ThrowAsync<SecurityException>(() =>
            service.LoadTableAsync("tenant-1", "raw", "customers", Analyst()).AsTask());
    }
}
