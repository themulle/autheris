namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

public sealed class PreStagingMaskingTests
{
    private const string Tenant = "tenant_test";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateCustomerTable()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "customers"),
            Table = new Table
            {
                TableName = "customers",
                SchemaName = "public",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "varchar" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar" },
                new TableColumn { ColumnName = "salary", DataType = "int" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["email"] = new MaskingRule { RuleType = "REDACT" },
                ["salary"] = new MaskingRule { RuleType = "REDACT" }
            }
        };
    }

    private static FederationBudget CreateBudget(int maxRows = 1000) =>
        new(
            MaxTableCount: 5,
            MaxStagedRowsPerTable: maxRows,
            MaxTotalStagedRows: 5000,
            MaxStagedBytesPerTable: 32 * 1024 * 1024,
            MaxTotalStagedBytes: 128 * 1024 * 1024,
            TimeoutSeconds: 30,
            MaxParallelSourceReads: 4);

    [Fact]
    public async Task StageAsync_DecidesAllTablesBeforeFirstRead()
    {
        var meta1 = CreateCustomerTable();
        var meta2 = new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var connector1 = Substitute.For<IAutherisConnector>();
        connector1.ConnectorId.Returns("crm");
        var recordSource1 = Substitute.For<IConnectorRecordSource>();
        connector1.RecordSource.Returns(recordSource1);
        connector1.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("s1")]);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(meta1.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector1; return true; });

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();

        var service = new FederatedStagingService(
            registry,
            maskingProvider,
            auditRepo,
            Options.Create(new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

        var req1 = new StagingTableRequest(
            new TableAccessTarget(null, "public", "customers", "customers", "public.customers"),
            meta1,
            TableAccessDecision.Allowed(meta1.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            "s0",
            ["id", "email"]);

        // Table 2 is DENIED
        var req2 = new StagingTableRequest(
            new TableAccessTarget(null, "public", "orders", "orders", "public.orders"),
            meta2,
            TableAccessDecision.Denied(meta2.Identifier, "Forbidden"),
            "s1",
            ["id"]);

        // Act & Assert
        await Should.ThrowAsync<Exception>(async () =>
            await service.StageAsync([req1, req2], CreateUser(), new TenantId(Tenant), CreateBudget()));

        // Verification: Because Table 2 was denied, Table 1 must NEVER have been read!
        await recordSource1.DidNotReceiveWithAnyArgs().ReadBatchAsync(default!, default!, default);
        await auditRepo.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StageAsync_MaskedColumn_IsMaskedBeforeStaging_AndDeniedColumnIsAbsent()
    {
        var meta = CreateCustomerTable();

        var connector = Substitute.For<IAutherisConnector>();
        connector.ConnectorId.Returns("crm");
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.RecordSource.Returns(recordSource);
        connector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("s1")]);

        var rawRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["email"] = "alice@secret.com",
                ["ssn"] = "123-45-6789",
                ["salary"] = 100000
            }
        };
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(rawRows);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(meta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        maskingProvider.MaskValue(Arg.Is<string>("email"), Arg.Is<object?>("alice@secret.com"), Arg.Any<MaskingRule>())
            .Returns("***REDACTED***");
        maskingProvider.MaskValue(Arg.Is<string>("salary"), Arg.Is<object?>(100000), Arg.Any<MaskingRule>())
            .Returns("***REDACTED_INT***");

        var service = new FederatedStagingService(
            registry,
            maskingProvider,
            auditRepo,
            Options.Create(new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["ssn"] = ColumnAccessLevel.Deny,
            ["salary"] = ColumnAccessLevel.Mask
        };

        var req = new StagingTableRequest(
            new TableAccessTarget(null, "public", "customers", "customers", "public.customers"),
            meta,
            TableAccessDecision.Allowed(meta.Identifier, colAccess, hasUnconstrainedColumnAllow: false),
            "s0",
            ["id", "email", "salary"]);

        var stagedSources = await service.StageAsync([req], CreateUser(), new TenantId(Tenant), CreateBudget());

        stagedSources.Count.ShouldBe(1);
        var source = stagedSources[0];
        source.StagingTableName.ShouldBe("s0");
        source.GovernedRows.Count.ShouldBe(1);

        var stagedRow = source.GovernedRows[0];
        stagedRow["id"].ShouldBe(1);
        stagedRow["email"].ShouldBe("***REDACTED***");
        stagedRow["salary"].ShouldBe("***REDACTED_INT***");

        // INV-3: Denied column must be stripped and completely absent
        stagedRow.ContainsKey("ssn").ShouldBeFalse();

        // Staged source should mark masked columns so DuckDB stages them as VARCHAR
        source.MaskedColumns.ShouldNotBeNull();
        source.MaskedColumns.ShouldContain("email");
        source.MaskedColumns.ShouldContain("salary");
    }

    [Fact]
    public async Task StageAsync_RowLimitExceeded_Throws_NeverTruncates()
    {
        var meta = CreateCustomerTable();

        var connector = Substitute.For<IAutherisConnector>();
        connector.ConnectorId.Returns("crm");
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.RecordSource.Returns(recordSource);
        connector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("s1")]);

        var rawRows = Enumerable.Range(1, 10).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = i,
            ["email"] = $"user{i}@test.com"
        }).ToList();

        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(rawRows);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(meta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();

        var service = new FederatedStagingService(
            registry,
            maskingProvider,
            auditRepo,
            Options.Create(new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

        var req = new StagingTableRequest(
            new TableAccessTarget(null, "public", "customers", "customers", "public.customers"),
            meta,
            TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            "s0",
            ["id", "email"]);

        // Budget allows max 5 rows; 10 rows returned
        var budget = CreateBudget(maxRows: 5);

        await Should.ThrowAsync<Exception>(async () =>
            await service.StageAsync([req], CreateUser(), new TenantId(Tenant), budget));
    }

    [Fact]
    public async Task StageAsync_AuditFailure_FailsClosed()
    {
        var meta = CreateCustomerTable();

        var connector = Substitute.For<IAutherisConnector>();
        connector.ConnectorId.Returns("crm");
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.RecordSource.Returns(recordSource);
        connector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("s1")]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([new Dictionary<string, object?> { ["id"] = 1, ["email"] = "a@b.com" }]);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(meta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Audit DB offline")));

        var service = new FederatedStagingService(
            registry,
            Substitute.For<IColumnMaskingProvider>(),
            auditRepo,
            Options.Create(new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

        var req = new StagingTableRequest(
            new TableAccessTarget(null, "public", "customers", "customers", "public.customers"),
            meta,
            TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            "s0",
            ["id"]);

        await Should.ThrowAsync<Exception>(async () =>
            await service.StageAsync([req], CreateUser(), new TenantId(Tenant), CreateBudget()));
    }

    [Fact]
    public async Task StageAsync_ResolvesConnectorByDataSourceType_NoDefaultSqlFallback()
    {
        var httpMeta = new TableMetadata
        {
            Identifier = new TableIdentifier("api", "v1", "shipments"),
            Table = new Table
            {
                TableName = "shipments",
                SchemaName = "v1",
                SourceType = "Http",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };

        // Registry only has "default-sql"
        var sqlConnector = Substitute.For<IAutherisConnector>();
        sqlConnector.ConnectorId.Returns("default-sql");

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(httpMeta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = sqlConnector; return true; });

        var service = new FederatedStagingService(
            registry,
            Substitute.For<IColumnMaskingProvider>(),
            Substitute.For<IAuditLogRepository>(),
            Options.Create(new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

        var req = new StagingTableRequest(
            new TableAccessTarget("api", "v1", "shipments", "shipments", "api.v1.shipments"),
            httpMeta,
            TableAccessDecision.Allowed(httpMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            "s0",
            ["id"]);

        // Must reject HTTP table when only default-sql is available
        await Should.ThrowAsync<Exception>(async () =>
            await service.StageAsync([req], CreateUser(), new TenantId(Tenant), CreateBudget()));
    }

    [Fact]
    public async Task MaskedIntegerColumn_StagesAsVarchar()
    {
        // Tests that DuckDbOlapEngine creates VARCHAR for masked columns so string masked values don't fail INSERT
        var engine = new DuckDbOlapEngine(new DuckDbOlapOptions { Enabled = true }, NullLogger<DuckDbOlapEngine>.Instance);
        var table = new TableIdentifier("sales", "public", "employees");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "salary", DataType = "int" }
            ]
        };

        // Staged row with masked salary (string in an int column)
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["salary"] = "***REDACTED***" }
        };

        var source = new OlapTableSource(
            table,
            rows,
            meta,
            StagingTableName: "s0",
            MaskedColumns: new HashSet<string>(["salary"], StringComparer.OrdinalIgnoreCase));

        var request = new OlapQueryRequest("SELECT id, salary FROM s0", [source]);

        var result = await engine.ExecuteOlapQueryAsync(request);

        result.Rows.Count.ShouldBe(1);
        result.Rows[0][1]?.ToString().ShouldBe("***REDACTED***");
    }
}
