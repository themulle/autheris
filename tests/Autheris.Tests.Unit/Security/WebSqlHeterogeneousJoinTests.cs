namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Tests.Common.Fakes;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Infrastructure.Connectors;
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

[Trait("Category", "Security")]
public sealed class WebSqlHeterogeneousJoinTests
{
    private const string Tenant = "tenant_test";

    private static ClaimsPrincipal CreateUser() =>
        Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim(ClaimTypes.Name, "alice"),
                new Claim("tenant_id", Tenant)
            ],
            "Test")));

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

    private static readonly ICrossSourceQueryRouter PassiveRouter = Substitute.For<ICrossSourceQueryRouter>();
    private static readonly ICrossSourcePlanner PassivePlanner = Substitute.For<ICrossSourcePlanner>();
    private static readonly IFederatedStagingService PassiveStagingService = Substitute.For<IFederatedStagingService>();
    private static readonly IDuckDbOlapEngine PassiveDuckDbEngine = Substitute.For<IDuckDbOlapEngine>();

    private static FederatedDuckDbExecutionService CreateTransportGateService(string[] allowedTransports)
    {
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
                    AllowedTransports = [.. allowedTransports]
                }
            }
        };

        return new FederatedDuckDbExecutionService(
            Options.Create(options),
            new InMemoryAuditLogRepository(),
            PassiveRouter,
            PassivePlanner,
            PassiveStagingService,
            PassiveDuckDbEngine);
    }

    [Fact]
    public async Task Join_SqlOrders_And_HttpShipments_ExecutesEndToEnd_AndReturnsJoinedRows()
    {
        var ordersMeta = CreateOrdersTable();
        var shipmentsMeta = CreateShipmentsTable(completeResponse: true);

        var ordersRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 101, ["tracking_id"] = "TRK-1", ["amount"] = 99.50m },
            new Dictionary<string, object?> { ["id"] = 102, ["tracking_id"] = "TRK-2", ["amount"] = 150.00m }
        };
        var shipmentsRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "TRK-1", ["carrier"] = "DHL", ["status"] = "DELIVERED" },
            new Dictionary<string, object?> { ["id"] = "TRK-2", ["carrier"] = "UPS", ["status"] = "IN_TRANSIT" }
        };

        var connectorRegistry = new InMemoryConnectorRegistry();
        connectorRegistry.RegisterConnector("crm", new StubAutherisConnector("crm", ordersRows));
        connectorRegistry.RegisterConnector("logistics", new StubAutherisConnector("logistics", shipmentsRows));

        var tableRepo = new InMemoryTableMetadataRepository(ordersMeta, shipmentsMeta);

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

        var auditRepo = new InMemoryAuditLogRepository();
        var maskingProvider = StubColumnMaskingProvider.Null;

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

        var governedSqlService = global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options),
            auditRepo,
            GovernedTestSupport.Guard,
            tableRepository: tableRepo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(tableRepo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
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
        auditRepo.Entries.Count(e => e.EventType == AuditEventTypes.WebSqlCrossSourceQuery && e.Decision == "ALLOW").ShouldBe(1);

        // 2. Source read audits (WEBSQL_CROSS_SOURCE_SOURCE_READ) for both tables
        auditRepo.Entries.Count(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable == ordersMeta.Identifier.ToString()).ShouldBe(1);
        auditRepo.Entries.Count(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.TargetTable == shipmentsMeta.Identifier.ToString()).ShouldBe(1);
    }

    [Fact]
    public async Task Join_HttpSourceWithoutCompleteResponse_IsRejected()
    {
        var shipmentsMeta = CreateShipmentsTable(completeResponse: false); // Incomplete response!

        var connectorRegistry = new InMemoryConnectorRegistry();
        var tableRepo = new InMemoryTableMetadataRepository(shipmentsMeta);

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

        var auditRepo = new InMemoryAuditLogRepository();
        var stagingService = new FederatedStagingService(
            connectorRegistry,
            StubColumnMaskingProvider.Null,
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
            PassiveDuckDbEngine,
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
        var federatedService = CreateTransportGateService(["WebSql", "Trino"]);

        var request = new GovernedSqlQueryRequest("SELECT 1", Transport: "ArrowExport");

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("Cross-catalog");
    }

    [Fact]
    public async Task Join_TransportGate_MissingTransport_IsRejected()
    {
        var federatedService = CreateTransportGateService(["WebSql", "Trino"]);

        var request = new GovernedSqlQueryRequest("SELECT 1", Transport: null);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("Cross-catalog");
    }

    [Fact]
    public async Task Join_TransportGate_Rest_NotOptedIn_IsRejected()
    {
        var federatedService = CreateTransportGateService(["WebSql", "Trino"]);

        var request = new GovernedSqlQueryRequest("SELECT 1", Transport: "Rest");

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

        var ordersConnector = new StubAutherisConnector(
            "crm",
            [new Dictionary<string, object?> { ["id"] = 1 }]);

        // Second source fails with an exception (e.g. HTTP 500 / network failure)
        var shipmentsConnector = new StubAutherisConnector(
            "logistics",
            readBatch: (_, _, _) => Task.FromException<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                new InvalidOperationException("Upstream HTTP service returned 500 Internal Server Error")));

        var connectorRegistry = new InMemoryConnectorRegistry();
        connectorRegistry.RegisterConnector("crm", ordersConnector);
        connectorRegistry.RegisterConnector("logistics", shipmentsConnector);

        var tableRepo = new InMemoryTableMetadataRepository(ordersMeta, shipmentsMeta);

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

        var auditRepo = new InMemoryAuditLogRepository();
        var stagingService = new FederatedStagingService(
            connectorRegistry,
            StubColumnMaskingProvider.Null,
            auditRepo,
            Options.Create(options),
            NullLogger<FederatedStagingService>.Instance);

        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            auditRepo,
            new CrossSourceQueryRouter(tableRepo, options.WebSql.CrossSource, options.WebSql),
            new CrossSourcePlanner(FastSqlEngine.Default),
            stagingService,
            PassiveDuckDbEngine);

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

        var tableRepo = new InMemoryTableMetadataRepository(ordersMeta, shipmentsMeta);

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
            PassiveStagingService,
            PassiveDuckDbEngine);

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

        var tableRepo = new InMemoryTableMetadataRepository(ordersMeta, shipmentsMeta);

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
                await Autheris.Tests.Common.Hang.UntilCancelledAsync(token);
                return (IReadOnlyList<OlapTableSource>)Array.Empty<OlapTableSource>();
            });

        var federatedService = new FederatedDuckDbExecutionService(
            Options.Create(options),
            new InMemoryAuditLogRepository(),
            new CrossSourceQueryRouter(tableRepo, options.WebSql.CrossSource, options.WebSql),
            new CrossSourcePlanner(FastSqlEngine.Default),
            stagingService,
            PassiveDuckDbEngine);

        var query = "SELECT o.id, s.carrier FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.tracking_id = s.id";
        var request = new GovernedSqlQueryRequest(query, Transport: "WebSql");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert (INV-13)
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await federatedService.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant), cts.Token).WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
