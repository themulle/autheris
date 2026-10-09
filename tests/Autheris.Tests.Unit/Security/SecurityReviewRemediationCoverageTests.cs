using System.Data;
using System.Data.Common;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.FinOps.Interfaces;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Security;

public sealed class SecurityReviewRemediationCoverageTests
{
    // =========================================================================
    // SQL-1 (High): UPDATE/DELETE with custom tenant column
    // =========================================================================

    [Fact]
    public void SQL_1_AstSecurityVisitor_BlocksUpdate_OnCustomTenantColumn()
    {
        var rlsOptions = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id",
            RejectUnfilteredDml = true
        };
        // Table 'accounts' uses custom tenant column 'TenantId'
        rlsOptions.TableTenantColumns["accounts"] = "TenantId";

        var engine = new FastSqlEngine();
        var sql = "UPDATE accounts SET balance = 100, TenantId = 'hacked' WHERE id = 1";

        // AstSecurityVisitor should throw SecurityException because modifying tenant column is forbidden
        Should.Throw<SecurityException>(() => engine.GenerateGovernedSql(sql.AsMemory(), rlsOptions));
    }

    [Fact]
    public void SQL_1_RlsListener_BlocksUpdate_OnCustomTenantColumn()
    {
        var rlsOptions = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id"
        };
        rlsOptions.TableTenantColumns["organizations"] = "org_id";

        var engine = new FastSqlEngine();
        var sql = "UPDATE organizations SET name = 'New Corp', org_id = 'rogue_tenant' WHERE id = 42";

        Should.Throw<SecurityException>(() => engine.RewriteRls(sql.AsMemory(), rlsOptions));
    }

    [Fact]
    public void SQL_1_AstSecurityVisitor_BlocksInsert_WithCheckOption_OnCustomTenantColumn()
    {
        var rlsOptions = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id",
            ExpectedTenantValue = "tenant-safe",
            RequireTenantColumnInInsert = true
        };
        rlsOptions.TableTenantColumns["accounts"] = "TenantId";

        var engine = new FastSqlEngine();
        var sqlWrongTenant = "INSERT INTO accounts (id, balance, TenantId) VALUES (1, 100, 'tenant-hacked')";
        Should.Throw<SecurityException>(() => engine.GenerateGovernedSql(sqlWrongTenant.AsMemory(), rlsOptions));

        var sqlMissingTenant = "INSERT INTO accounts (id, balance) VALUES (1, 100)";
        Should.Throw<SecurityException>(() => engine.GenerateGovernedSql(sqlMissingTenant.AsMemory(), rlsOptions));
    }

    [Fact]
    public void SQL_1_RlsListener_BlocksInsert_WithCheckOption_OnCustomTenantColumn()
    {
        var rlsOptions = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id",
            ExpectedTenantValue = "tenant-safe",
            RequireTenantColumnInInsert = true
        };
        rlsOptions.TableTenantColumns["organizations"] = "org_id";

        var engine = new FastSqlEngine();
        var sqlWrongTenant = "INSERT INTO organizations (id, name, org_id) VALUES (42, 'Rogue', 'rogue_tenant')";
        Should.Throw<SecurityException>(() => engine.RewriteRls(sqlWrongTenant.AsMemory(), rlsOptions));

        var sqlMissingTenant = "INSERT INTO organizations (id, name) VALUES (42, 'Rogue')";
        Should.Throw<SecurityException>(() => engine.RewriteRls(sqlMissingTenant.AsMemory(), rlsOptions));
    }

    [Fact]
    public void SQL_1_MultiTableDml_BlocksModification_OnCustomTenantColumn()
    {
        var rlsOptions = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id",
            ExpectedTenantValue = "tenant-safe"
        };
        rlsOptions.TableTenantColumns["accounts"] = "account_tenant";
        rlsOptions.TableTenantColumns["orders"] = "order_tenant";

        var engine = new FastSqlEngine();
        var sql1 = "UPDATE accounts SET balance = 50, account_tenant = 'hacked' WHERE id = 10";
        Should.Throw<SecurityException>(() => engine.GenerateGovernedSql(sql1.AsMemory(), rlsOptions));
        Should.Throw<SecurityException>(() => engine.RewriteRls(sql1.AsMemory(), rlsOptions));

        var sql2 = "UPDATE orders SET total = 200, order_tenant = 'hacked' WHERE id = 20";
        Should.Throw<SecurityException>(() => engine.GenerateGovernedSql(sql2.AsMemory(), rlsOptions));
        Should.Throw<SecurityException>(() => engine.RewriteRls(sql2.AsMemory(), rlsOptions));
    }

    [Fact]
    public void SQL_1_ValidDml_WithoutTenantColumnModification_Succeeds()
    {
        var rlsOptions = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id",
            ExpectedTenantValue = "tenant-safe",
            RequireTenantColumnInInsert = true
        };
        rlsOptions.TableTenantColumns["accounts"] = "TenantId";

        var engine = new FastSqlEngine();
        var validUpdate = "UPDATE accounts SET balance = 500 WHERE id = 1";
        var resUpd = engine.GenerateGovernedSql(validUpdate.AsMemory(), rlsOptions);
        resUpd.ShouldNotBeNullOrWhiteSpace();

        var validInsert = "INSERT INTO accounts (id, balance, TenantId) VALUES (1, 500, 'tenant-safe')";
        var resIns = engine.GenerateGovernedSql(validInsert.AsMemory(), rlsOptions);
        resIns.ShouldNotBeNullOrWhiteSpace();
    }

    // =========================================================================
    // POL-2 (Medium): Four-eyes approval for HIGH, RESTRICTED, SECRET sensitivity
    // =========================================================================

    [Theory]
    [InlineData("HIGH", true)]
    [InlineData("RESTRICTED", true)]
    [InlineData("SECRET", true)]
    [InlineData("high", true)]
    [InlineData("NORMAL", false)]
    [InlineData("PUBLIC", false)]
    public void POL_2_ConsentApprovalPolicy_EnforcesFourEyes_ForHighSensitivityClassifications(string sensitivity, bool expectedFourEyes)
    {
        // When requiresFourEyes is false, sensitivity dictates whether four eyes is mandated (ADR-008)
        var requiresFourEyes = ConsentApprovalPolicy.RequiresFourEyesApproval(requiresFourEyes: false, sensitivity);
        requiresFourEyes.ShouldBe(expectedFourEyes);

        var statusAfterFirstStep = ConsentApprovalPolicy.StatusAfterApproval(
            requiresFourEyes: false,
            stepNumber: 1,
            isExternalItsmApproval: false,
            sensitivity: sensitivity);

        if (expectedFourEyes)
        {
            statusAfterFirstStep.ShouldBe(ConsentApprovalPolicy.PendingSecond);
        }
        else
        {
            statusAfterFirstStep.ShouldBe(ConsentApprovalPolicy.Approved);
        }
    }

    // =========================================================================
    // POL-4 (Medium): Tenant-scoped HMAC pseudonymization differences between tenants
    // =========================================================================

    [Fact]
    public void POL_4_TenantScopedHmacRule_ProducesDifferentKeyIds_AndRejectsCrossTenantReuse()
    {
        var baseRule = new MaskingRule
        {
            Id = Guid.NewGuid(),
            RuleType = "HMAC_SHA256",
            HmacKeyId = "master_key"
        };

        var tenantARule = MaskingRule.CreateTenantScopedHmacRule(baseRule, "tenant-alpha");
        var tenantBRule = MaskingRule.CreateTenantScopedHmacRule(baseRule, "tenant-beta");

        tenantARule.HmacKeyId.ShouldBe("master_key|tenant:tenant-alpha");
        tenantBRule.HmacKeyId.ShouldBe("master_key|tenant:tenant-beta");

        // Idempotent for same tenant
        var idempotentRule = MaskingRule.CreateTenantScopedHmacRule(tenantARule, "tenant-alpha");
        idempotentRule.HmacKeyId.ShouldBe("master_key|tenant:tenant-alpha");

        // Reject cross-tenant correlation attempt
        Should.Throw<InvalidOperationException>(() =>
            MaskingRule.CreateTenantScopedHmacRule(tenantARule, "tenant-beta"));
    }

    // =========================================================================
    // POL-7 (Medium): ReBAC rejection of empty/whitespace tuple fields
    // =========================================================================

    [Theory]
    [InlineData("", "viewer", "doc:10", "tenant-1")]
    [InlineData("alice", "   ", "doc:10", "tenant-1")]
    [InlineData("alice", "viewer", "", "tenant-1")]
    [InlineData("alice", "viewer", "doc:10", "")]
    [InlineData("alice", "viewer", ":doc-without-namespace", "tenant-1")]
    public async Task POL_7_InMemoryRebacStore_RejectsEmptyOrWhitespaceFields(string user, string rel, string obj, string tenant)
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var tuple = new RebacTuple(tenant, user, rel, obj);

        await Should.ThrowAsync<ArgumentException>(() => store.AddTupleAsync(tuple).AsTask());
    }

    [Fact]
    public async Task POL_7_ZanzibarRebacEvaluator_DeniesCheck_OnEmptyParameters()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        var result = await evaluator.CheckAsync(new RebacCheckRequest("tenant-1", "", "viewer", "doc:10"));
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task POL_7_Userset_GroupMember_Syntax_FunctionsCorrectly()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        // Tuple 1: group:engineering#member has 'viewer' on doc:specs
        await store.AddTupleAsync(new RebacTuple("tenant-alpha", "group:engineering#member", "viewer", "doc:specs"));
        // Tuple 2: alice is member of group:engineering
        await store.AddTupleAsync(new RebacTuple("tenant-alpha", "alice", "member", "group:engineering"));

        // Alice should be permitted via userset expansion
        var aliceResult = await evaluator.CheckAsync(new RebacCheckRequest("tenant-alpha", "alice", "viewer", "doc:specs"));
        aliceResult.Allowed.ShouldBeTrue();

        // Bob is not a member of group:engineering and should be denied
        var bobResult = await evaluator.CheckAsync(new RebacCheckRequest("tenant-alpha", "bob", "viewer", "doc:specs"));
        bobResult.Allowed.ShouldBeFalse();
    }

    // =========================================================================
    // POL-3 (High): Column snapshot on consent activation & later column is Deny
    // =========================================================================

    [Fact]
    public async Task POL_3_ConsentResolutionService_AddedColumnsAfterConsentActivation_AreDenyNotClear()
    {
        using var repo = new SqliteGovernanceRepository(new StubEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "pol3_customers_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            IsActive = true
        };

        // Table initially has columns: id, name
        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns =
            [
                new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INTEGER" },
                new TableColumn { TableId = table.Id, ColumnName = "name", DataType = "VARCHAR" }
            ]
        });

        var userSid = new Sid("S-1-5-21-POL3-USER");
        var request = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = userSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = userSid.Value,
            BusinessJustification = "POL-3 Unit Test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Status = "PENDING",
            TenantId = new TenantId("tenant-pol3")
        });

        // Activate the consent: freezes column snapshot into CONSENT_COLUMN_RULES
        await repo.ActivateConsentForAutoApproveAsync(request.Id);

        // Retrieve active consents
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync([userSid], tableId, DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBe(1);
        activeConsents[0].ColumnRules.Count.ShouldBe(2); // "id" and "name"
        activeConsents[0].ColumnRules.ShouldContain(r => r.ColumnName.Equals("id", StringComparison.OrdinalIgnoreCase));
        activeConsents[0].ColumnRules.ShouldContain(r => r.ColumnName.Equals("name", StringComparison.OrdinalIgnoreCase));

        // Resolve access via ConsentResolutionService
        var resolutionService = new ConsentResolutionService();
        var decision = resolutionService.ResolveAccess(
            userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            tableId,
            activeConsents);

        decision.IsAllowed.ShouldBeTrue();
        decision.HasUnconstrainedColumnAllow.ShouldBeFalse();
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Clear);

        // A column added to the schema LATER was NOT part of the consent activation snapshot and must NOT resolve to Clear
        decision.GetColumnAccess("secret_credit_card").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetEffectiveColumnAccess("secret_credit_card", metadata: null).ShouldBe(ColumnAccessLevel.Deny);
    }

    // =========================================================================
    // API-4 / R-API-2: FinOps Anonymous & Invalid Tenant Handling
    // =========================================================================

    [Fact]
    public async Task API_4_FinOpsBudgetMiddleware_AnonymousRequest_IsNotAccounted()
    {
        var accountingService = Substitute.For<IFinOpsAccountingService>();
        accountingService.IsEnabled.Returns(true);

        bool nextCalled = false;
        var middleware = new FinOpsBudgetMiddleware(
            next: ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            logger: NullLogger<FinOpsBudgetMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity()); // Unauthenticated / Anonymous

        await middleware.InvokeAsync(context, accountingService);

        nextCalled.ShouldBeTrue();
        await accountingService.DidNotReceiveWithAnyArgs().CheckBudgetAsync(default!, default);
    }

    [Fact]
    public async Task API_4_FinOpsBudgetMiddleware_InvalidTenantClaim_DoesNotThrow_AndSkipsAccounting()
    {
        var accountingService = Substitute.For<IFinOpsAccountingService>();
        accountingService.IsEnabled.Returns(true);

        bool nextCalled = false;
        var middleware = new FinOpsBudgetMiddleware(
            next: ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            logger: NullLogger<FinOpsBudgetMiddleware>.Instance);

        var context = new DefaultHttpContext();
        // Claim with invalid tenant format (e.g. contains illegal characters / punctuation)
        var identity = new ClaimsIdentity([new Claim("tenant_id", "invalid!tenant@name#bad")], "Bearer");
        context.User = new ClaimsPrincipal(identity);

        await middleware.InvokeAsync(context, accountingService);

        nextCalled.ShouldBeTrue();
        await accountingService.DidNotReceiveWithAnyArgs().CheckBudgetAsync(default!, default);
    }

    // =========================================================================
    // R-SQL-5: WebSql Throttling returns 429 with Retry-After header
    // =========================================================================

    [Fact]
    public async Task R_SQL_5_WebSql_GatewayThrottledException_Sets429_AndRetryAfterHeader()
    {
        var httpContext = new DefaultHttpContext();
        var throttledEx = new GatewayThrottledException(retryAfterSeconds: 5);

        await WebSqlEndpoints.WriteWebSqlErrorAsync(
            httpContext,
            throttledEx,
            NullLogger.Instance,
            CancellationToken.None);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        httpContext.Response.Headers.RetryAfter.ToString().ShouldBe("5");
    }

    // =========================================================================
    // R-SQL-7: Rollback error preserves original exception and disposes transaction
    // =========================================================================

    [Fact]
    public async Task R_SQL_7_DbSessionContextInitializer_RollbackError_PreservesOriginalException()
    {
        var testTx = new FailingRollbackDbTransaction();
        var testConn = new FailingInitDbConnection(testTx);

        var initializer = new DbSessionContextInitializer();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            initializer.InitializeSessionAsync(
                testConn,
                DatabaseDialect.PostgreSql,
                new TenantId("tenant-test"),
                requireTransaction: true));

        // Original exception must be preserved, not masked by the rollback failure
        ex.Message.ShouldBe("PostgreSql init command failed");
        testTx.RollbackAttempted.ShouldBeTrue();
        testTx.IsDisposed.ShouldBeTrue();
    }

    // =========================================================================
    // R-POL-4 & R-POL-6: Casbin wildcard tenant event & IPolicyEnforcementService defaults
    // =========================================================================

    [Fact]
    public void R_POL_4_CasbinEnforcementService_WildcardTenantNotification_DoesNotThrow()
    {
        using var service = new CasbinEnforcementService();

        string? failedTenant = null;
        Exception? receivedException = null;
        service.OnPolicyReloadFailed += (tenant, ex) =>
        {
            failedTenant = tenant;
            receivedException = ex;
        };

        string? reloadedTenant = null;
        service.OnPolicyReloaded += (tenant) =>
        {
            reloadedTenant = tenant;
        };

        // When a global/all-tenants reload notification occurs with "*", it must not throw ArgumentException
        // (which would happen if TenantId("*") was constructed instead of using string).
        Should.NotThrow(() =>
        {
            var fieldFailed = typeof(CasbinEnforcementService).GetField("OnPolicyReloadFailed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var delFailed = (Action<string, Exception>?)fieldFailed?.GetValue(service);
            delFailed?.Invoke("*", new FileNotFoundException("Test reload error"));

            var fieldReloaded = typeof(CasbinEnforcementService).GetField("OnPolicyReloaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var delReloaded = (Action<string>?)fieldReloaded?.GetValue(service);
            delReloaded?.Invoke("*");
        });

        failedTenant.ShouldBe("*");
        receivedException.ShouldNotBeNull();
        reloadedTenant.ShouldBe("*");
    }

    [Fact]
    public void R_POL_6_IPolicyEnforcementService_DefaultLoadMethods_ThrowNotSupportedException()
    {
        IPolicyEnforcementService stub = new StubPolicyEnforcementService();

        Should.Throw<NotSupportedException>(() => stub.LoadPolicyFromText(new TenantId("tenant-1"), "p, admin, data, read"));
        Should.Throw<NotSupportedException>(() => stub.LoadPolicyFromText("p, admin, data, read"));
        Should.Throw<NotSupportedException>(() => stub.LoadPolicyFromFile(new TenantId("tenant-1"), "dummy.csv"));
        Should.Throw<NotSupportedException>(() => stub.LoadPolicyFromFile("dummy.csv"));
    }

    // =========================================================================
    // EXT-1 (Medium): dbt proposals not saved as column masks & ratchet preserved
    // =========================================================================

    [Fact]
    public void EXT_1_CatalogGovernanceRatchet_DoesNotDowngradeStrictMasks_OnCatalogMerge()
    {
        var table = new Table { Id = Guid.NewGuid(), SourceName = "raw_db", SchemaName = "public", TableName = "users" };
        var existingMeta = new TableMetadata
        {
            Table = table,
            Identifier = table.ToIdentifier("raw_db"),
            Columns = [new TableColumn { ColumnName = "ssn", DataType = "varchar" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["ssn"] = new MaskingRule { RuleType = "NULLIFY" } // Strongest mask
            }
        };

        var incomingMeta = new TableMetadata
        {
            Table = table,
            Identifier = table.ToIdentifier("raw_db"),
            Columns = [new TableColumn { ColumnName = "ssn", DataType = "varchar" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["ssn"] = new MaskingRule { RuleType = "REDACT" } // Weaker mask proposal
            }
        };

        var merged = CatalogGovernanceRatchet.Merge(incomingMeta, existingMeta);

        // Ratchet must preserve the stricter NULLIFY rule
        merged.ColumnMaskingRules["ssn"].RuleType.ShouldBe("NULLIFY");
    }

    // =========================================================================
    // EXT-2 (Medium): Physical SourceName preserved during catalog sync
    // =========================================================================

    [Fact]
    public void EXT_2_CatalogGovernanceRatchet_PreservesPhysicalSourceName()
    {
        var existingTable = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "physical_mssql_cluster_01",
            SchemaName = "sales",
            TableName = "invoices"
        };
        var existingMeta = new TableMetadata
        {
            Table = existingTable,
            Identifier = existingTable.ToIdentifier("sales")
        };

        var incomingCatalogTable = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "catalog_logical_domain",
            SchemaName = "sales",
            TableName = "invoices"
        };
        var incomingMeta = new TableMetadata
        {
            Table = incomingCatalogTable,
            Identifier = incomingCatalogTable.ToIdentifier("sales")
        };

        var merged = CatalogGovernanceRatchet.Merge(incomingMeta, existingMeta);

        // Existing physical SourceName must remain untouched to prevent rerouting database connections
        merged.Table.SourceName.ShouldBe("physical_mssql_cluster_01");
    }

    [Theory]
    [InlineData("hr_admin_connection", "")]
    [InlineData("sales", "sales")]
    [InlineData("SALES", "SALES")]
    public void EXT_2_CatalogGovernanceRatchet_UnboundTable_AcceptsOnlyItsOwnDomainAsSourceName(string incomingSourceName, string expected)
    {
        var existingMeta = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), SourceName = string.Empty, SchemaName = "dbo", TableName = "invoices" },
            Identifier = new TableIdentifier("sales", "dbo", "invoices")
        };
        var incomingMeta = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), SourceName = incomingSourceName, SchemaName = "dbo", TableName = "invoices" },
            Identifier = new TableIdentifier("sales", "dbo", "invoices")
        };

        CatalogGovernanceRatchet.Merge(incomingMeta, existingMeta).Table.SourceName.ShouldBe(expected);
    }

    // =========================================================================
    // DEP-6 / POL-14 (Medium): Cryptographic secret length >= 32 bytes enforced
    // =========================================================================

    [Fact]
    public void DEP_6_DefaultEnvironmentSecretProvider_RejectsShortKeys_InProduction()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HMAC_MASTER_KEY"] = "short_key_16_bytes!"
            })
            .Build();

        var prodEnv = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var provider = new DefaultEnvironmentSecretProvider(config, prodEnv, NullLogger<DefaultEnvironmentSecretProvider>.Instance);

        var ex = Should.Throw<InvalidOperationException>(() => provider.GetSecretBytes("hmac-master-key"));
        ex.Message.ShouldContain("32 Bytes", Case.Insensitive);
    }

    // =========================================================================
    // DEP-12 (Low): Historical benchmark secrets must not be present in tracked HEAD files
    // =========================================================================

    [Fact]
    public void DEP_12_TrackedBenchmarkConfigurations_DoNotContainHardcodedHistoricalSecrets()
    {
        string[] historicalSecrets =
        [
            "REDACTED_HISTORICAL_BENCHMARK_SECRET",
            "REDACTED_HISTORICAL_BENCHMARK_SECRET",
            "REDACTED_HISTORICAL_BENCHMARK_SECRET"
        ];

        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (currentDir != null && !File.Exists(Path.Combine(currentDir.FullName, "Autheris.sln")))
        {
            currentDir = currentDir.Parent;
        }

        currentDir.ShouldNotBeNull("Repository root containing Autheris.sln should be discoverable");

        string[] targetFolders = ["deploy", "benchmarks", "src"];
        var checkedFileCount = 0;

        foreach (var folder in targetFolders)
        {
            var dirPath = Path.Combine(currentDir.FullName, folder);
            if (!Directory.Exists(dirPath)) continue;

            var files = Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                            !f.Contains(".bench-secrets.env") &&
                            !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                checkedFileCount++;
                var content = File.ReadAllText(file);
                foreach (var secret in historicalSecrets)
                {
                    content.Contains(secret).ShouldBeFalse($"File '{file}' contains historical benchmark secret '{secret}' (DEP-12)");
                }
            }
        }

        checkedFileCount.ShouldBeGreaterThan(10, "Should have inspected configuration and benchmark files");
    }

    // =========================================================================
    // IDbSessionContextInitializer: Centralized session & rollback safety
    // =========================================================================

    [Fact]
    public async Task DbSessionContextInitializer_SqliteDialect_ReturnsNullTransaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var initializer = new DbSessionContextInitializer();

        // SQLite dialect returns null because SQLite does not use ambient session variables
        var tx = await initializer.InitializeSessionAsync(
            connection,
            DatabaseDialect.Sqlite,
            new TenantId("tenant-test"),
            requireTransaction: true);

        tx.ShouldBeNull();
    }

    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private sealed class StubPolicyEnforcementService : IPolicyEnforcementService
    {
        public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(TableAccessDecision.Allowed(context.TargetTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        public bool HasPolicies(TenantId tenant) => true;

        public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FailingRollbackDbTransaction : DbTransaction
    {
        public bool RollbackAttempted { get; private set; }
        public bool IsDisposed { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;

        public override void Commit() { }
        public override void Rollback() => throw new InvalidOperationException("Rollback failed synchronously");

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackAttempted = true;
            throw new InvalidOperationException("Rollback failed asynchronously");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class FailingInitDbConnection : DbConnection
    {
        private readonly FailingRollbackDbTransaction _tx;

        public FailingInitDbConnection(FailingRollbackDbTransaction tx) => _tx = tx;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "15.0";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => _tx;

        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
            => ValueTask.FromResult<DbTransaction>(_tx);

        protected override DbCommand CreateDbCommand() => new FailingDbCommand();
    }

    private sealed class FailingDbCommand : DbCommand
    {
        private readonly FailingDbParameterCollection _parameters = new();

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new InvalidOperationException("PostgreSql init command failed");
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PostgreSql init command failed");
        public override object? ExecuteScalar() => throw new NotImplementedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FailingDbParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
    }

    private sealed class FailingDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FailingDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = new();
        public override int Count => _parameters.Count;
        public override object SyncRoot => ((System.Collections.ICollection)_parameters).SyncRoot;
        public override int Add(object value) { _parameters.Add((DbParameter)value); return _parameters.Count - 1; }
        public override void AddRange(Array values) { foreach (var val in values) Add(val!); }
        public override void Clear() => _parameters.Clear();
        public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
        public override bool Contains(string value) => _parameters.Exists(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_parameters).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
        public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _parameters.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _parameters.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _parameters.RemoveAt(index);
        public override void RemoveAt(string parameterName) { int idx = IndexOf(parameterName); if (idx >= 0) RemoveAt(idx); }
        protected override DbParameter GetParameter(int index) => _parameters[index];
        protected override DbParameter GetParameter(string parameterName) => _parameters[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _parameters[IndexOf(parameterName)] = value;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
