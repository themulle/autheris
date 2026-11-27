using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Executor over the real catalog type module plus <see cref="DatasetFactsMiddleware"/> with hand-written governed-service fakes.</summary>
internal sealed class DatasetFactsTestHost
{
    public static readonly TableIdentifier Customers = new("sales", "dbo", "customers");
    public static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    public const string SecretEmail = "alice-secret@example.test";
    public const string SecretColumn = "email";
    public const string FixedDescription = "Row filter applies (defined by data owner).";

    public FakeTree Tree { get; } = new();

    public FakeVisibility Visibility { get; } = new();

    public Func<AggregationQuery, AggregationResult>? Aggregate { get; set; }

    public bool UseMiddleware { get; set; } = true;

    /// <summary>An error added to the result after execution (path set or null), to exercise the middleware error handling.</summary>
    public IError? InjectedError { get; set; }

    public bool AggregationEnabled { get; set; }

    public IReadOnlyList<TableMetadata> VisibleTables { set => Visibility.Visible = value.ToDictionary(t => t.Identifier); }

    public static TableMetadata Table(TableIdentifier id, IReadOnlyList<string>? tags = null, params string[] columns) => new()
    {
        Identifier = id,
        CatalogTags = tags ?? [],
        Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = (columns.Length == 0 ? ["id", SecretColumn, "order_id", "customer_id", "note"] : columns)
            .Select(c => new TableColumn { ColumnName = c, DataType = c.EndsWith("id", StringComparison.Ordinal) ? "int" : "nvarchar(100)" }).ToList(),
        PrimaryKeyColumns = [id == Customers ? "id" : "order_id"]
    };

    public async Task<IRequestExecutor> BuildAsync(IReadOnlyList<TableMetadata>? catalog = null)
    {
        catalog ??= [Table(Customers, ["pii"]), Table(Orders)];
        var metadataRepo = new Autheris.Tests.Common.Fakes.InMemoryTableMetadataRepository(catalog.ToArray());
        var relationRepo = new Autheris.Tests.Common.Fakes.InMemoryTableRelationRepository(
            new TableRelation
            {
                RelationName = "orders",
                ParentTableIdentifier = Customers,
                ChildTableIdentifier = Orders,
                Cardinality = RelationCardinality.OneToMany,
                JoinKeysParent = ["id"],
                JoinKeysChild = ["customer_id"]
            });

        var services = new ServiceCollection();
        services.AddSingleton<ITableMetadataRepository>(metadataRepo);
        services.AddSingleton<ITableRelationRepository>(relationRepo);
        services.AddSingleton<IGovernedTreeQueryService>(Tree);
        services.AddSingleton<ICatalogVisibilityService>(Visibility);
        services.AddSingleton<IGovernedAggregationService>(new FakeAggregation(this));
        services.AddSingleton<Autheris.Application.Analytics.ISemanticMetricQueryService>(new UnusedMetricService());
        services.AddSingleton(Options.Create(new AnalyticsOptions()));
        var accessor = new StaticHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim(ClaimTypes.NameIdentifier, "user-alice-123"), new Claim("tenant_id", "T1")], "TestAuth"))
            }
        };
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddSingleton<Autheris.Application.DataSources.Interfaces.IKeysetCursorEncoder>(new Autheris.Application.DataSources.Services.KeysetCursorEncoder("test-signing-key-32-bytes-long!!"));

        var builder = services.AddGraphQLServer().AddQueryType<Query>();
        if (UseMiddleware)
        {
            builder.UseRequest<DatasetFactsMiddleware>();
        }

        builder.UseRequest<CatalogOperationCleanupMiddleware>();
        if (InjectedError is { } injected)
        {
            builder.UseRequest(next => async context =>
            {
                await next(context);
                if (context.Result is OperationResult op)
                {
                    op.Errors = (op.Errors ?? []).Add(injected);
                }
            });
        }

        return await builder
            .UseDefaultPipeline()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(metadataRepo, relationRepo, null, null, null, AggregationEnabled))
            .BuildRequestExecutorAsync();
    }

    public static Task<OperationResult> RunAsync(IRequestExecutor executor, string query, CancellationToken ct = default) =>
        RunAsync(executor, query, true, ct);

    public static async Task<OperationResult> RunAsync(IRequestExecutor executor, string query, object? autherisExtension, CancellationToken ct = default)
    {
        var request = OperationRequestBuilder.New().SetDocument(query);
        if (autherisExtension is not null)
        {
            request.SetExtensions(new Dictionary<string, object?>
            {
                ["autheris"] = autherisExtension is true
                    ? new Dictionary<string, object?> { ["datasetFacts"] = true }
                    : autherisExtension
            });
        }

        return (await executor.ExecuteAsync(request.Build(), ct)).ExpectOperationResult();
    }

    public static JsonDocument Json(OperationResult result) => JsonDocument.Parse(result.ToJson());

    /// <summary><c>extensions.autheris.roots</c> of the result, or null when absent.</summary>
    public static JsonElement? Roots(OperationResult result)
    {
        var doc = Json(result);
        return doc.RootElement.TryGetProperty("extensions", out var ext)
            && ext.TryGetProperty("autheris", out var autheris)
            && autheris.TryGetProperty("roots", out var roots)
                ? roots.Clone()
                : null;
    }

    private sealed class StaticHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class UnusedMetricService : Autheris.Application.Analytics.ISemanticMetricQueryService
    {
        public Task<Autheris.Application.Analytics.MetricQueryResult> QueryMetricsAsync(
            ClaimsPrincipal principal, Autheris.Application.Analytics.MetricQueryRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    public sealed class FakeTree : IGovernedTreeQueryService
    {
        public Dictionary<TableIdentifier, string[]> RowFilters { get; } = [];

        public Dictionary<TableIdentifier, string> RowJson { get; } = new()
        {
            [Customers] = $$"""[{"id":1,"email":"{{SecretEmail}}","orders":[{"order_id":1,"note":"n1"}]},{"id":2,"email":"{{SecretEmail}}","orders":[{"order_id":2,"note":"n2"}]}]""",
            [Orders] = """[{"order_id":1,"customer_id":1,"note":"n1"}]"""
        };

        public HashSet<TableIdentifier> Denied { get; } = [];

        public bool Cancelled { get; set; }

        public bool NoFacts { get; set; }

        public int ExtraRelations { get; set; }

        public Task<JsonDocument> ExecuteAsync(ClaimsPrincipal? principal, TreeQueryNode root, IReadOnlyDictionary<string, string[]>? requestHeaders, string operationId, CancellationToken ct = default)
        {
            if (Cancelled)
            {
                throw new OperationCanceledException();
            }

            if (Denied.Contains(root.Table))
            {
                throw new GatewayForbiddenException();
            }

            return Task.FromResult(JsonDocument.Parse(RowJson[root.Table]));
        }

        public Task<long> CountAsync(
            ClaimsPrincipal? principal, TreeQueryNode root, IReadOnlyDictionary<string, string[]>? requestHeaders, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<TreeMaskedColumn> GetMaskedColumns(ClaimsPrincipal? principal, TreeQueryNode root, string operationId) => [];

        public IReadOnlyList<DatasetAccessFacts> GetDatasetFacts(ClaimsPrincipal? principal, TreeQueryNode root, string operationId)
        {
            if (NoFacts)
            {
                return [];
            }

            var facts = new List<DatasetAccessFacts>();
            Walk(root, [], facts);
            for (var i = 0; i < ExtraRelations; i++)
            {
                facts.Add(new DatasetAccessFacts(Orders, [$"extra{i}"], []));
            }

            return facts;
        }

        public void ClearOperation(string operationId)
        {
        }

        private void Walk(TreeQueryNode node, IReadOnlyList<string> path, List<DatasetAccessFacts> facts)
        {
            facts.Add(new DatasetAccessFacts(node.Table, path, RowFilters.GetValueOrDefault(node.Table) ?? []));
            foreach (var relation in node.Relations)
            {
                Walk(relation.Child, [.. path, relation.ResponseKey], facts);
            }
        }
    }

    public sealed class FakeVisibility : ICatalogVisibilityService
    {
        private int _calls;

        public Dictionary<TableIdentifier, TableMetadata> Visible { get; set; } = [];

        public bool Authoritative { get; set; } = true;

        public bool Throws { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract = null, CancellationToken ct = default) =>
            ValueTask.FromResult(CatalogVisibleSet.Empty);

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult<CatalogVisibleRow?>(Visible.TryGetValue(table, out var t) ? new CatalogVisibleRow(t, CatalogOutputShape.Full) : null);

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(candidates.Where(Visible.ContainsKey).ToHashSet());

        public ValueTask<CatalogVisibleBatch> FindVisibleManyAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (Throws)
            {
                throw new InvalidOperationException("visibility down");
            }

            var rows = new Dictionary<TableIdentifier, CatalogVisibleRow>();
            foreach (var candidate in candidates)
            {
                if (Visible.TryGetValue(candidate, out var table))
                {
                    rows[candidate] = new CatalogVisibleRow(table, CatalogOutputShape.Full);
                }
            }

            return ValueTask.FromResult(new CatalogVisibleBatch(Authoritative, rows));
        }
    }

    private sealed class FakeAggregation(DatasetFactsTestHost host) : IGovernedAggregationService
    {
        public Task<AggregationResult> ExecuteAggregationAsync(ClaimsPrincipal principal, AggregationQuery query, CancellationToken ct = default) =>
            host.Aggregate is { } produce ? Task.FromResult(produce(query)) : throw new GatewayForbiddenException();
    }
}
