using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Connectors;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Connectors;

public sealed class SqlConnectorStreamingTests
{
    private static TableMetadata CreateSampleMetadata(
        string domain = "finance",
        string schema = "dbo",
        string table = "Invoices",
        string sourceName = "test-sql",
        string sourceType = "PostgreSQL")
    {
        var identifier = new TableIdentifier(domain, schema, table);
        return new TableMetadata
        {
            Identifier = identifier,
            Table = new Table
            {
                SourceName = sourceName,
                SchemaName = schema,
                TableName = table,
                DisplayName = "Customer Invoices",
                DataSourceType = DataSourceType.Sql,
                SourceType = sourceType
            },
            Columns =
            [
                new TableColumn { ColumnName = "Id", DataType = "int" },
                new TableColumn { ColumnName = "Amount", DataType = "decimal" },
                new TableColumn { ColumnName = "Iban", DataType = "varchar", IsSensitive = true }
            ]
        };
    }

    private static SqlConnector CreateSqlConnector(
        string connectorId,
        ITableMetadataRepository metadataRepository,
        IGovernedSqlExecutionService governedService)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDataSourceExecutor>(_ => new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => governedService));
        return new SqlConnector(
            connectorId,
            metadataRepository,
            Substitute.For<IColumnMaskingProvider>(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private static ClaimsPrincipal CreatePrincipal(string sid = "S-1-5-21-STREAM", string role = "Analyst")
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, sid));
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task ReadSplitAsync_StreamsRowsViaStreamRowsAsync_WithoutBuffering()
    {
        var meta = CreateSampleMetadata();
        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetByIdentifierAsync(meta.Identifier, Arg.Any<CancellationToken>())
            .Returns(meta);

        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.StreamRowsAsync(
                Arg.Any<GovernedSqlQueryRequest>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TenantId>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => YieldSampleRowsAsync(3, onRowProduced: null, callInfo.Arg<CancellationToken>()));

        var connector = CreateSqlConnector("test-sql", metadataRepo, governed);
        var session = new ConnectorSessionContext(
            Principal: CreatePrincipal(),
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id", "Amount"],
            Arguments: new Dictionary<string, object?>());
        session.Items["TableMetadata"] = meta;

        var received = new List<IDictionary<string, object?>>();
        await foreach (var row in connector.RecordSource.ReadSplitAsync(ConnectorSplit.Default(), session))
        {
            received.Add(row);
        }

        received.Count.ShouldBe(3);
        received[0]["Id"].ShouldBe(1);
        received[2]["Id"].ShouldBe(3);

        await governed.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
        governed.Received(1).StreamRowsAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.Sql.Contains("FROM finance.dbo.Invoices", StringComparison.Ordinal)),
            Arg.Any<ClaimsPrincipal>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadSplitAsync_WhenConsumerBreaksEarly_DoesNotPullRemainingRows()
    {
        var meta = CreateSampleMetadata();
        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetByIdentifierAsync(meta.Identifier, Arg.Any<CancellationToken>())
            .Returns(meta);

        var producedCount = 0;
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.StreamRowsAsync(
                Arg.Any<GovernedSqlQueryRequest>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TenantId>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => YieldSampleRowsAsync(100, () => producedCount++, callInfo.Arg<CancellationToken>()));

        var connector = CreateSqlConnector("test-sql", metadataRepo, governed);
        var session = new ConnectorSessionContext(
            Principal: CreatePrincipal(),
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id"],
            Arguments: new Dictionary<string, object?>());
        session.Items["TableMetadata"] = meta;

        var consumedCount = 0;
        await foreach (var _ in connector.RecordSource.ReadSplitAsync(ConnectorSplit.Default(), session))
        {
            consumedCount++;
            if (consumedCount == 2)
            {
                break;
            }
        }

        consumedCount.ShouldBe(2);
        producedCount.ShouldBe(2);
    }

    [Fact]
    public async Task ReadSplitAsync_WithSensitiveFilterViolation_FailsClosedBeforeStreaming()
    {
        var meta = CreateSampleMetadata();
        var decision = TableAccessDecision.Allowed(
            meta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = ColumnAccessLevel.Plain,
                ["Iban"] = ColumnAccessLevel.Masked
            });

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        var connector = CreateSqlConnector("test-sql", metadataRepo, governed);

        var session = new ConnectorSessionContext(
            Principal: CreatePrincipal(),
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id"],
            Arguments: new Dictionary<string, object?>
            {
                ["Iban"] = "DE89370400440532013000"
            });
        session.Items["TableMetadata"] = meta;

        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await foreach (var _ in connector.RecordSource.ReadSplitAsync(ConnectorSplit.Default(), session))
            {
            }
        });

        governed.DidNotReceiveWithAnyArgs().StreamRowsAsync(default!, default!, default, default);
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> YieldSampleRowsAsync(
        int count,
        Action? onRowProduced,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onRowProduced?.Invoke();
            await Task.Yield();
            yield return new Dictionary<string, object?>
            {
                ["Id"] = i,
                ["Amount"] = i * 100m
            };
        }
    }
}
