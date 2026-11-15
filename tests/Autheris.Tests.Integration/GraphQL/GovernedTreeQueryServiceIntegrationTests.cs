namespace Autheris.Tests.Integration.GraphQL;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit;
using Autheris.Tests.Unit.GraphQL;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;
using BinaryOperator = TrinoSqlEngine.Ast.Nodes.BinaryOperator;

/// <summary>
/// PostgreSQL integration tests for <see cref="GovernedTreeQueryService"/> covering single-round-trip nested queries,
/// mixed-case sources, cross-domain child row filters/masks and composite relations.
/// </summary>
public sealed class GovernedTreeQueryServiceIntegrationTests
{
    private static readonly TableIdentifier Authors = new("blog", "public", "authors");
    private static readonly TableIdentifier Articles = new("blog", "public", "articles");
    private static readonly TableIdentifier CrmArticles = new("crm", "public", "articles");
    private static readonly TableIdentifier Orders = new("blog", "public", "orders");
    private static readonly TableIdentifier Lines = new("blog", "public", "lines");

    private static readonly (string, string)[] AuthorColumns = [("id", "integer"), ("name", "text"), ("email", "text")];
    private static readonly (string, string)[] ArticleColumns = [("id", "integer"), ("author_id", "integer"), ("title", "text")];
    private static readonly (string, string)[] OrderColumns = [("id", "integer"), ("k1", "integer"), ("k2", "integer"), ("k3", "integer"), ("name", "text")];
    private static readonly (string, string)[] LineColumns = [("id", "integer"), ("p1", "integer"), ("p2", "integer"), ("p3", "integer"), ("label", "text")];

    private static TableMetadata Meta(
        TableIdentifier id,
        string sourceName,
        IReadOnlyDictionary<string, MaskingRule>? masking,
        params (string Name, string Type)[] columns) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = sourceName, SchemaName = id.Schema, TableName = id.TableName, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        PrimaryKeyColumns = ["id"],
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList(),
        ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
    };

    private static ResolvedTableAccess Allowed(TableMetadata metadata) => new(
        metadata,
        TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
        new TenantId("t1"),
        new Sid("S-1-5-21-DAVID"),
        DefaultTenantCaller.Principal());

    private static Fixture Create(string connectionString, Action<Dictionary<TableIdentifier, ResolvedTableAccess>>? configure = null)
    {
        var access = new Dictionary<TableIdentifier, ResolvedTableAccess>
        {
            [Authors] = Allowed(Meta(Authors, "blog", null, AuthorColumns)),
            [Articles] = Allowed(Meta(Articles, "blog", null, ArticleColumns))
        };
        configure?.Invoke(access);

        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["blog"] = new() { Provider = "PostgreSql", ConnectionString = connectionString },
                    ["crm"] = new() { Provider = "PostgreSql", ConnectionString = connectionString }
                }
            }
        };

        var resolver = new RecordingTableAccessResolver(access);
        var audit = new RecordingAuditLogRepository();
        var factory = new CountingNpgsqlFactory(connectionString);
        var service = new GovernedTreeQueryService(resolver, factory, audit, new ColumnMaskingProvider(), Options.Create(options));
        return new Fixture { Service = service, Resolver = resolver, Audit = audit, Factory = factory };
    }

    private static async Task SeedAuthorsAndArticlesAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.authors (id integer PRIMARY KEY, name text, email text);
            CREATE TABLE public.articles (id integer PRIMARY KEY, author_id integer, title text);
            INSERT INTO public.authors VALUES (1, 'Ann', 'ann@example.com'), (2, 'Bob', 'bob@example.com');
            INSERT INTO public.articles VALUES (10, 1, 'A1'), (11, 1, 'A2'), (20, 2, 'B1');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedCompositeAsync(string connectionString, string? extraSql = null)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.orders (id integer PRIMARY KEY, k1 integer, k2 integer, k3 integer, name text);
            CREATE TABLE public.lines (id integer PRIMARY KEY, p1 integer, p2 integer, p3 integer, label text);
            INSERT INTO public.orders VALUES (1, 1, 1, 1, 'o1'), (2, 1, 1, 2, 'o2'), (3, 1, 2, 1, 'o3');
            INSERT INTO public.lines VALUES (10, 1, 1, 1, 'L10'), (11, 1, 1, 2, 'L11'), (12, 1, 2, 1, 'L12'), (13, 1, 2, 2, 'L13'), (14, 9, 9, 9, 'L14');
            """ + (extraSql ?? string.Empty);
        await cmd.ExecuteNonQueryAsync();
    }

    private static TreeQueryNode AuthorsWithArticles() => new(Authors, ["id", "name"])
    {
        Relations = [new TreeRelationNode("articles", ["id"], ["author_id"], true, new TreeQueryNode(Articles, ["title"]) { OrderBy = [new TreeOrder("id")] })],
        OrderBy = [new TreeOrder("id")]
    };

    private static TreeQueryNode AuthorsWithCrmArticles() => new(Authors, ["id"])
    {
        Relations = [new TreeRelationNode("articles", ["id"], ["author_id"], true, new TreeQueryNode(CrmArticles, ["title"]) { OrderBy = [new TreeOrder("id")] })],
        OrderBy = [new TreeOrder("id")]
    };

    private static TreeQueryNode OrdersWithLines(int keys) => new(Orders, ["id", "name"])
    {
        Relations = [new TreeRelationNode("lines", new[] { "k1", "k2", "k3" }[..keys], new[] { "p1", "p2", "p3" }[..keys], true,
            new TreeQueryNode(Lines, ["label"]) { OrderBy = [new TreeOrder("id")] })],
        OrderBy = [new TreeOrder("id")]
    };

    private static TreeQueryNode LinesWithOrder() => new(Lines, ["id"])
    {
        Relations = [new TreeRelationNode("order", ["p1", "p2", "p3"], ["k1", "k2", "k3"], false, new TreeQueryNode(Orders, ["name"]))],
        OrderBy = [new TreeOrder("id")]
    };

    private static List<string?> Labels(JsonElement row) =>
        row.GetProperty("lines").EnumerateArray().Select(x => x.GetProperty("label").GetString()).ToList();

    [RequiresContainerRuntimeFact]
    public async Task NestedQuery_RunsInOneRoundTrip_AndReturnsJson()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAuthorsAndArticlesAsync(db.ConnectionString);
        var fixture = Create(db.ConnectionString);

        using var result = await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), AuthorsWithArticles());

        fixture.Factory.Opened.ShouldBe(1);
        var root = result.RootElement;
        root.GetArrayLength().ShouldBe(2);
        root[0].GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("title").GetString()).ShouldBe(["A1", "A2"], ignoreOrder: false);
        root[1].GetProperty("articles").GetArrayLength().ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Tree_MixedCaseSourceNames_ExecuteAsOneStatement_WithGovernancePerTable()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAuthorsAndArticlesAsync(db.ConnectionString);
        var titleMask = new Dictionary<string, MaskingRule> { ["title"] = new MaskingRule { RuleType = "REDACT" } };
        var fixture = Create(db.ConnectionString, a =>
        {
            a[Authors] = Allowed(Meta(Authors, "blog", null, AuthorColumns));
            var articles = Meta(Articles, "BLOG", titleMask, ArticleColumns);
            a[Articles] = Allowed(articles) with
            {
                Decision = TypedTreeRowPolicy.Allowed(
                    Articles,
                    new Dictionary<string, ColumnAccessLevel> { ["title"] = ColumnAccessLevel.Mask },
                    TypedTreeRowPolicy.Int("author_id", BinaryOperator.Equal, 1))
            };
        });

        using var result = await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), AuthorsWithArticles());

        fixture.Factory.Opened.ShouldBe(1);
        fixture.Resolver.Calls.Count(t => t == Authors).ShouldBe(1);
        fixture.Resolver.Calls.Count(t => t == Articles).ShouldBe(1);
        fixture.Audit.Entries.Count.ShouldBe(2);
        var root = result.RootElement;
        root.GetArrayLength().ShouldBe(2);
        var firstArticles = root[0].GetProperty("articles").EnumerateArray().Select(x => x.GetProperty("title").GetString()).ToList();
        firstArticles.Count.ShouldBe(2);
        firstArticles.ShouldNotContain("A1");
        firstArticles.ShouldNotContain("A2");
        root[1].GetProperty("articles").GetArrayLength().ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task Tree_CrossDomainSameSource_AppliesRowFilterAndMaskOfTheChild()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAuthorsAndArticlesAsync(db.ConnectionString);
        var titleMask = new Dictionary<string, MaskingRule> { ["title"] = new MaskingRule { RuleType = "REDACT" } };
        var fixture = Create(db.ConnectionString, a =>
        {
            var child = Meta(CrmArticles, "blog", titleMask, ArticleColumns);
            a[CrmArticles] = Allowed(child) with
            {
                Decision = TypedTreeRowPolicy.Allowed(
                    CrmArticles,
                    new Dictionary<string, ColumnAccessLevel> { ["title"] = ColumnAccessLevel.Mask },
                    TypedTreeRowPolicy.Int("author_id", BinaryOperator.Equal, 1))
            };
        });

        using var result = await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), AuthorsWithCrmArticles());

        fixture.Factory.Opened.ShouldBe(1);
        var root = result.RootElement;
        var titles = root[0].GetProperty("articles").EnumerateArray().Select(x => x.GetProperty("title").GetString()).ToList();
        titles.Count.ShouldBe(2);
        titles.ShouldNotContain("A1");
        titles.ShouldNotContain("A2");
        root[1].GetProperty("articles").GetArrayLength().ShouldBe(0);
    }

    private static Fixture CreateComposite(string connectionString, IRowPolicy? orderFilter = null, IRowPolicy? lineFilter = null) =>
        Create(connectionString, a =>
        {
            var orders = Meta(Orders, "blog", null, OrderColumns);
            var lines = Meta(Lines, "blog", null, LineColumns);
            a[Orders] = new ResolvedTableAccess(
                orders,
                TypedTreeRowPolicy.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), orderFilter),
                new TenantId("t1"),
                new Sid("S-1-5-21-DAVID"),
                DefaultTenantCaller.Principal());
            a[Lines] = new ResolvedTableAccess(
                lines,
                TypedTreeRowPolicy.Allowed(Lines, new Dictionary<string, ColumnAccessLevel>(), lineFilter),
                new TenantId("t1"),
                new Sid("S-1-5-21-DAVID"),
                DefaultTenantCaller.Principal());
        });

    [RequiresContainerRuntimeFact]
    public async Task CompositeRelation_JoinsOnAllColumns_BothDirections()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedCompositeAsync(db.ConnectionString);
        var fixture = CreateComposite(db.ConnectionString);

        using var down = await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), OrdersWithLines(3));
        Labels(down.RootElement[0]).ShouldBe(["L10"]);
        Labels(down.RootElement[1]).ShouldBe(["L11"]);
        Labels(down.RootElement[2]).ShouldBe(["L12"]);

        using var up = await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), LinesWithOrder());
        up.RootElement[0].GetProperty("order").GetProperty("name").GetString().ShouldBe("o1");
        up.RootElement[1].GetProperty("order").GetProperty("name").GetString().ShouldBe("o2");
        up.RootElement[2].GetProperty("order").GetProperty("name").GetString().ShouldBe("o3");
        up.RootElement[3].GetProperty("order").ValueKind.ShouldBe(JsonValueKind.Null);
        up.RootElement[4].GetProperty("order").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task CompositeRelation_FirstKeyMatchOnly_ReturnsNoChildRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedCompositeAsync(db.ConnectionString, "INSERT INTO public.orders VALUES (4, 5, 5, 5, 'o4'); INSERT INTO public.lines VALUES (15, 5, 6, 6, 'L15'), (16, 5, 5, 6, 'L16');");
        var fixture = CreateComposite(db.ConnectionString);

        using var result = await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), OrdersWithLines(3));

        Labels(result.RootElement[3]).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task CompositeRelation_RowFilterAppliedPerLevel()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedCompositeAsync(db.ConnectionString);
        var fixture = CreateComposite(
            db.ConnectionString,
            orderFilter: TypedTreeRowPolicy.Text("name", BinaryOperator.NotEqual, "o3"),
            lineFilter: TypedTreeRowPolicy.Text("label", BinaryOperator.NotEqual, "L10"));

        using var result = await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), OrdersWithLines(3));

        result.RootElement.GetArrayLength().ShouldBe(2);
        Labels(result.RootElement[0]).ShouldBeEmpty();
        Labels(result.RootElement[1]).ShouldBe(["L11"]);
    }

    private sealed class CountingNpgsqlFactory(string connectionString) : ISqlConnectionFactory
    {
        public int Opened { get; private set; }

        public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            Opened++;
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }
    }

    private sealed class RecordingTableAccessResolver(IReadOnlyDictionary<TableIdentifier, ResolvedTableAccess> access) : ITableAccessResolver
    {
        public List<TableIdentifier> Calls { get; } = [];

        public Task<ResolvedTableAccess> ResolveTableAccessAsync(
            ClaimsPrincipal? principal,
            TableIdentifier table,
            IReadOnlyList<string>? requestedFields,
            IReadOnlyDictionary<string, string[]>? requestHeaders,
            CancellationToken ct = default)
        {
            Calls.Add(table);
            return Task.FromResult(access[table]);
        }
    }

    private sealed class RecordingAuditLogRepository : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) =>
            RecordAuditEventAsync(entry, ct);

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class Fixture
    {
        public required GovernedTreeQueryService Service { get; init; }
        public required RecordingTableAccessResolver Resolver { get; init; }
        public required RecordingAuditLogRepository Audit { get; init; }
        public required CountingNpgsqlFactory Factory { get; init; }

        public Task<JsonDocument> ExecuteAsync(ClaimsPrincipal? principal, TreeQueryNode root) =>
            Service.ExecuteAsync(principal, root, null, Guid.NewGuid().ToString("N"), CancellationToken.None);
    }
}
