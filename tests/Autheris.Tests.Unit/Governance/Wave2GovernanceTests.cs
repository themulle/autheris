using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Wave 2 Governance Tests:
/// - Issue #40: GitOps / Declarative Consent Apply API (dryRun, idempotent apply, WORM audit)
/// - Issue #35: MCP describe_my_access & GraphQL myAccess introspection
/// - Issue #34: Access Request Queries & Approver Inbox (myAccessRequests, pendingApprovalsForMe, myActiveConsents)
/// - Issue #26: Scope-specific masks/denies (monotonic hardening)
/// - Issue #29: App.Read & per-scope quota/budgets
/// </summary>
public sealed class Wave2GovernanceTests
{
    [Fact]
    public async Task Issue40_DeclarativeConsentApply_DryRun_DoesNotPersist_ReturnsDiff()
    {
        // Arrange
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();

        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new TableMetadata
            {
                Identifier = new TableIdentifier("default", "sales", "orders"),
                Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "secret_notes" }]
            });

        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var service = new DeclarativeConsentApplyService(tableRepo, consentRepo, auditRepo);

        var manifest = new DeclarativeConsentManifest(
            "1.0",
            [
                new DeclarativeConsentPolicy(
                    "default",
                    "sales",
                    "orders",
                    "User",
                    "S-1-5-21-USER1",
                    "Allow",
                    ColumnRules: [new DeclarativeColumnRule("secret_notes", "Mask", ["Agent.Read"])],
                    RowFilters: [new DeclarativeRowFilter("tenant_id", "EQ", "string", "[\"tenant_test\"]")],
                    ValidUntil: DateTimeOffset.UtcNow.AddDays(30)
                )
            ]);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Name, "AdminUser"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")],
            "test"));

        // Act
        var result = await service.ApplyManifestAsync(user, manifest, dryRun: true);

        // Assert
        result.DryRun.ShouldBeTrue();
        result.TotalEvaluated.ShouldBe(1);
        result.Applied.ShouldBe(1);
        result.Unchanged.ShouldBe(0);

        // Verify that NO consents were created and NO audit logs were recorded during DryRun
        await consentRepo.DidNotReceiveWithAnyArgs().CreateConsentAsync(default!);
        await auditRepo.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!);
    }

    [Fact]
    public async Task Issue40_DeclarativeConsentApply_Applies_And_Records_WormAudit()
    {
        // Arrange
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();

        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new TableMetadata
            {
                Identifier = new TableIdentifier("default", "sales", "orders"),
                Columns = [new TableColumn { ColumnName = "id" }]
            });

        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var service = new DeclarativeConsentApplyService(tableRepo, consentRepo, auditRepo);

        var manifest = new DeclarativeConsentManifest(
            "1.0",
            [
                new DeclarativeConsentPolicy(
                    "default",
                    "sales",
                    "orders",
                    "Role",
                    "DataScientist",
                    "Allow"
                )
            ]);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Name, "AdminUser"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")],
            "test"));

        // Act
        var result = await service.ApplyManifestAsync(user, manifest, dryRun: false);

        // Assert
        result.DryRun.ShouldBeFalse();
        result.Applied.ShouldBe(1);

        // Verify create and audit were called
        await consentRepo.Received(1).ReplaceConsentsAsync(
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Is<IReadOnlyList<Consent>>(l => l.Count == 1 &&
                l[0].RoleName == "DataScientist" &&
                l[0].GranteeType == GranteeType.Role &&
                l[0].Effect == ConsentEffect.Allow),
            Arg.Any<Sid>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<AuditLogEntry>?>(a => a != null && a.Count == 1 && a[0].EventType == "GITOPS_CONSENT_APPLY"),
            Arg.Any<CancellationToken>());

        // the audit is written inside the replace transaction, no longer separately after the commit
        await auditRepo.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!);
    }

    [Fact]
    public async Task Issue35_EffectiveAccessExplanation_Explains_AccessDetails()
    {
        // Arrange
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var resolutionService = Substitute.For<IConsentResolutionService>();

        var tableId = new TableIdentifier("default", "public", "customers");
        tableRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new TableMetadata
            {
                Identifier = tableId,
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "int" },
                    new TableColumn { ColumnName = "email", DataType = "varchar" },
                    new TableColumn { ColumnName = "ssn", DataType = "varchar" }
                ]
            });

        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        resolutionService.ResolveAccess(
            Arg.Any<Sid>(),
            Arg.Any<IReadOnlySet<Sid>>(),
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<IReadOnlyList<Consent>>(),
            Arg.Any<DatabaseDialect>(),
            Arg.Any<IReadOnlySet<Sid>?>(),
            Arg.Any<IReadOnlySet<string>?>())
            .Returns(TableAccessDecision.Allowed(
                tableId,
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Mask,
                    ["ssn"] = ColumnAccessLevel.Deny
                },
                rowFilterSql: "country = 'US'",
                hasUnconstrainedColumnAllow: false));

        var service = new EffectiveAccessExplanationService(
            tableRepo,
            consentRepo,
            resolutionService);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [DefaultTenantCaller.Claim, 
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
                new Claim("scp", "Agent.Read")
            ],
            "test"));

        // Act
        var explanation = await service.ExplainMyAccessAsync(user, tableId);

        // Assert
        explanation.Decision.ShouldBe("ALLOWED");
        explanation.ActiveRowFilters.ShouldNotContain("country = 'US'"); // issue #117: no SQL for ordinary callers
        explanation.ActiveRowFilters.Count.ShouldBe(1);
        explanation.MaskedColumns.ShouldContain("email");
        explanation.DeniedColumns.ShouldContain("ssn");
        explanation.AllowedColumns.ShouldContain("id");
    }

    [Fact]
    public async Task Issue35_McpDatasetCatalog_DescribeMyAccessAsync_FormatsStructuredDetails()
    {
        // Arrange
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var executionService = Substitute.For<IGatewayExecutionService>();
        var explanationService = Substitute.For<IEffectiveAccessExplanationService>();

        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new TableMetadata { Identifier = new TableIdentifier("default", "public", "customers") });

        explanationService.ExplainMyAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveTableAccessDto(
                "default.public.customers",
                "ALLOWED",
                ["id"],
                ["email"],
                ["ssn"],
                ["country = 'US'"],
                "Active consent granted",
                DateTimeOffset.UtcNow.AddDays(7)));

        var catalog = new McpDatasetCatalog(
            tableRepo,
            consentRepo,
            executionService,
            goldenQueryService: null,
            options: null,
            graphQlMap: null,
            explanationService: explanationService,
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(tableRepo, consentRepo));

        var user = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "test"));

        // Act
        var accessInfo = await catalog.DescribeMyAccessAsync(user, "default.public.customers");

        // Assert
        accessInfo.ShouldNotBeNull();
        accessInfo.Table.ShouldBe("default.public.customers");
        accessInfo.Decision.ShouldBe("ALLOWED");
        accessInfo.AllowedColumns.ShouldContain("id");
        accessInfo.MaskedColumns.ShouldContain("email");
        accessInfo.DeniedColumns.ShouldContain("ssn");
        accessInfo.ActiveRowFilters.ShouldContain("country = 'US'");
    }

    [Fact]
    public async Task Issue34_SqliteGovernanceRepository_GetRequestsByRequesterSidAsync_FiltersByStatus()
    {
        // Arrange
        var epochService = Substitute.For<IEpochValidationService>();
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source=governance_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            }
        });

        var repo = GovernanceStoreFactory.CreateSqlite(epochService, options, logger: NullLogger<SqliteGovernanceRepository>.Instance);

        var requesterSid = new Sid("S-1-5-21-REQUESTER-1");
        var tableIdentifier = new TableIdentifier("default", "sales", "invoices");

        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableIdentifier,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        });

        var tableId = meta.Table.Id;

        var req1 = new ConsentRequest
        {
            TableId = tableId,
            TableIdentifier = tableIdentifier,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Audit invoices",
            Status = "PENDING_APPROVAL",
            TenantId = new TenantId("tenant-1")
        };

        var req2 = new ConsentRequest
        {
            TableId = tableId,
            TableIdentifier = tableIdentifier,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Old request",
            Status = "APPROVED",
            TenantId = new TenantId("tenant-1")
        };

        await repo.CreateConsentRequestAsync(req1);
        await repo.CreateConsentRequestAsync(req2);

        // Act
        var allRequests = await repo.GetRequestsByRequesterSidAsync(requesterSid);
        var pendingRequests = await repo.GetRequestsByRequesterSidAsync(requesterSid, status: "PENDING_APPROVAL");

        // Assert
        allRequests.Count.ShouldBe(2);
        pendingRequests.Count.ShouldBe(1);
        pendingRequests[0].Status.ShouldBe("PENDING_APPROVAL");
        pendingRequests[0].BusinessJustification.ShouldBe("Audit invoices");
    }

    [Fact]
    public void Issue26_ConsentResolution_ScopeSpecificMask_EnforcesMonotonicHardening()
    {
        // Arrange
        var resolver = new ConsentResolutionService();
        var userSid = new Sid("S-1-5-21-TEST");
        var table = new TableIdentifier("default", "hr", "salaries");

        // Consent allows salary column clear for normal access, but masks for Agent.Read
        var consent = new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = table,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Mask, ApplicableScopes = ["Agent.Read"] }
            ]
        };

        // Act 1: Caller with no scopes or App.Read scope -> salary should be Clear
        var normalDecision = resolver.ResolveAccess(
            userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            table,
            [consent],
            callerScopes: new HashSet<string> { "App.Read" });

        // Act 2: Caller with Agent.Read scope -> salary should be hardened to Mask
        var agentDecision = resolver.ResolveAccess(
            userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            table,
            [consent],
            callerScopes: new HashSet<string> { "Agent.Read" });

        // Assert
        normalDecision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Clear);
        agentDecision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void Issue29_GatewayOptions_Defaults_Include_AppRead_And_ScopeBudgets()
    {
        // Arrange
        var options = new GatewayOptions();

        // Assert
        options.Authentication.EntraId.GetEffectiveReadOnlyScopes().ShouldContain("Agent.Read");
        options.Authentication.EntraId.GetEffectiveReadOnlyScopes().ShouldContain("App.Read");

        options.FinOps.ScopeMonthlyBudgets.ShouldNotBeNull();
        options.FinOps.ScopeMonthlyBudgets["Agent.Read"] = 250.0m;
        options.FinOps.ScopeMonthlyBudgets["Agent.Read"].ShouldBe(250.0m);
    }
}
