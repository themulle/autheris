namespace Autheris.Tests.Unit.Connectors;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedConnectorReaderStreamingTests
{
    private static TableMetadata CreateSampleMetadata()
    {
        var id = new TableIdentifier("sales", "dbo", "Orders");
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "sales-db",
                SchemaName = "dbo",
                TableName = "Orders",
                DisplayName = "Orders",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "Id", DataType = "int" },
                new TableColumn { ColumnName = "Customer", DataType = "varchar" },
                new TableColumn { ColumnName = "Amount", DataType = "decimal" }
            ]
        };
    }

    private static IAutherisConnector CreateMockConnector(int rowCount)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(rowCount);
        for (int i = 1; i <= rowCount; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["Id"] = i,
                ["Customer"] = "Customer " + i,
                ["Amount"] = 100m * i
            });
        }

        var recordSource = Substitute.For<IConnectorRecordSource>();
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rows));

        var splitManager = Substitute.For<IConnectorSplitManager>();
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConnectorSplit>>([ConnectorSplit.Default()]));

        var connector = Substitute.For<IAutherisConnector>();
        connector.RecordSource.Returns(recordSource);
        connector.SplitManager.Returns(splitManager);
        return connector;
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> GenerateRowsAsync(
        int count,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new Dictionary<string, object?>
            {
                ["Id"] = i,
                ["Customer"] = "Customer " + i,
                ["Amount"] = 100m * i
            };
        }
    }

    [Fact]
    public async Task StreamRawAsync_EnforcesMaxRows_OnTheFly()
    {
        var metadata = CreateSampleMetadata();
        var connector = CreateMockConnector(10);
        var session = new ConnectorSessionContext(
            new ClaimsPrincipal(),
            new TenantId("t1"),
            TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            ["Id", "Customer"],
            new Dictionary<string, object?>());

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var ex = await Should.ThrowAsync<ConnectorRowLimitExceededException>(async () =>
        {
            await foreach (var row in GovernedConnectorReader.StreamRawAsync(connector, session, metadata, maxRows: 5))
            {
                rows.Add(row);
            }
        });

        ex.MaxRows.ShouldBe(5);
        rows.Count.ShouldBe(5);
    }

    [Fact]
    public async Task StreamAsync_ThrowsResponseTooLarge_WhenMaxBytesExceeded()
    {
        var metadata = CreateSampleMetadata();
        var connector = CreateMockConnector(100);
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var session = new ConnectorSessionContext(
            new ClaimsPrincipal(),
            new TenantId("t1"),
            decision,
            ["Id", "Customer", "Amount"],
            new Dictionary<string, object?>());

        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        // Set a small maxBytes limit, e.g. 100 bytes (each row has ~30-50 bytes)
        var policy = new GovernedRowPolicy(maskingProvider, null, MaxBytes: 100);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
        {
            await foreach (var row in GovernedConnectorReader.StreamAsync(connector, session, metadata, decision, policy))
            {
                // Consuming stream
            }
        });

        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
    }

    [Fact]
    public async Task StreamAsync_YieldsRowsWithoutBuffering_WhenWithinLimits()
    {
        var metadata = CreateSampleMetadata();
        var connector = CreateMockConnector(5);
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var session = new ConnectorSessionContext(
            new ClaimsPrincipal(),
            new TenantId("t1"),
            decision,
            ["Id", "Customer", "Amount"],
            new Dictionary<string, object?>());

        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        var policy = new GovernedRowPolicy(maskingProvider, null, MaxBytes: 10000);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in GovernedConnectorReader.StreamAsync(connector, session, metadata, decision, policy))
        {
            rows.Add(row);
        }

        rows.Count.ShouldBe(5);
        rows[0]["Customer"].ShouldBe("Customer 1");
        rows[4]["Customer"].ShouldBe("Customer 5");
    }
}
