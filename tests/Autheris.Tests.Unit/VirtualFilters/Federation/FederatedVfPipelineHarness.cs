namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>
/// Shared building blocks for the federated Virtual Filter pipeline tests (F-GOV-14): catalog tables, a counting
/// connector stub per table and the real <see cref="FederatedStagingService"/>.
/// </summary>
internal static class FederatedVfPipelineHarness
{
    internal const string TenantValue = "tenant_vf";

    internal static readonly TenantId Tenant = new(TenantValue);

    internal static ClaimsPrincipal User(string sid = "S-1-5-21-VF-ALICE") =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim(ClaimTypes.Name, "alice"),
                new Claim("tenant_id", TenantValue)
            ],
            "Test"));

    internal static TableMetadata HttpTable(string domain, string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(domain, schema, table),
        Table = new Table
        {
            TableName = table,
            SchemaName = schema,
            SourceType = "Http",
            DataSourceType = DataSourceType.HttpDeclarative,
            SourceName = domain,
            IsActive = true,
            HttpEndpoint = new HttpEndpointDescriptor
            {
                BaseUrl = "https://api.example.internal",
                PathTemplate = "/" + table,
                CompleteResponse = true
            }
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
    };

    internal static TableMetadata SqlTable(string domain, string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(domain, schema, table),
        Table = new Table
        {
            TableName = table,
            SchemaName = schema,
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            SourceName = domain,
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
    };

    internal static TableAccessDecision Unrestricted(TableMetadata table, string? rowFilterSql = null) =>
        TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql, hasUnconstrainedColumnAllow: true);

    internal static FederationBudget Budget() => new(
        MaxTableCount: 5,
        MaxStagedRowsPerTable: 1_000,
        MaxTotalStagedRows: 10_000,
        MaxStagedBytesPerTable: 1_000_000,
        MaxTotalStagedBytes: 10_000_000,
        TimeoutSeconds: 30,
        MaxParallelSourceReads: 1);

    internal static FederatedStagingService Staging(IAutherisConnectorRegistry registry, GatewayOptions? options = null) =>
        new(registry,
            Substitute.For<IColumnMaskingProvider>(),
            Substitute.For<IAuditLogRepository>(),
            Options.Create(options ?? new GatewayOptions()),
            NullLogger<FederatedStagingService>.Instance);

    internal static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] values) =>
        values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Connector stub for one table: returns fixed rows, counts the upstream reads, records the session it saw and can run
/// a callback inside <c>ReadBatchAsync</c> (to simulate a connector that tampers with <c>session.Items</c>).
/// </summary>
internal sealed class StubTableConnector
{
    private readonly List<IReadOnlyDictionary<string, object?>> _rows;

    public StubTableConnector(string connectorId, IEnumerable<IReadOnlyDictionary<string, object?>> rows, Action<ConnectorSessionContext>? onRead = null)
    {
        _rows = rows.ToList();
        Connector = Substitute.For<IAutherisConnector>();
        Connector.ConnectorId.Returns(connectorId);
        var recordSource = Substitute.For<IConnectorRecordSource>();
        Connector.RecordSource.Returns(recordSource);
        Connector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split-" + connectorId)]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var session = ci.ArgAt<ConnectorSessionContext>(1);
                Interlocked.Increment(ref _calls);
                LastSession = session;
                onRead?.Invoke(session);
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(_rows);
            });
    }

    private int _calls;

    public IAutherisConnector Connector { get; }

    public int UpstreamCalls => Volatile.Read(ref _calls);

    public ConnectorSessionContext? LastSession { get; private set; }
}

/// <summary>Builds an <see cref="IAutherisConnectorRegistry"/> that maps each table identifier to its stub.</summary>
internal static class StubRegistry
{
    internal static IAutherisConnectorRegistry For(params (TableMetadata Table, StubTableConnector Stub)[] entries)
    {
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        foreach (var (table, stub) in entries)
        {
            var connector = stub.Connector;
            registry.TryGetConnectorForTable(table.Identifier, out Arg.Any<IAutherisConnector>()!)
                .Returns(x => { x[1] = connector; return true; });
        }

        return registry;
    }
}
