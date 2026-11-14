namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
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
    private readonly IOptions<GatewayOptions> _options = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } });
    private readonly IDataSourceTransportGate _transportGate = Substitute.For<IDataSourceTransportGate>();

    private static readonly TableIdentifier OrdersId = new("tenant-1", "raw", "orders");

    private static ClaimsPrincipal Analyst() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "analyst@corp.com"), new Claim(ClaimTypes.PrimarySid, "analyst@corp.com"), new Claim(ClaimTypes.Role, "DataScientist") },
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

    private IcebergRestCatalogFederationService CreateService(
        IOptions<GatewayOptions>? options = null,
        IAuditLogRepository? auditRepository = null,
        IConsentResolutionService? consentService = null,
        IConsentRepository? consentRepo = null,
        Autheris.Application.Policy.Pdp.IAccessDecisionPoint? accessDecisionPoint = null)
    {
        var opt = options ?? _options;
        var adp = accessDecisionPoint ?? new Autheris.Application.Policy.Pdp.AccessDecisionPoint(
            opt,
            consentService,
            consentRepo);

        return new IcebergRestCatalogFederationService(
            _metadataReader,
            _metadataRepo,
            opt,
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            auditRepository ?? global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            adp,
            consentRepo: consentRepo,
            transportGate: _transportGate);
    }

    private IcebergRestCatalogFederationService ServiceWith(TableMetadata meta, params Consent[] consents)
    {
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return CreateService(consentService: new ConsentResolutionService(), consentRepo: repo);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-50-02")]
    public void Federation_service_requires_decision_point()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new IcebergRestCatalogFederationService(
                _metadataReader,
                _metadataRepo,
                _options,
                NullLogger<IcebergRestCatalogFederationService>.Instance,
                global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
                accessDecisionPoint: null!));

        ex.ParamName.ShouldBe("accessDecisionPoint");
    }

    private static List<AuditLogEntry> Audits(IAuditLogRepository audit) =>
        audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditLogRepository.RecordAuditEventAsync))
            .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
            .ToList();

    private static ClaimsPrincipal NoSid() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "analyst@corp.com") }, "Bearer"));

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-10")]
    public async Task Unenforceable_decision_constraints_deny()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(Orders("id")));

        var withRowFilter = Substitute.For<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>();
        withRowFilter.DecideAsync(Arg.Any<Autheris.Application.Policy.Pdp.AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(
                OrdersId, new Dictionary<string, ColumnAccessLevel>(), "region = 'EU'", hasUnconstrainedColumnAllow: true)));
        await Should.ThrowAsync<SecurityException>(() =>
            CreateService(auditRepository: audit, accessDecisionPoint: withRowFilter).LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());

        var withMask = Substitute.For<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>();
        withMask.DecideAsync(Arg.Any<Autheris.Application.Policy.Pdp.AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(
                OrdersId, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Mask }, hasUnconstrainedColumnAllow: true)));
        await Should.ThrowAsync<SecurityException>(() =>
            CreateService(auditRepository: audit, accessDecisionPoint: withMask).LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());

        var records = Audits(audit);
        records.Count.ShouldBe(2);
        records.ShouldAllBe(r => r.EventType == "Iceberg.LoadTable" && r.Decision == "DENY");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AZ-04")]
    public async Task LoadTable_without_sid_is_denied()
    {
        var service = ServiceWith(Orders("id"), AllowFor("analyst@corp.com"));

        await Should.ThrowAsync<SecurityException>(() => service.LoadTableAsync("tenant-1", "raw", "orders", NoSid()).AsTask());
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AZ-04")]
    public async Task VendCredential_without_sid_is_denied()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(Orders("id")));
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(new[] { AllowFor("analyst@corp.com") }));
        var service = CreateService(auditRepository: audit, consentService: new ConsentResolutionService(), consentRepo: repo);

        await Should.ThrowAsync<SecurityException>(() => service.VendCredentialAsync("tenant-1", "raw", "orders", NoSid()).AsTask());

        var records = Audits(audit);
        records.Count.ShouldBe(1);
        records[0].EventType.ShouldBe("Iceberg.VendCredential");
        records[0].Decision.ShouldBe("DENY");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AZ-04")]
    public async Task ListTables_and_ListNamespaces_without_sid_return_nothing()
    {
        var service = ServiceWith(Orders("id"), AllowFor("analyst@corp.com"));
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { Orders("id") }));

        (await service.ListNamespacesAsync("tenant-1", NoSid())).ShouldBeEmpty();
        (await service.ListTablesAsync("tenant-1", "raw", NoSid())).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VendCredential_denied_writes_deny_audit()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(Orders("id")));
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var service = CreateService(auditRepository: audit, consentService: new ConsentResolutionService(), consentRepo: repo);

        await Should.ThrowAsync<SecurityException>(() => service.VendCredentialAsync("tenant-1", "raw", "orders", Analyst()).AsTask());

        var records = audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditLogRepository.RecordAuditEventAsync))
            .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
            .ToList();
        records.Count.ShouldBe(1);
        records[0].EventType.ShouldBe("Iceberg.VendCredential");
        records[0].Decision.ShouldBe("DENY");
    }

    [Fact]
    public async Task LoadTable_WithoutConsentInfrastructure_FailsClosed()
    {
        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(Orders("id")));
        var service = CreateService();

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

        var catalogService = CreateService(auditRepository: Substitute.For<IAuditLogRepository>());

        // Act
        // Wunsch 9: a GovernanceAdmin sees the whole catalog of the tenant, but still never another tenant's tables
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "admin@corp.com"), new Claim(ClaimTypes.PrimarySid, "admin@corp.com"), new Claim(ClaimTypes.Role, "GovernanceAdmin") }, "Bearer"));
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
        var catalogService = CreateService(auditRepository: Substitute.For<IAuditLogRepository>());

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

        var service = CreateService(
            auditRepository: auditRepo,
            consentService: new ConsentResolutionService(),
            consentRepo: consentRepo);

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
}
