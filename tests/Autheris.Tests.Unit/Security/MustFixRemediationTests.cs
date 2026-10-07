using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

public sealed class MustFixRemediationTests
{
    // =========================================================================
    // 1. R-SQL-1: SqlDataSourceExecutor synthetic data fallback in Dev vs Prod
    // =========================================================================

    [Fact]
    public async Task R_SQL_1_SqlDataSourceExecutor_ThrowsNotSupported_InProduction_WhenNoConnectionConfigured()
    {
        var prodEnv = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var executor = new SqlDataSourceExecutor(
            environment: prodEnv,
            logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "prod_db",
            SchemaName = "public",
            TableName = "sensitive_data",
            DataSourceType = DataSourceType.Sql,
            SourceType = "PostgreSQL"
        };
        var metadata = new TableMetadata
        {
            Table = table,
            Identifier = table.ToIdentifier("prod_db"),
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var context = new DataSourceExecutionContext(
            SourceName: "prod_db",
            Metadata: metadata,
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user1")])),
            AccessDecision: TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }),
            Arguments: new Dictionary<string, object?> { ["id"] = 1 },
            RequestedFields: ["id"]);

        var ex = await Should.ThrowAsync<NotSupportedException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Synthetischer Daten-Fallback ist in Produktivumgebungen deaktiviert");
    }

    [Fact]
    public async Task R_SQL_1_SqlDataSourceExecutor_GeneratesSyntheticData_InDevelopmentOrUnitTests()
    {
        // When _environment is null (typical for unit tests), synthetic fallback succeeds
        var executor = new SqlDataSourceExecutor(
            environment: null,
            logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "test_db",
            SchemaName = "public",
            TableName = "items",
            DataSourceType = DataSourceType.Sql,
            SourceType = "PostgreSQL"
        };
        var metadata = new TableMetadata
        {
            Table = table,
            Identifier = table.ToIdentifier("test_db"),
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };

        var context = new DataSourceExecutionContext(
            SourceName: "test_db",
            Metadata: metadata,
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user1")])),
            AccessDecision: TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["name"] = ColumnAccessLevel.Clear
            }),
            Arguments: new Dictionary<string, object?> { ["id"] = 1 },
            RequestedFields: ["id", "name"]);

        var rows = await executor.ExecuteAsync(context);
        rows.ShouldNotBeNull();
        rows.Count.ShouldBeGreaterThan(0);
    }

    // =========================================================================
    // 2. R-POL-2: Global Casbin Deny rules take absolute precedence
    // =========================================================================

    [Fact]
    public async Task R_POL_2_GlobalCasbinDenyRule_BlocksTenant_EvenWhenTenantHasSpecificAllowRules()
    {
        var service = new CasbinEnforcementService();

        // Policy with:
        // 1. Global wildcard Deny for 'sensitive_orders'
        // 2. Specific tenant-a Allow rule for all tables
        var targetTable = new TableIdentifier("default", "public", "sensitive_orders");
        var policy = $@"
p, alice, *, {targetTable}, read, true, deny
p, alice, tenant-a, *, read, true, allow
";
        service.LoadPolicyFromText(policy);

        var evalContext = new SecurityEvaluationContext(
            UserSid: new Sid("alice"),
            GroupSids: Array.Empty<Sid>(),
            Tenant: new TenantId("tenant-a"),
            TargetTable: targetTable,
            RequestedColumns: Array.Empty<string>(),
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null);

        var decision = await service.EvaluatePolicyAsync(evalContext);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("denied by ABAC policy"));
    }

    // =========================================================================
    // 3. R-POL-3: Reloading policy file drops removed tenants immediately
    // =========================================================================

    [Fact]
    public async Task R_POL_3_PolicyReload_ImmediatelyRevokesTenants_RemovedFromNewPolicy()
    {
        var service = new CasbinEnforcementService();
        var table = new TableIdentifier("default", "public", "docs");

        // Initial policy granting access to tenant-a and tenant-b
        var initialPolicy = $@"
p, bob, tenant-a, {table}, read, true, allow
p, bob, tenant-b, {table}, read, true, allow
";
        service.LoadPolicyFromText(initialPolicy);

        var ctxTenantB = new SecurityEvaluationContext(
            UserSid: new Sid("bob"),
            GroupSids: Array.Empty<Sid>(),
            Tenant: new TenantId("tenant-b"),
            TargetTable: table,
            RequestedColumns: Array.Empty<string>(),
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null);

        // Pre-reload: bob has access to tenant-b
        var decisionBefore = await service.EvaluatePolicyAsync(ctxTenantB);
        decisionBefore.IsAllowed.ShouldBeTrue();

        // New policy: tenant-b is removed/revoked
        var newPolicy = $@"
p, bob, tenant-a, {table}, read, true, allow
";
        service.LoadPolicyFromText(newPolicy);

        // Post-reload: tenant-b is immediately revoked
        var decisionAfter = await service.EvaluatePolicyAsync(ctxTenantB);
        decisionAfter.IsAllowed.ShouldBeFalse();
    }

    // =========================================================================
    // 4. R-POL-1: Global Casbin Allow rules apply across tenants
    // =========================================================================

    [Fact]
    public async Task R_POL_1_GlobalCasbinAllowRule_GrantsAccess_ToAnyTenantWithoutOverrides()
    {
        var service = new CasbinEnforcementService();
        var table = new TableIdentifier("default", "public", "reports");

        // Policy with global wildcard Allow for reports
        var policy = $@"
p, auditor, *, {table}, read, true, allow
";
        service.LoadPolicyFromText(policy);

        // Auditor evaluates in tenant-acme
        var evalContext = new SecurityEvaluationContext(
            UserSid: new Sid("auditor"),
            GroupSids: Array.Empty<Sid>(),
            Tenant: new TenantId("tenant-acme"),
            TargetTable: table,
            RequestedColumns: Array.Empty<string>(),
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null);

        var decision = await service.EvaluatePolicyAsync(evalContext);

        decision.IsAllowed.ShouldBeTrue();
    }

    // =========================================================================
    // 5. R-GQL-1: Masked numbers & booleans in GraphQL return null
    // =========================================================================

    [Fact]
    public void R_GQL_1_TreeSqlCompiler_EmitsNull_ForMaskedNonStringColumns()
    {
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "public",
            TableName = "deals",
            DataSourceType = DataSourceType.Sql,
            SourceType = "PostgreSQL"
        };
        var metadata = new TableMetadata
        {
            Table = table,
            Identifier = table.ToIdentifier("sales"),
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "is_confidential", DataType = "boolean" },
                new TableColumn { ColumnName = "notes", DataType = "text" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["amount"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" },
                ["is_confidential"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" },
                ["notes"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" }
            }
        };

        var decision = TableAccessDecision.Allowed(
            table.ToIdentifier("sales"),
            new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Mask,
                ["is_confidential"] = ColumnAccessLevel.Mask,
                ["notes"] = ColumnAccessLevel.Mask
            });

        var rootNode = new TreeQueryNode(table.ToIdentifier("sales"), ["id", "amount", "is_confidential", "notes"]);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [table.ToIdentifier("sales")] = new(metadata, decision)
        };

        var compiled = TreeSqlCompiler.Compile(rootNode, access, DatabaseDialect.PostgreSql);

        // SQL should output NULL for masked decimal and boolean, but '***' literal for text notes
        compiled.Sql.ShouldContain("'amount', NULL");
        compiled.Sql.ShouldContain("'is_confidential', NULL");
        compiled.Sql.ShouldContain("'notes', '***'");
    }

    // =========================================================================
    // 6. R-GQL-2: GraphQL Schema handles name collisions (orders_filter, and/or)
    // =========================================================================

    [Fact]
    public async Task R_GQL_2_CatalogSchemaModel_ResolvesTypeCollisions_AndReservedFilterKeywords()
    {
        var metaRepo = new InMemoryTableMetadataRepository();
        var relRepo = new InMemoryTableRelationRepository();

        // 1. Table 'orders' and Table 'orders_filter' in the same domain
        var table1 = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "db",
            SchemaName = "public",
            TableName = "orders",
            DataSourceType = DataSourceType.Sql,
            SourceType = "PostgreSQL"
        };
        var meta1 = new TableMetadata
        {
            Table = table1,
            Identifier = table1.ToIdentifier("db"),
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "and", DataType = "varchar" },
                new TableColumn { ColumnName = "or", DataType = "boolean" }
            ]
        };

        var table2 = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "db",
            SchemaName = "public",
            TableName = "orders_filter",
            DataSourceType = DataSourceType.Sql,
            SourceType = "PostgreSQL"
        };
        var meta2 = new TableMetadata
        {
            Table = table2,
            Identifier = table2.ToIdentifier("db"),
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" }
            ]
        };

        await metaRepo.UpsertTableMetadataAsync(meta1);
        await metaRepo.UpsertTableMetadataAsync(meta2);

        var schemaModel = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        // Verify no duplicate type names exist in the schema model
        var allTypeNames = schemaModel.Tables.SelectMany(t => new[] { t.TypeName, t.FilterTypeName, t.OrderByTypeName }).ToList();
        allTypeNames.Distinct().Count().ShouldBe(allTypeNames.Count);

        // Verify column names 'and' and 'or' are sanitized to avoid colliding with logical operators
        var ordersTable = schemaModel.Tables.First(t => t.Identifier.TableName == "orders");
        ordersTable.Columns.ShouldContain(c => c.ColumnName == "and" && c.FieldName == "and_col");
        ordersTable.Columns.ShouldContain(c => c.ColumnName == "or" && c.FieldName == "or_col");

        // Verify type names in schema model avoid collisions
        schemaModel.Tables.Any(t => t.TypeName == "db_public_orders").ShouldBeTrue();
        schemaModel.Tables.Any(t => t.TypeName == "db_public_orders_filter_2").ShouldBeTrue();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class InMemoryTableMetadataRepository : ITableMetadataRepository
    {
        private readonly List<TableMetadata> _tables = new();

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables.RemoveAll(t => t.Identifier.Equals(metadata.Identifier));
            _tables.Add(metadata);
            return Task.FromResult(metadata);
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(_tables.FirstOrDefault(t => t.Identifier.Equals(table)));

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>(_tables);
    }

    private sealed class InMemoryTableRelationRepository : ITableRelationRepository
    {
        private readonly List<TableRelation> _relations = new();

        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>(_relations.Where(r => r.ParentTableIdentifier.Equals(parentTable)).ToList());

        public Task CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
        {
            _relations.Add(relation);
            return Task.CompletedTask;
        }
    }
}
