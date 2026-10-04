namespace Autheris.Tests.Unit;

using System.Net;
using System.Security.Claims;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public class CasbinRlsPushdownTests
{
    [Fact]
    public async Task Casbin_SimpleRlsPolicy_PopulatesCombinedRowFilterSql()
    {
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-finance");
        var userSid = new Sid("S-1-5-21-accountant");
        var targetTable = new TableIdentifier("finance", "dbo", "invoices");

        service.AddPolicy(
            tenant: tenant,
            sub: "role:finance",
            obj: targetTable.ToString(),
            act: "read",
            subRule: "true",
            eft: "allow",
            rlsFilter: "department = 'Finance'");

        service.AddRoleForUser(tenant, userSid.Value, "role:finance");

        var context = new SecurityEvaluationContext(
            UserSid: userSid,
            GroupSids: [],
            Tenant: tenant,
            TargetTable: targetTable,
            RequestedColumns: ["id", "amount", "department"],
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null);

        var decision = await service.EvaluatePolicyAsync(context);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("department = 'Finance'");
    }

    [Fact]
    public async Task Casbin_DynamicAbacContext_InterpolatesRlsFilterVariables()
    {
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-hr");
        var userSid = new Sid("S-1-5-21-emp-101");
        var targetTable = new TableIdentifier("hr", "dbo", "timesheets");

        service.AddPolicy(
            tenant: tenant,
            sub: userSid.Value,
            obj: targetTable.ToString(),
            act: "read",
            subRule: "r.ctx.Department == 'Engineering'",
            eft: "allow",
            rlsFilter: "owner_sid = '${r.sub}' AND department = '${r.ctx.Department}' AND tenant_id = '${r.tenant}'");

        var attributes = new Dictionary<string, object?>
        {
            ["department"] = "Engineering"
        };

        var context = new SecurityEvaluationContext(
            UserSid: userSid,
            GroupSids: [],
            Tenant: tenant,
            TargetTable: targetTable,
            RequestedColumns: ["id", "hours"],
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: "self-service",
            Attributes: attributes);

        var decision = await service.EvaluatePolicyAsync(context);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("owner_sid = 'S-1-5-21-emp-101' AND department = 'Engineering' AND tenant_id = 'tenant-hr'");
    }

    [Fact]
    public async Task Casbin_MultipleMatchingRules_CombinesWithOrSemantics()
    {
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-eu");
        var userSid = new Sid("S-1-5-21-eu-sales");
        var targetTable = new TableIdentifier("sales", "dbo", "leads");

        service.AddPolicy(tenant, "role:de_sales", targetTable.ToString(), "read", "true", "allow", rlsFilter: "country = 'DE'");
        service.AddPolicy(tenant, "role:at_sales", targetTable.ToString(), "read", "true", "allow", rlsFilter: "country = 'AT'");

        service.AddRoleForUser(tenant, userSid.Value, "role:de_sales");
        service.AddRoleForUser(tenant, userSid.Value, "role:at_sales");

        var context = new SecurityEvaluationContext(
            UserSid: userSid,
            GroupSids: [],
            Tenant: tenant,
            TargetTable: targetTable,
            RequestedColumns: ["id", "lead_name", "country"],
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null);

        var decision = await service.EvaluatePolicyAsync(context);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("(country = 'DE') OR (country = 'AT')");
    }

    [Fact]
    public async Task GatewayExecutionService_PushesDownCasbinRlsFilter_ToSqlExecutor()
    {
        var casbinService = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-corp");
        var userSid = new Sid("S-1-5-21-corp-user");
        var table = new TableIdentifier("corp", "dbo", "assets");

        casbinService.AddPolicy(
            tenant: tenant,
            sub: userSid.Value,
            obj: table.ToString(),
            act: "read",
            subRule: "true",
            eft: "allow",
            rlsFilter: "is_active = 1");

        var metadata = new TableMetadata
        {
            Identifier = table,
            Table = new Table { SchemaName = "dbo", TableName = "assets" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "nvarchar" },
                new TableColumn { ColumnName = "is_active", DataType = "bit" }
            ]
        };

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(table, Arg.Any<CancellationToken>()).Returns(metadata);

        var consentRepo = Substitute.For<IConsentRepository>();
        var allowConsent = new Consent
        {
            Id = Guid.NewGuid(),
            GranteeSid = userSid,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            TenantId = tenant,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent> { allowConsent });

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var resolutionService = new ConsentResolutionService();
        var cacheService = Substitute.For<IConsentCacheService>();
        var maskingProvider = new ColumnMaskingProvider();

        var captureExecutor = new QueryCaptureDataSourceExecutor();

        var gatewayExecutionService = new GatewayExecutionService(
            metadataRepo,
            consentRepo,
            auditRepo,
            resolutionService,
            cacheService,
            maskingProvider,
            dataSourceExecutors: [captureExecutor],
            policyEnforcementService: casbinService);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, userSid.Value),
            new Claim("tenant", tenant.Value)
        ], "TestAuth"));

        // Act
        var result = await gatewayExecutionService.ExecuteTableQueryAsync(principal, table);

        // Assert
        result.Decision.IsAllowed.ShouldBeTrue();
        result.Decision.CombinedRowFilterSql.ShouldNotBeNull();
        result.Decision.CombinedRowFilterSql!.ShouldContain("is_active = 1");
        captureExecutor.LastExecutedContext.ShouldNotBeNull();
        captureExecutor.LastExecutedContext.AccessDecision.CombinedRowFilterSql.ShouldNotBeNull();
        captureExecutor.LastExecutedContext.AccessDecision.CombinedRowFilterSql!.ShouldContain("is_active = 1");
    }

    private sealed class QueryCaptureDataSourceExecutor : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;
        public DataSourceExecutionContext? LastExecutedContext { get; private set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            DataSourceExecutionContext context,
            CancellationToken ct = default)
        {
            LastExecutedContext = context;
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Server-A", ["is_active"] = 1 }
            };
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rows);
        }
    }
}
