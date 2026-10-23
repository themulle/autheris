namespace Autheris.Tests.Unit.Olap;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-07: table authorization, staging audit and engine call of the OLAP endpoint live in the use case.</summary>
public sealed class OlapStagingServiceTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableMetadata Meta = new() { Identifier = Orders, Columns = [new TableColumn { ColumnName = "id", DataType = "int" }] };

    private static SecurityPrincipalContext Caller() => new()
    {
        UserSid = new Sid("S-1-5-21-USER"),
        TenantId = new TenantId("tenant-1"),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer"
    };

    private sealed record Fixture(
        OlapStagingService Sut,
        IAuditWriter Audit,
        IDuckDbOlapEngine Engine,
        ICrossDomainAccessResolver Resolver,
        IAutherisConnectorRegistry Registry);

    private static Fixture Create(TableAccessDecision decision, Microsoft.Extensions.Logging.ILogger<OlapStagingService>? logger = null, TableMetadata? metadata = null)
    {
        var meta = Substitute.For<ITableMetadataRepository>();
        meta.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(metadata ?? Meta);
        var resolver = Substitute.For<ICrossDomainAccessResolver>();
        resolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(decision);
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        var engine = Substitute.For<IDuckDbOlapEngine>();
        engine.ExecuteOlapQueryAsync(Arg.Any<OlapQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OlapQueryResult(["x"], [new List<object?> { 1 }], 1, TimeSpan.FromMilliseconds(1)));
        var audit = Substitute.For<IAuditWriter>();
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowedDataSources = ["sales"] },
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true }
        });
        var sut = new OlapStagingService(
            engine, meta, registry, resolver, Substitute.For<IColumnMaskingProvider>(), options, audit, logger ?? NullLogger<OlapStagingService>.Instance,
            new Autheris.Application.Federation.FederationGate(options), TrinoSqlEngine.FastSqlEngine.Default, new OlapRequestLimiter(options));
        return new Fixture(sut, audit, engine, resolver, registry);
    }

    private static OlapQueryCommand Command(params string[] tables) => new("SELECT 1", tables, null);

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("user_sid", "S-1-5-21-USER")], "Bearer"));

    [Fact]
    public async Task CallerWithoutTableAccess_IsDenied_AndNoStagingAuditIsWritten()
    {
        var f = Create(TableAccessDecision.Denied(Orders, "no grant"));

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command("sales.public.orders"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        outcome.Error.ShouldBe("Access denied to table 'sales.public.orders'.");
        outcome.Result.ShouldBeNull();
        f.Registry.DidNotReceiveWithAnyArgs().TryGetConnectorForTable(default!, out _);
        await f.Audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
        await f.Engine.DidNotReceiveWithAnyArgs().ExecuteOlapQueryAsync(default!, default);
    }

    [Fact]
    public async Task AuditStoreDown_Returns503_NoRowsReturned()
    {
        var f = Create(TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        f.Audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new AuditUnavailableException("down"));

        // No staged table: the OLAP_QUERY audit is the first required write and runs after the engine call.
        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command(), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        outcome.Error.ShouldBe("Audit unavailable; the analytical request was rejected.");
        outcome.Result.ShouldBeNull();
    }

    [Fact]
    public async Task MissingTenant_IsForbidden()
    {
        var f = Create(TableAccessDecision.Denied(Orders));
        var caller = Caller() with { TenantId = default };

        var outcome = await f.Sut.ExecuteAsync(User(), caller, Command(), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        outcome.Error.ShouldBe("Forbidden: Missing tenant identity.");
    }

    [Fact]
    public async Task Success_AuditsQueryWithTraceId_BeforeReturningTheResult()
    {
        var f = Create(TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command(), isDevelopment: false, "trace-9", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status200OK);
        outcome.Result!.TotalRowCount.ShouldBe(1);
        await f.Audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == "OLAP_QUERY" && e.TraceId == "trace-9" && e.Decision == "ALLOW"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StagedTableAuditFails_Returns503_AndTheEngineIsNotCalled()
    {
        var f = Create(TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var connector = Substitute.For<IAutherisConnector>();
        var splitManager = Substitute.For<IConnectorSplitManager>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.SplitManager.Returns(splitManager);
        connector.RecordSource.Returns(recordSource);
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([new ConnectorSplit("s1", new Dictionary<string, object?>())]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([(IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = 1 }]);
        f.Registry.TryGetConnectorForTable(Orders, out Arg.Any<IAutherisConnector>()!).Returns(x => { x[1] = connector; return true; });
        f.Audit.RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == "OLAP_TABLE_STAGED"), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new AuditUnavailableException("down"));

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command("sales.public.orders"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        outcome.Result.ShouldBeNull();
        await f.Engine.DidNotReceiveWithAnyArgs().ExecuteOlapQueryAsync(default!, default);
    }

    [Fact]
    public async Task UnexpectedFailureQuotingAValue_IsNotLoggedAboveDebug_WithTheTypeAndCode()
    {
        // SEC-FED-DELTA-01: exception text can quote staged data values.
        const string sentinel = "SENTINEL-customer-4711-secret-value";
        var logs = new List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)>();
        var f = Create(TableAccessDecision.Denied(Orders, "x"), new Autheris.Tests.Unit.VirtualFilters.Federation.ListLogger<OlapStagingService>(logs));
        f.Resolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<TableAccessDecision>(_ => throw new InvalidOperationException($"ORA-00001: duplicate value '{sentinel}'"));

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command("sales.public.orders"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBeGreaterThanOrEqualTo(400);
        logs.Where(l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Information).ShouldAllBe(l => !l.Message.Contains(sentinel));
        logs.ShouldContain(l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains(nameof(InvalidOperationException)));
    }

    private static TableMetadata RegionRequired() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "sales", SchemaName = "public", TableName = "orders", IsActive = true, RequiredFilterColumns = ["region"] },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "region", DataType = "varchar" }]
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task Olap_RequiredFilterColumn_NotInRls_Returns400RequiredFilterMissing()
    {
        var decision = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var f = Create(decision, metadata: RegionRequired());

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command("sales.public.orders"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        outcome.ErrorCode.ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
        outcome.Result.ShouldBeNull();
        await f.Engine.DidNotReceiveWithAnyArgs().ExecuteOlapQueryAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Olap_CrossSource_RequiredFilterColumn_NotInRls_Returns400()
    {
        var decision = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var f = Create(decision, metadata: RegionRequired());

        var outcome = await f.Sut.ExecuteAsync(
            User(), Caller(), Command("sales.public.orders", "sales.public.orders_b"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        outcome.ErrorCode.ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Olap_RequiredFilterColumn_LongerIdentifierInRowFilter_DoesNotSatisfy()
    {
        var decision = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: "region_code = 'EU'", hasUnconstrainedColumnAllow: true);
        var f = Create(decision, metadata: RegionRequired());

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command("sales.public.orders"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.ErrorCode.ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Olap_RequiredFilterColumn_SatisfiedByRowFilter_PassesTheRule()
    {
        var decision = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: "region = 'EU'", hasUnconstrainedColumnAllow: true);
        var f = Create(decision, metadata: RegionRequired());

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command("sales.public.orders"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.ErrorCode.ShouldNotBe("REQUIRED_FILTER_COLUMN_MISSING");
    }
}
