namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
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
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

public sealed class WebSqlHeterogeneousJoinTests
{
    private const string Tenant = "tenant_test";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim(ClaimTypes.Name, "alice"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateOrdersTable()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "orders"),
            Table = new Table
            {
                TableName = "orders",
                SchemaName = "public",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                SourceName = "crm",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "tracking_id", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "numeric" }
            ]
        };
    }

    private static TableMetadata CreateShipmentsTable(bool completeResponse = true)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("logistics", "v1", "shipments"),
            Table = new Table
            {
                TableName = "shipments",
                SchemaName = "v1",
                SourceType = "Http",
                DataSourceType = DataSourceType.HttpDeclarative,
                SourceName = "logistics",
                IsActive = true,
                HttpEndpoint = new HttpEndpointDescriptor
                {
                    BaseUrl = "https://api.logistics.internal",
                    PathTemplate = "/shipments",
                    CompleteResponse = completeResponse
                }
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "carrier", DataType = "varchar" },
                new TableColumn { ColumnName = "status", DataType = "varchar" }
            ]
        };
    }

    [Fact]
    public async Task Join_SqlOrders_And_HttpShipments_ExecutesEndToEnd_AndReturnsJoinedRows()
    {
        var ordersMeta = CreateOrdersTable();
        var shipmentsMeta = CreateShipmentsTable(completeResponse: true);

        // Connector for SQL orders
        var ordersConnector = Substitute.For<IAutherisConnector>();
        ordersConnector.ConnectorId.Returns("crm");
        var ordersRecordSource = Substitute.For<IConnectorRecordSource>();
        ordersConnector.RecordSource.Returns(ordersRecordSource);
        ordersConnector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-orders")]);

        var ordersRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 101, ["tracking_id"] = "TRK-1", ["amount"] = 99.50m },
            new Dictionary<string, object?> { ["id"] = 102, ["tracking_id"] = "TRK-2", ["amount"] = 150.00m }
        };
        ordersRecordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ordersRows);

        // Connector for HTTP shipments
        var shipmentsConnector = Substitute.For<IAutherisConnector>();
        shipmentsConnector.ConnectorId.Returns("logistics");
        var shipmentsRecordSource = Substitute.For<IConnectorRecordSource>();
        shipmentsConnector.RecordSource.Returns(shipmentsRecordSource);
        shipmentsConnector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-shipments")]);

        var shipmentsRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "TRK-1", ["carrier"] = "DHL", ["status"] = "DELIVERED" },
            new Dictionary<string, object?> { ["id"] = "TRK-2", ["carrier"] = "UPS", ["status"] = "IN_TRANSIT" }
        };
        shipmentsRecordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(shipmentsRows);

        // Connector Registry
        var connectorRegistry = Substitute.For<IAutherisConnectorRegistry>();
        connectorRegistry.TryGetConnectorForTable(ordersMeta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = ordersConnector; return true; });
        connectorRegistry.TryGetConnectorForTable(shipmentsMeta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = shipmentsConnector; return true; });

        // Table Metadata Repository
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(ordersMeta.Identifier, Arg.Any<CancellationToken>())
            .Returns(ordersMeta);
        tableRepo.GetTableMetadataAsync(shipmentsMeta.Identifier, Arg.Any<CancellationToken>())
            .Returns(shipmentsMeta);

        // Gateway Options with CrossSource enabled
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "crm",
                AllowedDataSources = ["crm", "logistics"],
                CrossSource = new CrossSourceOptions
                {
                    Enabled = true,
                    AllowedTransports = ["WebSql", "Trino"]
                }
            },
            DuckDbOlap = new DuckDbOlapOptions
            {
                Enabled = true,
                MaxMemory = "256MB",
                MaxTempDirectorySize = "0B"
            }
        };

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();

        // Real DuckDB OLAP Engine
        var duckDbEngine = new DuckDbOlapEngine(options.DuckDbOlap, NullLogger<DuckDbOlapEngine>.Instance);

        // Staging Service
        var stagingService = new FederatedStagingService(
            connectorRegistry,
            maskingProvider,
            auditRepo,
            Options.Create(options),
            NullLogger<FederatedStagingService>.Instance);

        var sqlEngine = FastSqlEngine.Default;
        var router = new CrossSourceQueryRouter(tableRepo, options.WebSql.CrossSource, options.WebSql);
        var planner = new CrossSourcePlanner(sqlEngine);

        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            auditRepo,
            router,
            planner,
            stagingService,
            duckDbEngine,
            sqlEngine: sqlEngine,
            logger: NullLogger<FederatedDuckDbExecutionService>.Instance);

        var governedSqlService = new GovernedSqlExecutionService(
            Options.Create(options),
            auditRepo,
            tableRepository: tableRepo,
            sqlEngine: sqlEngine,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            federatedExecutionService: federatedService);

        var query = "SELECT o.id, o.amount, s.carrier, s.status " +
                    "FROM crm.public.orders o " +
                    "JOIN logistics.v1.shipments s ON o.tracking_id = s.id " +
                    "WHERE s.status = 'IN_TRANSIT'";

        var request = new GovernedSqlQueryRequest(query, Transport: "WebSql");

        // Act
        var result = await governedSqlService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant));

        // Assert - Only order 102 has status 'IN_TRANSIT'
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["id"]?.ToString().ShouldBe("102");
        Convert.ToDecimal(result.Rows[0]["amount"]).ShouldBe(150.00m);
        result.Rows[0]["carrier"]?.ToString().ShouldBe("UPS");
        result.Rows[0]["status"]?.ToString().ShouldBe("IN_TRANSIT");

        // Assert Audit Logs
        // 1. Start audit (WEBSQL_CROSS_SOURCE_QUERY)
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlCrossSourceQuery && e.Decision == "ALLOW"),
            Arg.Any<CancellationToken>());

        // 2. Source read audits (WEBSQL_CROSS_SOURCE_SOURCE_READ) for both tables
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable == ordersMeta.Identifier.ToQualifiedName()),
            Arg.Any<CancellationToken>());
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable == shipmentsMeta.Identifier.ToQualifiedName()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Join_HttpSourceWithoutCompleteResponse_IsRejected()
    {
        var shipmentsMeta = CreateShipmentsTable(completeResponse: false); // Incomplete response!

        var connectorRegistry = Substitute.For<IAutherisConnectorRegistry>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(shipmentsMeta.Identifier, Arg.Any<CancellationToken>())
            .Returns(shipmentsMeta);

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = ["logistics"],
                CrossSource = new CrossSourceOptions { Enabled = true }
            }
        };

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        var stagingService = new FederatedStagingService(
            connectorRegistry,
            maskingProvider,
            auditRepo,
            Options.Create(options),
            NullLogger<FederatedStagingService>.Instance);

        var duckDbEngine = Substitute.For<IDuckDbOlapEngine>();
        var sqlEngine = FastSqlEngine.Default;
        var router = new CrossSourceQueryRouter(tableRepo, options.WebSql.CrossSource, options.WebSql);
        var planner = new CrossSourcePlanner(sqlEngine);

        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            auditRepo,
            router,
            planner,
            stagingService,
            duckDbEngine,
            sqlEngine: sqlEngine);

        var query = "SELECT id, carrier FROM logistics.v1.shipments";
        var request = new GovernedSqlQueryRequest(query, Transport: "WebSql");

        // Act & Assert (E-3)
        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("CompleteResponse");
    }

    [Fact]
    public async Task Join_TransportGate_ArrowExport_IsRejected()
    {
        var ordersMeta = CreateOrdersTable();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(ordersMeta.Identifier, Arg.Any<CancellationToken>())
            .Returns(ordersMeta);

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = ["crm"],
                CrossSource = new CrossSourceOptions
                {
                    Enabled = true,
                    AllowedTransports = ["WebSql", "Trino"] // ArrowExport NOT in list
                }
            }
        };

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            auditRepo,
            Substitute.For<ICrossSourceQueryRouter>(),
            Substitute.For<ICrossSourcePlanner>(),
            Substitute.For<IFederatedStagingService>(),
            Substitute.For<IDuckDbOlapEngine>());

        var request = new GovernedSqlQueryRequest("SELECT 1", Transport: "ArrowExport");

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("Cross-catalog");
    }

    [Fact]
    public void CompileTime_Encapsulation_ExecuteGeneratedAsync_AcceptsGeneratedOlapQuery()
    {
        // Type encapsulation check: IDuckDbOlapEngine.ExecuteGeneratedAsync must accept GeneratedOlapQuery, not string.
        var method = typeof(IDuckDbOlapEngine).GetMethod("ExecuteGeneratedAsync");
        method.ShouldNotBeNull();

        var firstParam = method.GetParameters()[0];
        firstParam.ParameterType.ShouldBe(typeof(GeneratedOlapQuery));
    }

    [Fact]
    public async Task OneSourceFails_NoPartialResult_AbortsExecution()
    {
        var ordersMeta = CreateOrdersTable();
        var shipmentsMeta = CreateShipmentsTable(completeResponse: true);

        var ordersConnector = Substitute.For<IAutherisConnector>();
        ordersConnector.ConnectorId.Returns("crm");
        var ordersRecordSource = Substitute.For<IConnectorRecordSource>();
        ordersConnector.RecordSource.Returns(ordersRecordSource);
        ordersConnector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-orders")]);
        ordersRecordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["id"] = 1 } });

        // Second source fails with an exception (e.g. HTTP 500 / network failure)
        var shipmentsConnector = Substitute.For<IAutherisConnector>();
        shipmentsConnector.ConnectorId.Returns("logistics");
        var shipmentsRecordSource = Substitute.For<IConnectorRecordSource>();
        shipmentsConnector.RecordSource.Returns(shipmentsRecordSource);
        shipmentsConnector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-shipments")]);
        shipmentsRecordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Upstream HTTP service returned 500 Internal Server Error"));

        var connectorRegistry = Substitute.For<IAutherisConnectorRegistry>();
        connectorRegistry.TryGetConnectorForTable(ordersMeta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = ordersConnector; return true; });
        connectorRegistry.TryGetConnectorForTable(shipmentsMeta.Identifier, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = shipmentsConnector; return true; });

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(ordersMeta.Identifier, Arg.Any<CancellationToken>()).Returns(ordersMeta);
        tableRepo.GetTableMetadataAsync(shipmentsMeta.Identifier, Arg.Any<CancellationToken>()).Returns(shipmentsMeta);

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = ["crm", "logistics"],
                CrossSource = new CrossSourceOptions { Enabled = true }
            }
        };

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var stagingService = new FederatedStagingService(
            connectorRegistry,
            Substitute.For<IColumnMaskingProvider>(),
            auditRepo,
            Options.Create(options),
            NullLogger<FederatedStagingService>.Instance);

        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            auditRepo,
            new CrossSourceQueryRouter(tableRepo, options.WebSql.CrossSource, options.WebSql),
            new CrossSourcePlanner(FastSqlEngine.Default),
            stagingService,
            Substitute.For<IDuckDbOlapEngine>());

        var query = "SELECT o.id, s.carrier FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.tracking_id = s.id";
        var request = new GovernedSqlQueryRequest(query, Transport: "WebSql");

        // Act & Assert (INV-14: all-or-nothing, no partial results)
        await Should.ThrowAsync<Exception>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task StartAuditFailure_FailsClosed_AbortsExecution()
    {
        var ordersMeta = CreateOrdersTable();
        var shipmentsMeta = CreateShipmentsTable(completeResponse: true);

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(ordersMeta.Identifier, Arg.Any<CancellationToken>()).Returns(ordersMeta);
        tableRepo.GetTableMetadataAsync(shipmentsMeta.Identifier, Arg.Any<CancellationToken>()).Returns(shipmentsMeta);

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = ["crm", "logistics"],
                CrossSource = new CrossSourceOptions { Enabled = true }
            }
        };

        // Audit repo throws on RecordAuditEventAsync
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Audit store unreachable"));

        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            auditRepo,
            new CrossSourceQueryRouter(tableRepo, options.WebSql.CrossSource, options.WebSql),
            new CrossSourcePlanner(FastSqlEngine.Default),
            Substitute.For<IFederatedStagingService>(),
            Substitute.For<IDuckDbOlapEngine>());

        var query = "SELECT o.id, s.carrier FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.tracking_id = s.id";
        var request = new GovernedSqlQueryRequest(query, Transport: "WebSql");

        // Act & Assert (INV-17: fail-closed audit)
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task Cancellation_AbortsFederatedExecution()
    {
        var ordersMeta = CreateOrdersTable();
        var shipmentsMeta = CreateShipmentsTable(completeResponse: true);

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(ordersMeta.Identifier, Arg.Any<CancellationToken>()).Returns(ordersMeta);
        tableRepo.GetTableMetadataAsync(shipmentsMeta.Identifier, Arg.Any<CancellationToken>()).Returns(shipmentsMeta);

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = ["crm", "logistics"],
                CrossSource = new CrossSourceOptions { Enabled = true }
            }
        };

        var stagingService = Substitute.For<IFederatedStagingService>();
        stagingService.StageAsync(Arg.Any<IReadOnlyList<StagingTableRequest>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<FederationBudget>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var token = callInfo.Arg<CancellationToken>();
                await Task.Delay(5000, token);
                return (IReadOnlyList<OlapTableSource>)Array.Empty<OlapTableSource>();
            });

        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            Substitute.For<IAuditLogRepository>(),
            new CrossSourceQueryRouter(tableRepo, options.WebSql.CrossSource, options.WebSql),
            new CrossSourcePlanner(FastSqlEngine.Default),
            stagingService,
            Substitute.For<IDuckDbOlapEngine>());

        var query = "SELECT o.id, s.carrier FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.tracking_id = s.id";
        var request = new GovernedSqlQueryRequest(query, Transport: "WebSql");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert (INV-13)
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant), cts.Token));
    }
}
