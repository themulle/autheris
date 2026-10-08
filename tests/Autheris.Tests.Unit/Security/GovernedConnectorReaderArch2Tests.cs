namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Architecture 2 / SQL2-5: every connector read goes through one governed pipeline (row filter when the connector did
/// not push it down, masking exactly once, capacity limit). Before, only the REST/GraphQL path filtered after a connector
/// that ignored the row filter; OLAP staged every row the connector returned.
/// </summary>
public sealed class GovernedConnectorReaderArch2Tests
{
    private static readonly TableIdentifier Table = new("sales", "public", "orders");

    private static readonly TableMetadata Meta = new()
    {
        Identifier = Table,
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = true }
        ]
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows() =>
    [
        new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU", ["email"] = "a@x.eu" },
        new Dictionary<string, object?> { ["id"] = 2, ["region"] = "US", ["email"] = "b@x.us" }
    ];

    private static IAutherisConnector ConnectorWithoutPushdown(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var connector = Substitute.For<IAutherisConnector>();
        var splitManager = Substitute.For<IConnectorSplitManager>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.SplitManager.Returns(splitManager);
        connector.RecordSource.Returns(recordSource);
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([new ConnectorSplit("s1", new Dictionary<string, object?>())]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(rows);
        return connector;
    }

    private static TableAccessDecision EuOnly() =>
        TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), "[region] = 'EU'", hasUnconstrainedColumnAllow: true);

    [Fact]
    public async Task Olap_ConnectorWithoutRowFilterPushdown_StagesOnlyPermittedRows()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"), new Claim("tenant_id", "tenant-a")], "Bearer"));
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(Substitute.For<IAuditLogRepository>())
            .BuildServiceProvider();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            sql = "SELECT * FROM orders",
            tableNames = new[] { "sales.public.orders" }
        })));

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Meta);

        var connector = ConnectorWithoutPushdown(Rows());
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(Table, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var accessResolver = Substitute.For<ICrossDomainAccessResolver>();
        accessResolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(EuOnly());

        var rebac = Substitute.For<IRebacEvaluator>();
        rebac.IsEnabled.Returns(false);

        OlapQueryRequest? staged = null;
        var engine = Substitute.For<IDuckDbOlapEngine>();
        engine.ExecuteOlapQueryAsync(Arg.Do<OlapQueryRequest>(r => staged = r), Arg.Any<CancellationToken>())
            .Returns(new OlapQueryResult([], [], 0, TimeSpan.Zero));

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            httpContext,
            engine,
            metadataRepo,
            registry,
            accessResolver,
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true, MaxStagedRowsPerTable = 100 } }),
            NullLoggerFactory.Instance);

        staged.ShouldNotBeNull();
        var rows = staged.Sources.Single().GovernedRows;
        rows.Count.ShouldBe(1);
        rows[0]["region"].ShouldBe("EU");
    }

    [Fact]
    public async Task Reader_ConnectorWithoutPushdown_FiltersAndMasksOnce()
    {
        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");

        var result = await GovernedConnectorReader.ReadAsync(
            ConnectorWithoutPushdown(Rows()),
            Session(EuOnly()),
            Meta,
            new GovernedRowPolicy(masking, HmacKeyId: null),
            CancellationToken.None);

        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["region"].ShouldBe("EU");
        result.Rows[0]["email"].ShouldBe("***");
        masking.ReceivedCalls().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Reader_ConnectorThatPushedDownAndMasked_IsNotFilteredOrMaskedAgain()
    {
        var masking = Substitute.For<IColumnMaskingProvider>();
        var connector = ConnectorWithoutPushdown(Rows());
        connector.RecordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var session = ci.ArgAt<ConnectorSessionContext>(1);
                session.Items["RlsPushdownExecuted"] = true;
                session.Items["InDbColumnMaskingExecuted"] = true;
                return Rows();
            });

        var result = await GovernedConnectorReader.ReadAsync(connector, Session(EuOnly()), Meta, new GovernedRowPolicy(masking, null), CancellationToken.None);

        result.Rows.Count.ShouldBe(2);
        masking.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Reader_MaxRows_IsCheckedBeforeRowsAreKept()
    {
        var policy = new GovernedRowPolicy(Substitute.For<IColumnMaskingProvider>(), null, MaxRows: 1);

        await Should.ThrowAsync<ConnectorRowLimitExceededException>(() =>
            GovernedConnectorReader.ReadAsync(ConnectorWithoutPushdown(Rows()), Session(TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)), Meta, policy, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_DeniedColumns_AreStripped()
    {
        var decision = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear });

        var result = await GovernedConnectorReader.ReadAsync(
            ConnectorWithoutPushdown(Rows()), Session(decision), Meta, new GovernedRowPolicy(Substitute.For<IColumnMaskingProvider>(), null), CancellationToken.None);

        result.Rows.ShouldAllBe(r => r.Keys.SequenceEqual(new[] { "id" }));
    }

    private static ConnectorSessionContext Session(TableAccessDecision decision) => new(
        Principal: new ClaimsPrincipal(new ClaimsIdentity()),
        Tenant: new TenantId("tenant-a"),
        AccessDecision: decision,
        ProjectedColumns: Meta.Columns.Select(c => c.ColumnName).ToList(),
        Arguments: new Dictionary<string, object?>(),
        PushdownFilterSql: decision.CombinedRowFilterSql,
        Limit: 100,
        Offset: 0);
}
