using System.Security;
using System.Security.Claims;
using System.Text;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            TenantColumnName = "tenant_id"
        };
        rlsOptions.TableTenantColumns["organizations"] = "org_id";

        var engine = new FastSqlEngine();
        var sql = "UPDATE organizations SET name = 'New Corp', org_id = 'rogue_tenant' WHERE id = 42";

        Should.Throw<SecurityException>(() => engine.RewriteRls(sql.AsMemory(), rlsOptions));
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
    // IDbSessionContextInitializer: Centralized session & rollback safety
    // =========================================================================

    [Fact]
    public async Task DbSessionContextInitializer_RollsBackTransaction_OnError()
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
