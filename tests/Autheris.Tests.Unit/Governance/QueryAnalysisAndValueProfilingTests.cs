namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public sealed class QueryAnalysisAndValueProfilingTests
{
    private static ClaimsPrincipal CreatePrincipal(string sid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sid),
            new(ClaimTypes.Name, sid),
            new("sid", sid)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims.Append(DefaultTenantCaller.Claim), "TestAuth"));
    }

    private static TableMetadata CreateCatalogTable(
        string domain,
        string schema,
        string name,
        DatasetVisibility visibility,
        IReadOnlyList<TableColumn> columns,
        string? rlsExpression = null,
        long? estimatedRowCount = 5000,
        IReadOnlyDictionary<string, MaskingRule>? maskingRules = null)
    {
        var id = new TableIdentifier(domain, schema, name);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = domain,
                SchemaName = schema,
                TableName = name,
                DisplayName = name,
                Visibility = visibility,
                RlsExpression = rlsExpression
            },
            Columns = columns,
            ColumnMaskingRules = maskingRules ?? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase),
            PrimaryKeyColumns = columns.Any(c => string.Equals(c.ColumnName, "id", StringComparison.OrdinalIgnoreCase))
                ? new[] { "id" }
                : Array.Empty<string>(),
            EstimatedRowCount = estimatedRowCount
        };
    }

    // =========================================================================
    // Issue #79 — Criterion 1: Valid and invalid GraphQL & SQL queries analyzed
    // with zero source database reads
    // =========================================================================

    [Fact]
    public async Task AnalyzeQueriesAsync_ValidAndInvalidGraphQlAndSql_ReturnsExpectedReportWithoutSourceReads()
    {
        var ordersTable = CreateCatalogTable(
            "sales",
            "public",
            "orders",
            DatasetVisibility.Full,
            new[]
            {
                new TableColumn { ColumnName = "id", DataType = "uuid" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "total_amount", DataType = "decimal" },
                new TableColumn
                {
                    ColumnName = "legacy_code",
                    DataType = "varchar",
                    Meta = new Dictionary<string, string>
                    {
                        ["deprecated_since"] = "2026-01-01T00:00:00Z",
                        ["sunset_date"] = "2026-06-01T00:00:00Z",
                        ["replacement_field"] = "order_code",
                        ["deprecation_reason"] = "Migrated to standardized order code"
                    }
                }
            },
            rlsExpression: "tenant_id = current_setting('app.tenant_id')");

        var metadataRepo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { ordersTable });

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Consent
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    TableIdentifier = ordersTable.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("user-1"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(10)
                }
            });

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            analysisOptions: new QueryAnalysisOptions
            {
                MaxAllowedCost = 500,
                MandatoryVirtualFilters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sales.public.orders"] = new[] { "region" }
                }
            });

        var caller = CreatePrincipal("user-1");

        // 1. Valid GraphQL + Valid SQL
        var validResult = await service.AnalyzeQueriesAsync(
            new[]
            {
                QueryAnalysisItem.FromGraphQl("query { orders(where: { region_eq: \"EU\" }) { id total_amount legacy_code } }", name: "gql_valid"),
                QueryAnalysisItem.FromSql("SELECT id, total_amount FROM sales.public.orders WHERE region = 'EU' LIMIT 10", name: "sql_valid")
            },
            caller);

        validResult.SourceDatabaseReadsExecuted.Should().Be(0);
        validResult.ValidationReport.IsValid.Should().BeTrue();
        validResult.ValidationReport.HasRowFilters.Should().BeTrue();
        validResult.ValidationReport.HasMandatoryVirtualFilters.Should().BeTrue();
        validResult.ValidationReport.CostEstimate.IsWithinCostLimit.Should().BeTrue();
        validResult.ValidationReport.OutputColumns.Should().Contain(c => c.Column == "total_amount" && c.EffectiveAccessLevel == ColumnAccessLevel.Clear);
        validResult.ValidationReport.DeprecatedFields.Should().ContainSingle(d =>
            d.Column == "legacy_code" && d.ReplacementField == "order_code");

        // 2. Invalid GraphQL syntax + Invalid SQL unknown column
        var invalidResult = await service.AnalyzeQueriesAsync(
            new[]
            {
                QueryAnalysisItem.FromGraphQl("query { orders { id ", name: "gql_syntax_err"),
                QueryAnalysisItem.FromSql("SELECT id, non_existent_col FROM sales.public.orders", name: "sql_unknown_col")
            },
            caller);

        invalidResult.SourceDatabaseReadsExecuted.Should().Be(0);
        invalidResult.ValidationReport.IsValid.Should().BeFalse();
        invalidResult.ValidationReport.Diagnostics.Should().Contain(d =>
            d.QueryName == "gql_syntax_err" && d.Code == QueryDiagnosticCodes.SyntaxError);
        invalidResult.ValidationReport.Diagnostics.Should().Contain(d =>
            d.QueryName == "sql_unknown_col" && d.Code == QueryDiagnosticCodes.UnknownColumn && d.Column == "non_existent_col");
    }

    // =========================================================================
    // Issue #79 — Criterion 2: Missing table consent, masked column exception,
    // and Hidden table non-disclosure
    // =========================================================================

    [Fact]
    public async Task AnalyzeQueriesAsync_MissingConsentMaskedColumnAndHiddenTable_ReportsRequirementsWithoutNamingHiddenTable()
    {
        var customersTable = CreateCatalogTable(
            "crm",
            "public",
            "customers",
            DatasetVisibility.Full,
            new[]
            {
                new TableColumn { ColumnName = "id", DataType = "uuid" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            }).WithPii("email");

        var invoicesTable = CreateCatalogTable(
            "billing",
            "public",
            "invoices",
            DatasetVisibility.Summary,
            new[]
            {
                new TableColumn { ColumnName = "id", DataType = "uuid" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            });

        var secretSalariesTable = CreateCatalogTable(
            "hr",
            "private",
            "secret_salaries",
            DatasetVisibility.Hidden,
            new[]
            {
                new TableColumn { ColumnName = "id", DataType = "uuid" },
                new TableColumn { ColumnName = "base_salary", DataType = "decimal" }
            }).WithPii("base_salary");

        var metadataRepo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { customersTable, invoicesTable, secretSalariesTable });

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Consent
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    TableIdentifier = customersTable.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("analyst-1"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(10)
                }
            });

        var service = new QueryAnalysisService(metadataRepo, consentRepo);
        var caller = CreatePrincipal("analyst-1");

        var result = await service.AnalyzeQueriesAsync(
            new[]
            {
                QueryAnalysisItem.FromSql("SELECT id, email FROM crm.public.customers", name: "q_customers"),
                QueryAnalysisItem.FromSql("SELECT id, amount FROM billing.public.invoices", name: "q_invoices"),
                QueryAnalysisItem.FromSql("SELECT id, base_salary FROM hr.private.secret_salaries", name: "q_hidden")
            },
            caller);

        // Missing table grant for Summary-visible invoices table
        result.AccessRequirements.MissingTableGrants.Should().ContainSingle(m =>
            m.Dataset == "billing.public.invoices" && m.RequestedLevel == "READ");

        // Masked column cleartext exception for customers.email
        result.AccessRequirements.MaskedColumnExceptions.Should().ContainSingle(m =>
            m.Dataset == "crm.public.customers" && m.Column == "email" && m.RequestedLevel == "READ_CLEARTEXT");

        // Hidden table must NEVER be named in MissingConsents, OutputColumns, or Diagnostics
        result.AccessRequirements.HiddenMissingDatasetsCount.Should().Be(1);
        result.AccessRequirements.MissingConsents.Should().NotContain(m =>
            m.Dataset.Contains("secret_salaries", StringComparison.OrdinalIgnoreCase));
        result.ValidationReport.OutputColumns.Should().NotContain(c =>
            c.Dataset.Contains("secret_salaries", StringComparison.OrdinalIgnoreCase));
        result.ValidationReport.Diagnostics.Should().NotContain(d =>
            (d.Dataset ?? string.Empty).Contains("secret_salaries", StringComparison.OrdinalIgnoreCase) ||
            d.Message.Contains("secret_salaries", StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // Issue #79 — Criterion 3: Masked column in WHERE or JOIN ... ON is flagged
    // as an error (MASKED_COLUMN_IN_FILTER)
    // =========================================================================

    [Fact]
    public async Task AnalyzeQueriesAsync_MaskedColumnInWhereOrJoinOn_FlagsMaskedColumnInFilterDiagnostic()
    {
        var usersTable = CreateCatalogTable(
            "iam",
            "public",
            "users",
            DatasetVisibility.Full,
            new[]
            {
                new TableColumn { ColumnName = "id", DataType = "uuid" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar" }
            }).WithPii("ssn");

        var auditTable = CreateCatalogTable(
            "iam",
            "public",
            "user_events",
            DatasetVisibility.Full,
            new[]
            {
                new TableColumn { ColumnName = "event_id", DataType = "uuid" },
                new TableColumn { ColumnName = "actor_ssn", DataType = "varchar" }
            }).WithPii("actor_ssn");

        var metadataRepo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { usersTable, auditTable });

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Consent
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    TableIdentifier = usersTable.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("dev-1"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(5)
                },
                new Consent
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    TableIdentifier = auditTable.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("dev-1"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(5)
                }
            });

        var service = new QueryAnalysisService(metadataRepo, consentRepo);
        var caller = CreatePrincipal("dev-1");

        var result = await service.AnalyzeQueriesAsync(
            new[]
            {
                QueryAnalysisItem.FromSql(
                    "SELECT u.id FROM iam.public.users u JOIN iam.public.user_events e ON u.ssn = e.actor_ssn WHERE u.ssn = '123-45-6789'",
                    name: "masked_join_and_where")
            },
            caller);

        result.ValidationReport.Diagnostics.Should().Contain(d =>
            d.Code == QueryDiagnosticCodes.MaskedColumnInFilter &&
            d.Dataset == "iam.public.users" &&
            d.Column == "ssn");

        result.ValidationReport.Diagnostics.Should().Contain(d =>
            d.Code == QueryDiagnosticCodes.MaskedColumnInFilter &&
            d.Dataset == "iam.public.user_events" &&
            d.Column == "actor_ssn");
    }

    // =========================================================================
    // Issue #79 — Criterion 4: Bundled consent request splits by Data Owner with
    // shared ConsumerReference
    // =========================================================================

    [Fact]
    public async Task SubmitBundledRequestAsync_MultiTableQueryAcrossTwoOwners_CreatesOneTicketPerOwnerWithSharedConsumerRef()
    {
        var ordersTable = CreateCatalogTable(
            "sales",
            "public",
            "orders",
            DatasetVisibility.Summary,
            new[] { new TableColumn { ColumnName = "id", DataType = "uuid" } });

        var shipmentsTable = CreateCatalogTable(
            "logistics",
            "public",
            "shipments",
            DatasetVisibility.Summary,
            new[] { new TableColumn { ColumnName = "id", DataType = "uuid" } });

        var paymentsTable = CreateCatalogTable(
            "sales",
            "public",
            "payments",
            DatasetVisibility.Full,
            new[] { new TableColumn { ColumnName = "card_last4", DataType = "varchar" } }).WithPii("card_last4");

        var metadataRepo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { ordersTable, shipmentsTable, paymentsTable });
        Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(metadataRepo);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());

        var ownerRepo = Substitute.For<IDataOwnershipRepository>();
        ownerRepo.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(false);
        ownerRepo.GetDataOwnersForTableAsync(ordersTable.Identifier, Arg.Any<CancellationToken>())
            .Returns(new[] { new DataOwner { AdSid = new Sid("owner-sales"), DisplayName = "Sales Owner", IsActive = true } });
        ownerRepo.GetDataOwnersForTableAsync(paymentsTable.Identifier, Arg.Any<CancellationToken>())
            .Returns(new[] { new DataOwner { AdSid = new Sid("owner-sales"), DisplayName = "Sales Owner", IsActive = true } });
        ownerRepo.GetDataOwnersForTableAsync(shipmentsTable.Identifier, Arg.Any<CancellationToken>())
            .Returns(new[] { new DataOwner { AdSid = new Sid("owner-logistics"), DisplayName = "Logistics Owner", IsActive = true } });

        var createdRequests = new List<ConsentRequest>();
        var approvalRepo = Substitute.For<IConsentApprovalRepository>();
        approvalRepo.CreateConsentRequestAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<ConsentRequest>();
                createdRequests.Add(req);
                return Task.FromResult(req);
            });

        var service = new BundledConsentRequestService(
            metadataRepo,
            consentRepo,
            approvalRepo,
            ownerRepo);

        var requester = CreatePrincipal("app-checkout-svc");
        var bundleResult = await service.SubmitBundledRequestAsync(
            requester,
            new BundledConsentRequest(
                Items: new[]
                {
                    new RequestableConsentItem("sales.public.orders", null, "READ", "Order fulfillment join"),
                    new RequestableConsentItem("sales.public.payments", "card_last4", "READ_CLEARTEXT", "Receipt display"),
                    new RequestableConsentItem("logistics.public.shipments", null, "READ", "Shipment status join")
                },
                Purpose: "Checkout v2 deployment",
                ConsumerReference: "PR-4821"));

        bundleResult.ConsumerReference.Should().Be("PR-4821");
        bundleResult.Tickets.Should().HaveCount(2);

        var salesTicket = bundleResult.Tickets.Single(t => t.DataOwnerKey == "owner:owner-sales");
        salesTicket.ConsumerReference.Should().Be("PR-4821");
        salesTicket.Datasets.Should().BeEquivalentTo(new[] { "sales.public.orders", "sales.public.payments" });
        salesTicket.Datasets.Should().NotContain("logistics.public.shipments");

        var logisticsTicket = bundleResult.Tickets.Single(t => t.DataOwnerKey == "owner:owner-logistics");
        logisticsTicket.ConsumerReference.Should().Be("PR-4821");
        logisticsTicket.Datasets.Should().ContainSingle().Which.Should().Be("logistics.public.shipments");

        createdRequests.Should().HaveCount(3);
        createdRequests.Should().OnlyContain(r => r.ItsmTicketId == "PR-4821");
    }

    // =========================================================================
    // Issue #79 — Criterion 5: Evaluating for another principal or group is
    // RBAC-restricted and returns only aggregate counts for groups
    // =========================================================================

    [Fact]
    public async Task AnalyzeQueriesAndGroupRequirements_RbacEnforcedAndReturnsOnlyAggregateCountsForGroup()
    {
        var ordersTable = CreateCatalogTable(
            "sales",
            "public",
            "orders",
            DatasetVisibility.Full,
            new[] { new TableColumn { ColumnName = "id", DataType = "uuid" } });

        var metadataRepo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { ordersTable });

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sids = callInfo.Arg<IEnumerable<Sid>>();
                if (sids.Any(s => s.Value == "member-granted"))
                {
                    return Task.FromResult<IReadOnlyList<Consent>>(new[]
                    {
                        new Consent
                        {
                            TenantId = Autheris.Tests.Common.TestTenants.Default,
                            TableIdentifier = ordersTable.Identifier,
                            Effect = ConsentEffect.Allow,
                            GranteeSid = new Sid("member-granted"),
                            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                            ValidTo = DateTimeOffset.UtcNow.AddDays(5)
                        }
                    });
                }

                return Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());
            });

        var service = new QueryAnalysisService(metadataRepo, consentRepo);
        var unprivilegedCaller = CreatePrincipal("regular-user");
        var targetUser = CreatePrincipal("other-user");
        var queries = new[] { QueryAnalysisItem.FromSql("SELECT id FROM sales.public.orders") };

        // Unprivileged caller cannot evaluate for another principal
        Func<Task> actOtherPrincipal = () => service.AnalyzeQueriesAsync(queries, unprivilegedCaller, targetUser);
        await actOtherPrincipal.Should().ThrowAsync<UnauthorizedAccessException>();

        // Unprivileged caller cannot evaluate group access requirements
        Func<Task> actGroupUnprivileged = () => service.EvaluateGroupAccessRequirementsAsync(
            queries,
            new[] { targetUser },
            unprivilegedCaller);
        await actGroupUnprivileged.Should().ThrowAsync<UnauthorizedAccessException>();

        // Privileged caller with governance:simulate can evaluate group and receives only aggregate counts
        var simulateCaller = CreatePrincipal("auditor-1", "governance:simulate");
        var groupMembers = new[]
        {
            CreatePrincipal("member-granted"),
            CreatePrincipal("member-missing-1"),
            CreatePrincipal("member-missing-2")
        };

        var summary = await service.EvaluateGroupAccessRequirementsAsync(queries, groupMembers, simulateCaller);
        summary.Should().ContainSingle(s =>
            s.Dataset == "sales.public.orders" &&
            s.MembersMissingGrantCount == 2 &&
            s.TotalMembersEvaluated == 3);
    }

    // =========================================================================
    // Issue #79 — Criterion 6: SqlEndpointDefinition and ProcedureDefinition
    // have ReferencedTables verified against SQL AST and auto-populated when omitted
    // =========================================================================

    [Fact]
    public async Task AnalyzeQueriesAsync_SqlEndpointAndProcedure_VerifiesAndAutoPopulatesReferencedTables()
    {
        var ordersTable = CreateCatalogTable(
            "sales",
            "public",
            "orders",
            DatasetVisibility.Full,
            new[] { new TableColumn { ColumnName = "id", DataType = "uuid" } });

        var customersTable = CreateCatalogTable(
            "sales",
            "public",
            "customers",
            DatasetVisibility.Full,
            new[] { new TableColumn { ColumnName = "id", DataType = "uuid" } });

        var metadataRepo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { ordersTable, customersTable });

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());

        var service = new QueryAnalysisService(metadataRepo, consentRepo);
        var caller = CreatePrincipal("admin-1", "GovernanceAdmin");

        // 1. Omitted ReferencedTables -> auto-populated from SQL AST
        var endpointAuto = new SqlEndpointDefinition(
            Name: "get_customer_orders",
            Summary: "Orders joined with customers",
            RawSql: "SELECT o.id FROM sales.public.orders o JOIN sales.public.customers c ON o.id = c.id",
            ReferencedTables: Array.Empty<string>());

        var procMismatch = new ProcedureDefinition(
            Name: "sp_orders_only_declared",
            Summary: "Mismatched procedure declaration",
            ProcedureName: "dbo.sp_orders",
            Mode: ProcedureMode.Read,
            DataSource: "sales",
            Parameters: Array.Empty<ProcedureParameter>(),
            ContextBindings: Array.Empty<ProcedureContextBinding>(),
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: null,
            ClearedResultColumns: Array.Empty<string>(),
            RequiredRoles: Array.Empty<string>(),
            TimeoutSeconds: 30)
        {
            ReferencedTables = new[] { "sales.public.orders" }
        };

        var result = await service.AnalyzeQueriesAsync(
            new[]
            {
                QueryAnalysisItem.FromSqlEndpoint(endpointAuto),
                QueryAnalysisItem.FromProcedure(
                    procMismatch,
                    procedureSqlBody: "SELECT o.id FROM sales.public.orders o JOIN sales.public.customers c ON o.id = c.id")
            },
            caller);

        result.ValidationReport.EffectivePopulatedReferencedTables.Should().ContainKey("get_customer_orders");
        result.ValidationReport.EffectivePopulatedReferencedTables["get_customer_orders"]
            .Should().BeEquivalentTo(new[] { "sales.public.customers", "sales.public.orders" });

        result.ValidationReport.IsValid.Should().BeFalse();
        result.ValidationReport.Diagnostics.Should().ContainSingle(d =>
            d.Code == QueryDiagnosticCodes.ReferencedTablesMismatch &&
            d.QueryName == "sp_orders_only_declared");
    }

}
