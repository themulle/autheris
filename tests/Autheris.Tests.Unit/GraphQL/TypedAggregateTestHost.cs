using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Executor over the real catalog type module with substituted governed services (#112 resolver tests).</summary>
internal sealed class TypedAggregateTestHost
{
    public IGovernedAggregationService Aggregation { get; } = Substitute.For<IGovernedAggregationService>();

    public ISemanticMetricQueryService Metrics { get; } = Substitute.For<ISemanticMetricQueryService>();

    public IGovernedTreeQueryService Tree { get; } = Substitute.For<IGovernedTreeQueryService>();

    public AnalyticsOptions Analytics { get; } = new();

    public bool AggregationEnabled { get; set; } = true;

    public ClaimsPrincipal User { get; set; } =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant_id", "T1")], "TestAuth"));

    public IReadOnlyList<TableMetadata> Tables { get; set; } = [DefaultTable()];

    public static TableMetadata DefaultTable(string name = "orders", params (string Name, string Type)[] columns) => new()
    {
        Identifier = new TableIdentifier("sales", "dbo", name),
        Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = (columns.Length == 0
                ? [("id", "int"), ("region", "varchar(50)"), ("amount", "decimal(18,2)"), ("qty", "int"), ("is_vip", "bit"),
                   ("created", "datetime"), ("big", "bigint"), ("ratio", "float"), ("null", "varchar(10)")]
                : columns)
            .Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList(),
        PrimaryKeyColumns = ["id"]
    };

    public async Task<IRequestExecutor> BuildAsync()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Tables.ToList());
        var relationRepo = Substitute.For<ITableRelationRepository>();
        relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);

        var services = new ServiceCollection();
        services.AddSingleton(metadataRepo);
        services.AddSingleton(relationRepo);
        services.AddSingleton(Tree);
        services.AddSingleton(Aggregation);
        services.AddSingleton(Metrics);
        // A substitute, not HttpContextAccessor: its AsyncLocal storage would not flow out of this async method.
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = User });
        services.AddSingleton(accessor);
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddSingleton(Options.Create(Analytics));

        return await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                metadataRepo,
                relationRepo,
                null,
                null,
                null,
                AggregationEnabled))
            .BuildRequestExecutorAsync();
    }

    public static JsonDocument Json(IExecutionResult result) => JsonDocument.Parse(result.ToJson());

    public static IReadOnlyList<(string? Code, string Message)> Errors(IExecutionResult result)
    {
        using var doc = Json(result);
        var list = new List<(string?, string)>();
        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            foreach (var e in errors.EnumerateArray())
            {
                string? code = e.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var c) ? c.GetString() : null;
                list.Add((code, e.GetProperty("message").GetString() ?? string.Empty));
            }
        }

        return list;
    }

    public static AggregationResult Result(
        IReadOnlyList<AggregationGroupRow> groups,
        long total = 0,
        AggregationMetadata? meta = null) =>
        new(new TableIdentifier("sales", "dbo", "orders"), groups,
            new AggregationGroupRow(new Dictionary<string, object?>(), total, new Dictionary<string, object?>()),
            meta ?? new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false));
}
