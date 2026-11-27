using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// Real catalog schema, cost rule, error filter and (optionally) the enumeration shield for the validation-error disclosure tests.
/// Tables: customers (fully visible), masked (one Mask column), partial (one Deny column), secrets (denied), orders (relation to secrets).
/// </summary>
internal sealed class DisclosureFixture
{
    public const string ValidationBody = "{\"errors\":[{\"message\":\"The GraphQL request is invalid.\",\"extensions\":{\"code\":\"GRAPHQL_VALIDATION_FAILED\"}}]}";

    public static readonly TableIdentifier Customers = new("sales", "dbo", "customers");
    public static readonly TableIdentifier Masked = new("sales", "dbo", "masked");
    public static readonly TableIdentifier Partial = new("sales", "dbo", "partial");
    public static readonly TableIdentifier Secrets = new("sales", "dbo", "secrets");
    public static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    public HashSet<TableIdentifier> DeniedTables { get; } = [Secrets];

    /// <summary>Per table column levels; defaults are set for masked and partial.</summary>
    public Dictionary<TableIdentifier, Dictionary<string, ColumnAccessLevel>> Levels { get; } = new()
    {
        [Masked] = new(StringComparer.OrdinalIgnoreCase) { ["note"] = ColumnAccessLevel.Mask },
        [Partial] = new(StringComparer.OrdinalIgnoreCase) { ["hidden"] = ColumnAccessLevel.Deny },
    };

    /// <summary>Column names reported by the resolver metadata when they differ from the schema (drift tests).</summary>
    public Dictionary<TableIdentifier, string[]> MetadataColumnNames { get; } = new();

    public Func<TableIdentifier, bool>? ThrowFor { get; set; }

    public int ExtraTables { get; set; }

    public bool IncludeResolver { get; set; } = true;

    public bool WithShield { get; set; } = true;

    public bool Authenticated { get; set; } = true;

    public bool WithErrorFilter { get; set; } = true;

    public int MaxRootFields { get; set; } = 25;

    public static TableIdentifier Extra(int i) => new("sales", "dbo", $"extra{i:00}");

    private static string[] ColumnsOf(TableIdentifier id) =>
        id == Masked ? ["id", "note"] : id == Partial ? ["id", "hidden"] : id == Orders ? ["id", "secret_ref"] : ["id", "name"];

    private TableMetadata MetadataFor(TableIdentifier id, bool forSchema)
    {
        var names = !forSchema && MetadataColumnNames.TryGetValue(id, out var o) ? o : ColumnsOf(id);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [.. names.Select(n => new TableColumn { ColumnName = n, DataType = n is "id" or "secret_ref" ? "int" : "nvarchar(100)" })],
            PrimaryKeyColumns = ["id"]
        };
    }

    private IEnumerable<TableIdentifier> AllTables() =>
        new[] { Customers, Masked, Partial, Secrets, Orders }.Concat(Enumerable.Range(0, ExtraTables).Select(Extra));

    public async Task<IRequestExecutor> CreateAsync(string environmentName)
    {
        IHostEnvironment env = new TestHostEnvironment(environmentName);
        var metadataRepo = new InMemoryTableMetadataRepository([.. AllTables().Select(t => MetadataFor(t, forSchema: true))]);
        var relationRepo = new InMemoryTableRelationRepository(new TableRelation
        {
            RelationName = "secret",
            ParentTableIdentifier = Orders,
            ChildTableIdentifier = Secrets,
            Cardinality = RelationCardinality.ManyToOne,
            JoinKeysParent = ["secret_ref"],
            JoinKeysChild = ["id"]
        });

        var resolver = new StubTableAccessResolver((principal, id) =>
        {
            if (ThrowFor?.Invoke(id) == true) throw new InvalidOperationException("resolver failure");
            var meta = MetadataFor(id, forSchema: false);
            var decision = DeniedTables.Contains(id)
                ? TableAccessDecision.Denied(id, ["Access denied."])
                : TableAccessDecision.Allowed(
                    id,
                    new Dictionary<string, ColumnAccessLevel>(Levels.TryGetValue(id, out var l) ? l : [], StringComparer.OrdinalIgnoreCase),
                    null,
                    hasUnconstrainedColumnAllow: true);
            return new ResolvedTableAccess(meta, decision, new TenantId("T1"), new Sid("S-1-5-21-ALICE"), principal ?? new ClaimsPrincipal());
        });

        var services = new ServiceCollection();
        services.AddSingleton<ITableMetadataRepository>(metadataRepo);
        services.AddSingleton<ITableRelationRepository>(relationRepo);
        services.AddSingleton<IGovernedTreeQueryService>(new DenyingTreeService(this));
        if (IncludeResolver) services.AddSingleton<ITableAccessResolver>(resolver);
        services.AddSingleton(env);
        services.AddSingleton(_ => new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance));
        var identity = Authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant_id", "T1")], "TestAuth")
            : new ClaimsIdentity();
        services.AddSingleton<IHttpContextAccessor>(new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }));
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var builder = services
            .AddGraphQLServer()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddQueryType<Query>();
        if (WithErrorFilter) builder = builder.AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>());
        if (WithShield) builder = builder.UseRequest<GraphQlEnumerationShieldMiddleware>();
        return await builder
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxAllowedCost: 5000, maxResponseRows: 5000, maxRootFields: MaxRootFields))
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>()))
            .BuildRequestExecutorAsync();
    }

    /// <summary>Tree executor that refuses a denied table or a denied column like the governed pipeline and returns no rows otherwise.</summary>
    private sealed class DenyingTreeService(DisclosureFixture fixture) : IGovernedTreeQueryService
    {
        public Task<JsonDocument> ExecuteAsync(
            ClaimsPrincipal? principal, TreeQueryNode root, IReadOnlyDictionary<string, string[]>? requestHeaders, string operationId, CancellationToken ct = default)
        {
            Check(root);
            return Task.FromResult(JsonDocument.Parse("[]"));
        }

        public IReadOnlyList<TreeMaskedColumn> GetMaskedColumns(ClaimsPrincipal? principal, TreeQueryNode root, string operationId) => [];

        public void ClearOperation(string operationId)
        {
        }

        private void Check(TreeQueryNode node)
        {
            var denied = fixture.DeniedTables.Contains(node.Table)
                || (fixture.Levels.TryGetValue(node.Table, out var levels)
                    && node.Columns.Any(c => levels.TryGetValue(c, out var level) && level == ColumnAccessLevel.Deny));
            if (denied)
            {
                throw new WebSqlTableDeniedException("denied");
            }

            foreach (var relation in node.Relations)
            {
                Check(relation.Child);
            }
        }
    }

    public static async Task<(string Json, OperationResult Result)> RunAsync(IRequestExecutor executor, string query, string? variablesJson = null)
    {
        var request = variablesJson is null
            ? OperationRequest.FromSourceText(query)
            : OperationRequest.FromSourceText(query, variableValues: JsonDocument.Parse(variablesJson));
        var result = (OperationResult)await executor.ExecuteAsync(request);
        return (JsonSerializer.Serialize(JsonDocument.Parse(result.ToJson()).RootElement), result);
    }
}

/// <summary>Accessor with a plain property: the stock accessor keeps its value in an async-local that does not flow out of a fixture method.</summary>
internal sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; } = context;
}
