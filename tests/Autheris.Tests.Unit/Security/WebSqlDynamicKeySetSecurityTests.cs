namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Federation.KeySet;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using TrinoSqlEngine.Analysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

public sealed class WebSqlDynamicKeySetSecurityTests
{
    private static TableMetadata CreateSqlTable(
        string domain,
        string schema,
        string table,
        IReadOnlyList<string> columns,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        IReadOnlyList<string>? pkColumns = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table
            {
                TableName = table,
                SchemaName = schema,
                SourceType = dialect.ToString(),
                DataSourceType = DataSourceType.Sql,
                SourceName = domain,
                IsActive = true
            },
            PrimaryKeyColumns = pkColumns ?? [columns[0]],
            Columns = columns.Select(c => new TableColumn
            {
                ColumnName = c,
                DataType = "varchar"
            }).ToList()
        };
    }

    private static TableAccessTarget Target(string catalog, string schema, string table, string alias) =>
        new(catalog, schema, table, alias, $"{catalog}.{schema}.{table}");

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA124-01, SEC-FED-19
    public void T_SEC_DATA124_01_MaskedKey_NeverPushed()
    {
        var drivingTable = CreateSqlTable("crm", "public", "customers", ["id", "name"]);
        var drivenTable = CreateSqlTable("erp", "public", "orders", ["id", "customer_id", "total"]);

        // 1. Both Clear -> Allowed
        var drivingDecisionClear = TableAccessDecision.Allowed(drivingTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var drivenDecisionClear = TableAccessDecision.Allowed(drivenTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["customer_id"] = ColumnAccessLevel.Clear,
            ["total"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        KeySetGovernanceGuard.AreJoinKeysClear(
            drivingTable, drivingDecisionClear, "id",
            drivenTable, drivenDecisionClear, "customer_id").ShouldBeTrue();

        // 2. Driving key Mask -> Blocked
        var drivingDecisionMask = TableAccessDecision.Allowed(drivingTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Mask,
            ["name"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        KeySetGovernanceGuard.AreJoinKeysClear(
            drivingTable, drivingDecisionMask, "id",
            drivenTable, drivenDecisionClear, "customer_id").ShouldBeFalse();

        // 3. Driving key Missing / Not Clear -> Blocked
        var drivingDecisionMissing = TableAccessDecision.Allowed(drivingTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["name"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: false);

        KeySetGovernanceGuard.AreJoinKeysClear(
            drivingTable, drivingDecisionMissing, "id",
            drivenTable, drivenDecisionClear, "customer_id").ShouldBeFalse();

        // 4. Driving key Deny -> Blocked
        var drivingDecisionDeny = TableAccessDecision.Allowed(drivingTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Deny,
            ["name"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        KeySetGovernanceGuard.AreJoinKeysClear(
            drivingTable, drivingDecisionDeny, "id",
            drivenTable, drivenDecisionClear, "customer_id").ShouldBeFalse();

        // 5. Driven key Mask -> Blocked
        var drivenDecisionMask = TableAccessDecision.Allowed(drivenTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["customer_id"] = ColumnAccessLevel.Mask,
            ["total"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        KeySetGovernanceGuard.AreJoinKeysClear(
            drivingTable, drivingDecisionClear, "id",
            drivenTable, drivenDecisionMask, "customer_id").ShouldBeFalse();

        // 6. Driven key Missing / Not Clear -> Blocked
        var drivenDecisionMissing = TableAccessDecision.Allowed(drivenTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["total"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: false);

        KeySetGovernanceGuard.AreJoinKeysClear(
            drivingTable, drivingDecisionClear, "id",
            drivenTable, drivenDecisionMissing, "customer_id").ShouldBeFalse();

        // 7. Driven key Deny -> Blocked
        var drivenDecisionDeny = TableAccessDecision.Allowed(drivenTable.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["customer_id"] = ColumnAccessLevel.Deny,
            ["total"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        KeySetGovernanceGuard.AreJoinKeysClear(
            drivingTable, drivingDecisionClear, "id",
            drivenTable, drivenDecisionDeny, "customer_id").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA124-02
    public async Task T_SEC_DATA124_02_AllowKeySetImport_OptInEnforced()
    {
        var customers = CreateSqlTable("crm", "public", "customers", ["id", "name"]);
        var orders = CreateSqlTable("erp", "public", "orders", ["id", "customer_id", "total"]);

        var customerRows = new[]
        {
            Row(("id", "101"), ("name", "Alice")),
            Row(("id", "102"), ("name", "Bob"))
        };

        var orderRows = new[]
        {
            Row(("id", "1"), ("customer_id", "101"), ("total", "50")),
            Row(("id", "2"), ("customer_id", "102"), ("total", "100"))
        };

        var stubCustomers = new StubTableConnector("crm", customerRows);
        var stubOrders = new StubTableConnector("erp", orderRows);
        var registry = StubRegistry.For((customers, stubCustomers), (orders, stubOrders));

        var reqCustomers = new StagingTableRequest(
            Target("crm", "public", "customers", "c"),
            customers,
            Unrestricted(customers),
            "s0",
            ["id", "name"]);

        var reqOrders = new StagingTableRequest(
            Target("erp", "public", "orders", "o"),
            orders,
            Unrestricted(orders),
            "s1",
            ["id", "customer_id", "total"],
            KeySetPlan: new KeySetPlanInfo("s0", customers.Identifier, "id", "customer_id"));

        // Case A: AllowKeySetImport = false (Default) -> Key-set pushdown bypassed
        var auditWithoutOptIn = Substitute.For<IAuditLogRepository>();
        var gatewayOptionsWithoutOptIn = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = "PostgreSql", AllowKeySetImport = false }
                }
            }
        };

        var stagingServiceA = Staging(registry, gatewayOptionsWithoutOptIn, auditWithoutOptIn);
        var stagedA = await stagingServiceA.StageAsync([reqCustomers, reqOrders], User(), Tenant, Budget());

        stagedA.Count.ShouldBe(2);
        await auditWithoutOptIn.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(a => a.TargetTable == orders.Identifier.ToString() && a.DetailsJson.Contains("\"keySetFilter\":false")),
            Arg.Any<CancellationToken>());

        // Case B: AllowKeySetImport = true -> Key-set pushdown executed
        var auditWithOptIn = Substitute.For<IAuditLogRepository>();
        var gatewayOptionsWithOptIn = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = "PostgreSql", AllowKeySetImport = true }
                }
            }
        };

        var stagingServiceB = Staging(registry, gatewayOptionsWithOptIn, auditWithOptIn);
        var stagedB = await stagingServiceB.StageAsync([reqCustomers, reqOrders], User(), Tenant, Budget());

        stagedB.Count.ShouldBe(2);
        await auditWithOptIn.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(a => a.TargetTable == orders.Identifier.ToString() && a.DetailsJson.Contains("\"keySetFilter\":true")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA124-03
    public async Task T_SEC_DATA124_03_KeyBounds_Exceeded_Fallback()
    {
        var customers = CreateSqlTable("crm", "public", "customers", ["id", "name"]);
        var orders = CreateSqlTable("erp", "public", "orders", ["id", "customer_id", "total"]);

        // Generate 2,001 distinct keys (> MaxKeySetKeys = 2000)
        var customerRows = Enumerable.Range(1, 2001)
            .Select(i => Row(("id", i.ToString()), ("name", $"User_{i}")))
            .ToArray();

        var orderRows = new[]
        {
            Row(("id", "1"), ("customer_id", "1"), ("total", "50"))
        };

        var stubCustomers = new StubTableConnector("crm", customerRows);
        var stubOrders = new StubTableConnector("erp", orderRows);
        var registry = StubRegistry.For((customers, stubCustomers), (orders, stubOrders));

        var reqCustomers = new StagingTableRequest(
            Target("crm", "public", "customers", "c"),
            customers,
            Unrestricted(customers),
            "s0",
            ["id", "name"]);

        var reqOrders = new StagingTableRequest(
            Target("erp", "public", "orders", "o"),
            orders,
            Unrestricted(orders),
            "s1",
            ["id", "customer_id", "total"],
            KeySetPlan: new KeySetPlanInfo("s0", customers.Identifier, "id", "customer_id"));

        var audit = Substitute.For<IAuditLogRepository>();
        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = "PostgreSql", AllowKeySetImport = true }
                }
            },
            WebSql = new WebSqlOptions
            {
                CrossSource = new CrossSourceOptions
                {
                    DynamicKeySet = new DynamicKeySetOptions
                    {
                        MaxKeySetKeys = 2000,
                        MaxKeySetBatches = 10
                    }
                }
            }
        };

        var budget = new FederationBudget(5, 5000, 10000, 10000000, 20000000, 30, 1);
        var staging = Staging(registry, gatewayOptions, audit);

        // When bounds are exceeded, it cleanly falls back to plain staging without throwing unhandled exception
        var staged = await staging.StageAsync([reqCustomers, reqOrders], User(), Tenant, budget);

        staged.Count.ShouldBe(2);
        // Target table audit records keySetFilter: false (plain staging read executed)
        await audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(a => a.TargetTable == orders.Identifier.ToString() && a.DetailsJson.Contains("\"keySetFilter\":false")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA124-03
    public void T_SEC_DATA124_04_BatchParameterClamping_SqlDialect()
    {
        // SQL Server ceiling is 2,090 parameters
        int clampedSqlServer = KeyBatcher.ResolveBatchSize(DatabaseDialect.SqlServer, 5000);
        clampedSqlServer.ShouldBe(2090);

        // Even with composite keys (2 columns per key), parameters stay <= 2,090
        int compositeBatchSize = KeyBatcher.ResolveBatchSize(DatabaseDialect.SqlServer, 5000, columnsPerKey: 2);
        (compositeBatchSize * 2).ShouldBeLessThanOrEqualTo(2090);

        // Batch generation honors clamped size
        var keys = Enumerable.Range(1, 3000).Cast<object>().ToList();
        var batches = KeyBatcher.CreateBatches(keys, clampedSqlServer);

        batches.Count.ShouldBe(2);
        batches[0].Count.ShouldBe(2090);
        batches[1].Count.ShouldBe(910);
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA124-04
    public async Task T_SEC_DATA124_05_OrdinalDeduplication_CollationSafety()
    {
        var customers = CreateSqlTable("crm", "public", "customers", ["id", "code"]);
        var orders = CreateSqlTable("erp", "public", "orders", ["id", "customer_code", "total"], pkColumns: ["id"]);

        // Driving table returns two distinct codes under ordinal comparison: "ABC" and "abc"
        var customerRows = new[]
        {
            Row(("id", "1"), ("code", "ABC")),
            Row(("id", "2"), ("code", "abc"))
        };

        // SQL Server case-insensitive collation matches both "ABC" and "abc" to the exact same order row
        var duplicateOrder = Row(("id", "order_1"), ("customer_code", "ABC"), ("total", "99"));

        var stubCustomers = new StubTableConnector("crm", customerRows);
        // Stub returns duplicateOrder for each batch call
        var stubOrders = new StubTableConnector("erp", [duplicateOrder]);
        var registry = StubRegistry.For((customers, stubCustomers), (orders, stubOrders));

        var reqCustomers = new StagingTableRequest(
            Target("crm", "public", "customers", "c"),
            customers,
            Unrestricted(customers),
            "s0",
            ["id", "code"]);

        var reqOrders = new StagingTableRequest(
            Target("erp", "public", "orders", "o"),
            orders,
            Unrestricted(orders),
            "s1",
            ["id", "customer_code", "total"],
            KeySetPlan: new KeySetPlanInfo("s0", customers.Identifier, "code", "customer_code"));

        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = "SqlServer", AllowKeySetImport = true }
                }
            },
            WebSql = new WebSqlOptions
            {
                CrossSource = new CrossSourceOptions
                {
                    DynamicKeySet = new DynamicKeySetOptions
                    {
                        MaxKeySetBatchSize = 1 // Forces 2 separate batches: 1 for "ABC", 1 for "abc"
                    }
                }
            }
        };

        var staging = Staging(registry, gatewayOptions);
        var staged = await staging.StageAsync([reqCustomers, reqOrders], User(), Tenant, Budget());

        var orderStaged = staged.First(s => s.Table == orders.Identifier);
        // Gateway ordinal deduplication must drop duplicate rows across batches
        orderStaged.GovernedRows.Count.ShouldBe(1);
        ((string?)orderStaged.GovernedRows[0]["id"]).ShouldBe("order_1");
    }

    [Fact]
    [Trait("Category", "Security")] // SEC-DATA124-05
    public async Task T_SEC_DATA124_06_Audit_ZeroPayload()
    {
        var customers = CreateSqlTable("crm", "public", "customers", ["id", "name"]);
        var orders = CreateSqlTable("erp", "public", "orders", ["id", "customer_id", "total"]);

        const string secretKeyAlpha = "SENSITIVE_SECRET_TOKEN_ALPHA_777";
        const string secretKeyBeta = "SENSITIVE_SECRET_TOKEN_BETA_888";

        var customerRows = new[]
        {
            Row(("id", secretKeyAlpha), ("name", "Alice")),
            Row(("id", secretKeyBeta), ("name", "Bob"))
        };

        var orderRows = new[]
        {
            Row(("id", "1"), ("customer_id", secretKeyAlpha), ("total", "100")),
            Row(("id", "2"), ("customer_id", secretKeyBeta), ("total", "200"))
        };

        var stubCustomers = new StubTableConnector("crm", customerRows);
        var stubOrders = new StubTableConnector("erp", orderRows);
        var registry = StubRegistry.For((customers, stubCustomers), (orders, stubOrders));

        var reqCustomers = new StagingTableRequest(
            Target("crm", "public", "customers", "c"),
            customers,
            Unrestricted(customers),
            "s0",
            ["id", "name"]);

        var reqOrders = new StagingTableRequest(
            Target("erp", "public", "orders", "o"),
            orders,
            Unrestricted(orders),
            "s1",
            ["id", "customer_id", "total"],
            KeySetPlan: new KeySetPlanInfo("s0", customers.Identifier, "id", "customer_id"));

        var recordedAudits = new List<AuditLogEntry>();
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recordedAudits.Add(e)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = "PostgreSql", AllowKeySetImport = true }
                }
            }
        };

        var staging = Staging(registry, gatewayOptions, audit);
        await staging.StageAsync([reqCustomers, reqOrders], User(), Tenant, Budget());

        var orderAudit = recordedAudits.FirstOrDefault(a => a.TargetTable == orders.Identifier.ToString());
        orderAudit.ShouldNotBeNull();

        // 1. Structural fields must be present
        orderAudit.DetailsJson.ShouldContain("\"keySetFilter\":true");
        orderAudit.DetailsJson.ShouldContain("\"keyCount\":2");
        orderAudit.DetailsJson.ShouldContain("\"batchCount\":1");
        orderAudit.DetailsJson.ShouldContain("\"targetColumn\":\"customer_id\"");

        // 2. Zero-Payload invariant: Neither literal key may appear anywhere in the audit details
        orderAudit.DetailsJson.ShouldNotContain(secretKeyAlpha);
        orderAudit.DetailsJson.ShouldNotContain(secretKeyBeta);
    }

    [Fact]
    [Trait("Category", "Security")] // Parity / DoD
    public async Task T_SEC_DATA124_07_BitForBitParity_ResultCorrectness()
    {
        var customers = CreateSqlTable("crm", "public", "customers", ["id", "tier"]);
        var orders = CreateSqlTable("erp", "public", "orders", ["id", "customer_id", "amount"], pkColumns: ["id"]);

        // Driving side: 2 VIP customers
        var customerRows = new[]
        {
            Row(("id", "C1"), ("tier", "VIP")),
            Row(("id", "C2"), ("tier", "VIP"))
        };

        // Driven side: 5 total orders, 3 belong to VIP customers (C1, C2), 2 belong to C3 (non-VIP)
        var allOrderRows = new[]
        {
            Row(("id", "O1"), ("customer_id", "C1"), ("amount", "50")),
            Row(("id", "O2"), ("customer_id", "C1"), ("amount", "75")),
            Row(("id", "O3"), ("customer_id", "C2"), ("amount", "120")),
            Row(("id", "O4"), ("customer_id", "C3"), ("amount", "30")),
            Row(("id", "O5"), ("customer_id", "C3"), ("amount", "40"))
        };

        var stubCustomers = new StubTableConnector("crm", customerRows);

        // Stub connector that filters by TableQueryItems.Filter if present, or returns all rows
        var stubOrders = new StubTableConnector("erp", allOrderRows, session => { });
        var recordSource = Substitute.For<IConnectorRecordSource>();
        stubOrders.Connector.RecordSource.Returns(recordSource);

        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var session = ci.ArgAt<ConnectorSessionContext>(1);
                if (session.Items.TryGetValue(TableQueryItems.Filter, out var filterObj) && filterObj is TableFilterClause filter)
                {
                    var allowedKeys = new HashSet<string>(filter.Parameters.Values.Select(v => v?.ToString() ?? string.Empty));
                    var filtered = allOrderRows.Where(r => allowedKeys.Contains((string)r["customer_id"]!)).ToList();
                    return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(filtered);
                }
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(allOrderRows);
            });

        var registry = StubRegistry.For((customers, stubCustomers), (orders, stubOrders));

        var reqCustomers = new StagingTableRequest(
            Target("crm", "public", "customers", "c"),
            customers,
            Unrestricted(customers),
            "s0",
            ["id", "tier"]);

        var reqOrders = new StagingTableRequest(
            Target("erp", "public", "orders", "o"),
            orders,
            Unrestricted(orders),
            "s1",
            ["id", "customer_id", "amount"],
            KeySetPlan: new KeySetPlanInfo("s0", customers.Identifier, "id", "customer_id"));

        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = "PostgreSql", AllowKeySetImport = true }
                }
            }
        };

        var staging = Staging(registry, gatewayOptions);
        var staged = await staging.StageAsync([reqCustomers, reqOrders], User(), Tenant, Budget());

        var stagedOrders = staged.First(s => s.Table == orders.Identifier).GovernedRows;
        stagedOrders.Count.ShouldBe(3);
        stagedOrders.Select(r => (string)r["id"]!).OrderBy(id => id).ShouldBe(["O1", "O2", "O3"]);
    }
}
