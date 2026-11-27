using System.Security.Claims;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

public sealed class AggregationAccessFactsTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private sealed class Rows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default) =>
            Task.FromResult(rows);
    }

    private static async Task<AggregationResult> RunAsync(string? rowFilterSql)
    {
        var tableId = Guid.NewGuid();
        var metadata = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { Id = tableId, SourceType = "PostgreSQL", SourceName = "default", SchemaName = "public", TableName = "orders", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "region", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "amount", DataType = "decimal" }
            ]
        };
        var metaRepo = new Autheris.Tests.Common.Fakes.InMemoryTableMetadataRepository(metadata);
        var cols = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["region"] = ColumnAccessLevel.Clear,
            ["amount"] = ColumnAccessLevel.Clear
        };
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["region"] = "EU", ["amount"] = 1m },
            new Dictionary<string, object?> { ["region"] = "EU", ["amount"] = 2m }
        ];
        var service = new GovernedAggregationService(
            metadataRepository: metaRepo,
            pdp: TestSupport.AccessDecisionPointStubs.Returning(_ => TableAccessDecision.Allowed(Orders, cols, rowFilterSql: rowFilterSql)),
            dataSourceExecutors: [new Rows(rows)],
            analyticsOptions: Options.Create(new AnalyticsOptions { MinimumGroupSize = 1 }));
        var principal = TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-A"),
            new Claim(ClaimTypes.Name, "a"),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth")));
        return await service.ExecuteAggregationAsync(principal, new AggregationQuery(
            Table: Orders,
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, null, "n")]));
    }

    [Fact]
    public async Task Execute_SetsAccessFactsFromDecision()
    {
        var result = await RunAsync("region = 'EU'");

        result.AccessFacts.ShouldNotBeNull();
        result.AccessFacts!.Table.ShouldBe(Orders);
        result.AccessFacts.Path.ShouldBeEmpty();
        result.AccessFacts.RowFilterApplied.ShouldBeTrue();
        result.AccessFacts.RowFilterDescriptions.ShouldBe(["Row filter applies (defined by data owner)."]);
    }

    [Fact]
    public async Task Execute_NoRowFilter_FactsEmpty()
    {
        var result = await RunAsync(null);

        result.AccessFacts.ShouldNotBeNull();
        result.AccessFacts!.RowFilterApplied.ShouldBeFalse();
        result.AccessFacts.RowFilterDescriptions.ShouldBeEmpty();
    }
}
