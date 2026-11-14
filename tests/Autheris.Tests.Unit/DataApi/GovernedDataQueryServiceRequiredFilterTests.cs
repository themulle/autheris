namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Federation.KeySet;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;
using RequiredFilterColumnMissingException = TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException;

[Trait("Category", "Security")]
public sealed class GovernedDataQueryServiceRequiredFilterTests
{
    private static readonly TableIdentifier Table = new("sales", "main", "orders");

    private sealed class FakeMetadataRepo(TableMetadata meta) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier identifier, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(meta);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([meta]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata m, CancellationToken ct = default) => Task.FromResult(m);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier t, IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> idx, IReadOnlyList<KeyColumnDirection>? dir = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FixedAllowPdp(TableAccessDecision decision) : IUnifiedPolicyDecisionPoint
    {
        public Task<TableAccessDecision> EvaluateAccessAsync(TableIdentifier t, TableMetadata m, SecurityPrincipalContext c, IReadOnlyList<string>? cols = null, CancellationToken ct = default) => Task.FromResult(decision);
    }

    private sealed class StubGovernedSqlService : IGovernedSqlExecutionService
    {
        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest req, ClaimsPrincipal user, TenantId tid, Func<System.Data.Common.DbDataReader, CancellationToken, Task> onResultAsync, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest req, ClaimsPrincipal user, TenantId tid, CancellationToken ct = default) =>
            Task.FromResult(new GovernedSqlResult("q", ["id"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1));
        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tid, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubFederatedSqlService : IFederatedQueryExecutionService
    {
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest req, ClaimsPrincipal user, TenantId tid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tid, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => rawValue;
    }

    private static GovernedDataQueryService CreateRestService()
    {
        var metadata = new TableMetadata
        {
            Identifier = Table,
            Table = new Table
            {
                SourceName = "sales",
                SchemaName = "main",
                TableName = "orders",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                RequiredFilterColumns = ["region"]
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "status", DataType = "varchar" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var decision = TableAccessDecision.Allowed(
            Table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["region"] = ColumnAccessLevel.Clear,
                ["status"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true);

        return new GovernedDataQueryService(
            new FakeMetadataRepo(metadata),
            new FixedAllowPdp(decision),
            new StubGovernedSqlService(),
            new StubFederatedSqlService(),
            new NoopMaskingProvider());
    }

    private static RequestContext CallerContext()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1"), new Claim("tenant", "default")], "t"));
        return new RequestContext(user, new TenantId("default"));
    }

    [Fact]
    public async Task RestFilter_StrictEquality_SatisfiesRequiredFilter()
    {
        var service = CreateRestService();
        var result = await service.ExecuteQueryAsync(
            new DatasetQueryRequest(Table, FilterExpression: "region='EU'", Limit: 10),
            CallerContext());
        result.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("region like '%'")]
    [InlineData("region != 'EU'")]
    [InlineData("lower(region) = 'eu'")]
    [InlineData("region = null")]
    [InlineData("region = status")]
    public async Task RestFilter_WeakOrNullOrFunctionFilter_ThrowsRequiredFilterMissing(string filter)
    {
        var service = CreateRestService();
        await Should.ThrowAsync<RequiredFilterColumnMissingException>(() =>
            service.ExecuteQueryAsync(new DatasetQueryRequest(Table, FilterExpression: filter, Limit: 10), CallerContext()));
    }

    [Fact]
    public void KeySetClauseBuilder_Build_PreservesExistingConstrainedColumns_AndNeverAddsKeysetColumn()
    {
        var existing = new TableFilterClause("\"region\" = 'EU'", new Dictionary<string, object?>(), ["region"])
        {
            ConstrainedColumns = ["region"],
            TrinoSqlFactory = _ => "\"region\" = 'EU'"
        };

        var combined = KeySetClauseBuilder.Build("id", new object[] { 1, 2 }, existing);
        combined.ConstrainedColumns.ShouldBe(["region"]);
        combined.ConstrainedColumns!.ShouldNotContain("id");

        KeySetClauseBuilder.Build("id", new object[] { 1, 2 }).ConstrainedColumns.ShouldBeNull();

        var nullExisting = new TableFilterClause("1 = 1", new Dictionary<string, object?>(), [])
        {
            TrinoSqlFactory = _ => "1 = 1"
        };
        KeySetClauseBuilder.Build("id", new object[] { 1, 2 }, nullExisting).ConstrainedColumns.ShouldBeNull();
    }

    [Fact]
    public void SqlTableReadExecutor_BuildFilter_PopulatesAndCombinesConstrainedColumns()
    {
        var metadata = new TableMetadata
        {
            Identifier = Table,
            Table = new Table { SourceName = "sales", SchemaName = "main", TableName = "orders" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "region", DataType = "varchar" }
            ]
        };
        var ctx = new DataSourceExecutionContext(
            "sales",
            metadata,
            new ClaimsPrincipal(),
            TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            new Dictionary<string, object?> { ["region"] = "EU" },
            ["id"],
            null,
            10,
            0,
            new TenantId("default"));

        var clause = SqlTableReadExecutor.BuildFilter(ctx);
        clause.ShouldNotBeNull();
        clause!.ConstrainedColumns.ShouldBe(["region"]);

        ctx.Items[TableQueryItems.Filter] = new TableFilterClause("\"id\" > 1", new Dictionary<string, object?>(), ["id"])
        {
            ConstrainedColumns = ["id"],
            TrinoSqlFactory = _ => "\"id\" > 1"
        };
        var combined = SqlTableReadExecutor.BuildFilter(ctx);
        combined.ShouldNotBeNull();
        combined!.ConstrainedColumns.ShouldBe(["region", "id"]);
    }

    [Fact]
    public void SemanticMetricQueryService_CombineMetricFiltersWithWhere_PreservesRequestConstrainedColumnsUnderAnd()
    {
        var metadata = new TableMetadata
        {
            Identifier = Table,
            Table = new Table { SourceName = "sales", SchemaName = "main", TableName = "orders" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "status", DataType = "varchar" }
            ]
        };
        var metric = new DbtMetric(
            "rev",
            "simple",
            "d",
            "rev",
            "status = 'open'",
            "{ \"measure\": \"x\" }",
            ["status"],
            ["order_total"],
            new Dictionary<string, string> { ["model"] = "orders" });
        var requestWhere = new TableFilterClause("\"region\" = 'EU'", new Dictionary<string, object?>(), ["region"])
        {
            ConstrainedColumns = ["region"],
            TrinoSqlFactory = _ => "\"region\" = 'EU'"
        };

        var combined = SemanticMetricQueryService.CombineMetricFiltersWithWhere([metric], requestWhere, metadata);
        combined.ShouldNotBeNull();
        combined!.ConstrainedColumns.ShouldNotBeNull();
        combined.ConstrainedColumns.ShouldContain("region");
        combined.ConstrainedColumns.ShouldContain("status");
    }
}
