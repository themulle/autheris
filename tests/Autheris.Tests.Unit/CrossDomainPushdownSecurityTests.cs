using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.Adapters;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Connectors.Pushdown;
using Autheris.Application.Connectors.Streaming;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Connectors;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class CrossDomainPushdownSecurityTests
{
    private static TableMetadata CreateTableMetadata(string domain, string schema, string table, List<TableColumn> columns)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table
            {
                SourceName = $"{domain}-source",
                SchemaName = schema,
                TableName = table,
                DisplayName = table,
                DataSourceType = DataSourceType.Sql
            },
            Columns = columns
        };
    }

    private static ClaimsPrincipal CreatePrincipal(string sid = "S-1-5-21-ALICE", string role = "Analyst")
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, sid));
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task CrossDomainJoin_MultiTenantIsolation_EnforcesTenantBoundaries()
    {
        // SEC-CDJ-01: Cross-tenant data isolation test
        var invoicesTable = new TableIdentifier("finance", "dbo", "Invoices");
        var customersTable = new TableIdentifier("crm", "dbo", "Customers");

        var invoicesMeta = CreateTableMetadata("finance", "dbo", "Invoices", [
            new TableColumn { ColumnName = "InvoiceId", DataType = "int" },
            new TableColumn { ColumnName = "CustomerId", DataType = "int" },
            new TableColumn { ColumnName = "Amount", DataType = "decimal" },
            new TableColumn { ColumnName = "TenantId", DataType = "varchar" }
        ]);

        var customersMeta = CreateTableMetadata("crm", "dbo", "Customers", [
            new TableColumn { ColumnName = "CustomerId", DataType = "int" },
            new TableColumn { ColumnName = "CustomerName", DataType = "varchar" },
            new TableColumn { ColumnName = "TenantId", DataType = "varchar" }
        ]);

        var registry = new InMemoryConnectorRegistry();

        // Invoices: 2 invoices for tenant-alpha (customer 100 and customer 200)
        var invoiceRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["InvoiceId"] = 1, ["CustomerId"] = 100, ["Amount"] = 500m, ["TenantId"] = "tenant-alpha" },
            new Dictionary<string, object?> { ["InvoiceId"] = 2, ["CustomerId"] = 200, ["Amount"] = 900m, ["TenantId"] = "tenant-alpha" }
        };
        var invoicesConnector = new LegacyDataSourceExecutorAdapter(new MockDataExecutor(invoiceRows), "finance");
        registry.RegisterConnector("finance", invoicesConnector);

        // Customers: customer 100 belongs to tenant-alpha; customer 200 belongs to tenant-beta (cross-tenant attacker payload!)
        var customerRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["CustomerId"] = 100, ["CustomerName"] = "Acme Alpha", ["TenantId"] = "tenant-alpha" },
            new Dictionary<string, object?> { ["CustomerId"] = 200, ["CustomerName"] = "Hostile Beta Corp", ["TenantId"] = "tenant-beta" }
        };
        var customersConnector = new LegacyDataSourceExecutorAdapter(new MockDataExecutor(customerRows), "crm");
        registry.RegisterConnector("crm", customersConnector);

        var metaRepo = new MockMetadataRepo([invoicesMeta, customersMeta]);
        var accessResolver = new MockAccessResolver(allowAll: true);
        var maskingProvider = new MockMaskingProvider();

        var joinEngine = new CrossDomainJoinEngine(registry, metaRepo, accessResolver, maskingProvider);

        var request = new CrossDomainJoinRequest(
            PrimaryTable: invoicesTable,
            JoinedTable: customersTable,
            ForeignKeyColumn: "CustomerId",
            PrimaryKeyColumn: "CustomerId",
            TargetRelationPropertyName: "Customer",
            Principal: CreatePrincipal(),
            Tenant: new TenantId("tenant-alpha"));

        var result = await joinEngine.ExecuteJoinAsync(request);

        result.Rows.Count.ShouldBe(2);

        // Invoice 1 should have joined customer 100 (same tenant)
        var row1 = result.Rows[0];
        row1["Customer"].ShouldNotBeNull();
        var c1 = (IReadOnlyDictionary<string, object?>)row1["Customer"]!;
        c1["CustomerName"].ShouldBe("Acme Alpha");

        // Invoice 2 joined with customer 200 MUST BE NULL because customer 200 belongs to tenant-beta!
        var row2 = result.Rows[1];
        row2["Customer"].ShouldBeNull();
        result.JoinedEntitiesMergedCount.ShouldBe(1);
    }

    [Fact]
    public async Task CrossDomainJoin_IndependentConsentEnforcement_BlocksUnauthorizedJoinedTable()
    {
        // SEC-CDJ-02: User has consent for Invoices, but NO consent for Customers
        var invoicesTable = new TableIdentifier("finance", "dbo", "Invoices");
        var customersTable = new TableIdentifier("crm", "dbo", "Customers");

        var invoicesMeta = CreateTableMetadata("finance", "dbo", "Invoices", [
            new TableColumn { ColumnName = "InvoiceId", DataType = "int" },
            new TableColumn { ColumnName = "CustomerId", DataType = "int" }
        ]);

        var customersMeta = CreateTableMetadata("crm", "dbo", "Customers", [
            new TableColumn { ColumnName = "CustomerId", DataType = "int" },
            new TableColumn { ColumnName = "SecretCreditScore", DataType = "varchar" }
        ]);

        var registry = new InMemoryConnectorRegistry();
        var invoiceRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["InvoiceId"] = 1, ["CustomerId"] = 100 }
        };
        registry.RegisterConnector("finance", new LegacyDataSourceExecutorAdapter(new MockDataExecutor(invoiceRows), "finance"));

        var customerRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["CustomerId"] = 100, ["SecretCreditScore"] = "TOP_SECRET_SCORE" }
        };
        registry.RegisterConnector("crm", new LegacyDataSourceExecutorAdapter(new MockDataExecutor(customerRows), "crm"));

        var metaRepo = new MockMetadataRepo([invoicesMeta, customersMeta]);

        // Consent allows finance, but denies crm
        var accessResolver = new MockSelectiveAccessResolver(table => table.Domain == "finance");
        var maskingProvider = new MockMaskingProvider();

        var joinEngine = new CrossDomainJoinEngine(registry, metaRepo, accessResolver, maskingProvider);

        var request = new CrossDomainJoinRequest(
            PrimaryTable: invoicesTable,
            JoinedTable: customersTable,
            ForeignKeyColumn: "CustomerId",
            PrimaryKeyColumn: "CustomerId",
            TargetRelationPropertyName: "Customer",
            Principal: CreatePrincipal(),
            Tenant: null);

        var result = await joinEngine.ExecuteJoinAsync(request);

        result.Rows.Count.ShouldBe(1);
        result.JoinedTableAccessAllowed.ShouldBeFalse();
        result.Rows[0]["Customer"].ShouldBeNull();
    }

    [Fact]
    public async Task CrossDomainJoin_WhenPrimaryAccessDenied_ThrowsSecurityException()
    {
        var invoicesTable = new TableIdentifier("finance", "dbo", "Invoices");
        var customersTable = new TableIdentifier("crm", "dbo", "Customers");

        var invoicesMeta = CreateTableMetadata("finance", "dbo", "Invoices", [
            new TableColumn { ColumnName = "Id", DataType = "int" },
            new TableColumn { ColumnName = "CustomerId", DataType = "int" }
        ]);
        var customersMeta = CreateTableMetadata("crm", "dbo", "Customers", [new TableColumn { ColumnName = "Id", DataType = "int" }]);

        var registry = new InMemoryConnectorRegistry();
        registry.RegisterConnector("finance", new LegacyDataSourceExecutorAdapter(new MockDataExecutor([]), "finance"));
        registry.RegisterConnector("crm", new LegacyDataSourceExecutorAdapter(new MockDataExecutor([]), "crm"));

        var metaRepo = new MockMetadataRepo([invoicesMeta, customersMeta]);
        var accessResolver = new MockSelectiveAccessResolver(table => false); // Deny all
        var maskingProvider = new MockMaskingProvider();

        var joinEngine = new CrossDomainJoinEngine(registry, metaRepo, accessResolver, maskingProvider);

        var request = new CrossDomainJoinRequest(
            PrimaryTable: invoicesTable,
            JoinedTable: customersTable,
            ForeignKeyColumn: "CustomerId",
            PrimaryKeyColumn: "Id",
            TargetRelationPropertyName: "Customer",
            Principal: CreatePrincipal(),
            Tenant: null);

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await joinEngine.ExecuteJoinAsync(request);
        });

        ex.Message.ShouldContain("Zero-Trust-Verletzung");
    }

    [Fact]
    public void PushdownPlanner_UnsupportedCapability_FallsBackSafelyToMemoryFilter()
    {
        var meta = CreateTableMetadata("finance", "dbo", "Invoices", [
            new TableColumn { ColumnName = "Id", DataType = "int" },
            new TableColumn { ColumnName = "Amount", DataType = "decimal" }
        ]);

        var planner = new PushdownPlanner();
        var capabilities = new ConnectorCapabilities(ConnectorFeatures.None, MaxBatchSize: 1000); // No pushdown capabilities
        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            rowFilterSql: "Amount > 100",
            hasUnconstrainedColumnAllow: true);

        var plan = planner.CreatePlan(
            meta,
            capabilities,
            decision,
            requestedFields: ["Id"],
            queryArguments: null,
            first: 25,
            after: 0);

        // Filter pushdown must be intercepted and routed to in-memory filter
        plan.RequiresMemoryFilter.ShouldBeTrue();
        plan.PushedFilterSql.ShouldBeNull();
        plan.ResidualFilterSql.ShouldBe("Amount > 100");
    }

    [Fact]
    public async Task StreamingResultPipeline_StreamsRowsWithInStreamMasking()
    {
        var meta = CreateTableMetadata("finance", "dbo", "Invoices", [
            new TableColumn { ColumnName = "Id", DataType = "int" },
            new TableColumn { ColumnName = "Iban", DataType = "varchar", IsSensitive = true }
        ]);

        var rawRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1, ["Iban"] = "DE1234567890" },
            new Dictionary<string, object?> { ["Id"] = 2, ["Iban"] = "DE9876543210" }
        };

        var connector = new LegacyDataSourceExecutorAdapter(new MockDataExecutor(rawRows), "finance");
        var maskingProvider = new MockMaskingProvider();
        var pipeline = new StreamingResultPipeline(maskingProvider);

        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel> { ["Id"] = ColumnAccessLevel.Clear, ["Iban"] = ColumnAccessLevel.Mask });

        var session = new ConnectorSessionContext(
            Principal: CreatePrincipal(),
            Tenant: null,
            AccessDecision: decision,
            ProjectedColumns: ["Id", "Iban"],
            Arguments: new Dictionary<string, object?>());

        var streamedRows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in pipeline.StreamRowsAsync(meta, session, connector))
        {
            streamedRows.Add(row);
        }

        streamedRows.Count.ShouldBe(2);
        streamedRows[0]["Iban"].ShouldBe("###MASKED###");
        streamedRows[1]["Iban"].ShouldBe("###MASKED###");
    }

    [Fact]
    public async Task CrossDomainJoin_EnforcesZeroTrustMaskingOnPrimaryRows()
    {
        // SEC-CDJ-05: Ensure driving primary table has column masking applied!
        var invoicesTable = new TableIdentifier("finance", "dbo", "Invoices");
        var customersTable = new TableIdentifier("crm", "dbo", "Customers");

        var invoicesMeta = CreateTableMetadata("finance", "dbo", "Invoices", [
            new TableColumn { ColumnName = "InvoiceId", DataType = "int" },
            new TableColumn { ColumnName = "CustomerId", DataType = "int" },
            new TableColumn { ColumnName = "PrimaryIban", DataType = "varchar", IsSensitive = true }
        ]);

        var customersMeta = CreateTableMetadata("crm", "dbo", "Customers", [
            new TableColumn { ColumnName = "CustomerId", DataType = "int" },
            new TableColumn { ColumnName = "CustomerName", DataType = "varchar" }
        ]);

        var registry = new InMemoryConnectorRegistry();
        var invoiceRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["InvoiceId"] = 1, ["CustomerId"] = 100, ["PrimaryIban"] = "DE1111222233" }
        };
        registry.RegisterConnector("finance", new LegacyDataSourceExecutorAdapter(new MockDataExecutor(invoiceRows), "finance"));

        var customerRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["CustomerId"] = 100, ["CustomerName"] = "Acme Global" }
        };
        registry.RegisterConnector("crm", new LegacyDataSourceExecutorAdapter(new MockDataExecutor(customerRows), "crm"));

        var metaRepo = new MockMetadataRepo([invoicesMeta, customersMeta]);
        var accessResolver = new MockAccessResolver(allowAll: true);
        var maskingProvider = new MockMaskingProvider();

        var joinEngine = new CrossDomainJoinEngine(registry, metaRepo, accessResolver, maskingProvider);

        var request = new CrossDomainJoinRequest(
            PrimaryTable: invoicesTable,
            JoinedTable: customersTable,
            ForeignKeyColumn: "CustomerId",
            PrimaryKeyColumn: "CustomerId",
            TargetRelationPropertyName: "Customer",
            Principal: CreatePrincipal(),
            Tenant: null);

        var result = await joinEngine.ExecuteJoinAsync(request);

        result.Rows.Count.ShouldBe(1);
        var primaryRow = result.Rows[0];
        primaryRow["PrimaryIban"].ShouldBe("###MASKED###");
        primaryRow["Customer"].ShouldNotBeNull();
    }

    [Fact]
    public async Task CrossDomainJoin_InvalidOrMaliciousColumns_ThrowsArgumentException()
    {
        // SEC-CDJ-06: Identifier injection defense & catalog column validation
        var invoicesTable = new TableIdentifier("finance", "dbo", "Invoices");
        var customersTable = new TableIdentifier("crm", "dbo", "Customers");

        var invoicesMeta = CreateTableMetadata("finance", "dbo", "Invoices", [
            new TableColumn { ColumnName = "Id", DataType = "int" }
        ]);

        var customersMeta = CreateTableMetadata("crm", "dbo", "Customers", [
            new TableColumn { ColumnName = "Id", DataType = "int" }
        ]);

        var registry = new InMemoryConnectorRegistry();
        registry.RegisterConnector("finance", new LegacyDataSourceExecutorAdapter(new MockDataExecutor([]), "finance"));
        registry.RegisterConnector("crm", new LegacyDataSourceExecutorAdapter(new MockDataExecutor([]), "crm"));

        var metaRepo = new MockMetadataRepo([invoicesMeta, customersMeta]);
        var accessResolver = new MockAccessResolver(allowAll: true);
        var maskingProvider = new MockMaskingProvider();

        var joinEngine = new CrossDomainJoinEngine(registry, metaRepo, accessResolver, maskingProvider);

        // Test non-existent / malicious foreign key column
        var req1 = new CrossDomainJoinRequest(
            PrimaryTable: invoicesTable,
            JoinedTable: customersTable,
            ForeignKeyColumn: "NonExistentFk; DROP TABLE Users;--",
            PrimaryKeyColumn: "Id",
            TargetRelationPropertyName: "Customer",
            Principal: CreatePrincipal(),
            Tenant: null);

        var ex1 = await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await joinEngine.ExecuteJoinAsync(req1);
        });
        ex1.Message.ShouldContain("Fremdschlüsselspalte");

        // Test non-existent / malicious primary key column
        var req2 = new CrossDomainJoinRequest(
            PrimaryTable: invoicesTable,
            JoinedTable: customersTable,
            ForeignKeyColumn: "Id",
            PrimaryKeyColumn: "MaliciousPk' OR 1=1--",
            TargetRelationPropertyName: "Customer",
            Principal: CreatePrincipal(),
            Tenant: null);

        var ex2 = await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await joinEngine.ExecuteJoinAsync(req2);
        });
        ex2.Message.ShouldContain("Primärschlüsselspalte");
    }

    private sealed class MockDataExecutor : IDataSourceExecutor
    {
        private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows;
        public MockDataExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) => _rows = rows;
        public DataSourceType SupportedType => DataSourceType.Sql;
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
            => Task.FromResult(_rows);
    }

    private sealed class MockMetadataRepo : ITableMetadataRepository
    {
        private readonly List<TableMetadata> _tables;
        public MockMetadataRepo(IEnumerable<TableMetadata> tables) => _tables = [.. tables];
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(_tables.Find(t => t.Identifier.Equals(table)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>(_tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
    }

    private sealed class MockAccessResolver : ICrossDomainAccessResolver
    {
        private readonly bool _allowAll;
        public MockAccessResolver(bool allowAll) => _allowAll = allowAll;
        public Task<TableAccessDecision> ResolveAccessAsync(
            ClaimsPrincipal principal,
            TableIdentifier table,
            TableMetadata metadata,
            TenantId? tenant,
            CancellationToken ct = default)
        {
            if (_allowAll)
                return Task.FromResult(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
            return Task.FromResult(TableAccessDecision.Denied(table, "Access denied by mock"));
        }
    }

    private sealed class MockSelectiveAccessResolver : ICrossDomainAccessResolver
    {
        private readonly Func<TableIdentifier, bool> _predicate;
        public MockSelectiveAccessResolver(Func<TableIdentifier, bool> predicate) => _predicate = predicate;
        public Task<TableAccessDecision> ResolveAccessAsync(
            ClaimsPrincipal principal,
            TableIdentifier table,
            TableMetadata metadata,
            TenantId? tenant,
            CancellationToken ct = default)
        {
            if (_predicate(table))
                return Task.FromResult(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
            return Task.FromResult(TableAccessDecision.Denied(table, "Selective mock denial"));
        }
    }

    private sealed class MockMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => "###MASKED###";
    }
}
