using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Unit.Sql.Tree;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// G2/G3/G5 (docs/plans/rls-subquery-in-strategy.md): the service resolves access once per table and request, runs the
/// whole tree in ONE database round trip, pseudonymizes HMAC columns in the gateway and writes one audit entry per table.
/// </summary>
public sealed class GovernedTreeQueryServiceTests : IDisposable
{
    private static readonly TableIdentifier Authors = new("blog", "main", "authors");
    private static readonly TableIdentifier Articles = new("blog", "main", "articles");
    private static readonly TableIdentifier Elsewhere = new("crm", "main", "contacts");

    private readonly string _connectionString = $"Data Source=file:tree{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;

    public GovernedTreeQueryServiceTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT, email TEXT);
            CREATE TABLE articles (id INTEGER PRIMARY KEY, author_id INTEGER, title TEXT);
            INSERT INTO authors VALUES (1, 'Ann', 'ann@example.com'), (2, 'Bob', 'bob@example.com');
            INSERT INTO articles VALUES (10, 1, 'A1'), (11, 1, 'A2'), (20, 2, 'B1');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive.Dispose();

    private sealed class CountingFactory(string connectionString) : ISqlConnectionFactory
    {
        public int Opened { get; private set; }

        public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            Opened++;
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }
    }

    private static TableMetadata Meta(TableIdentifier id, string sourceName, params (string Name, string Type)[] columns) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = sourceName, SchemaName = id.Schema, TableName = id.TableName, SourceType = "Sqlite" },
        PrimaryKeyColumns = ["id"],
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList()
    };

    private static ResolvedTableAccess Allowed(TableMetadata metadata) => new(
        metadata,
        TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
        new TenantId("t1"),
        new Sid("S-1-5-21-DAVID"),
        new ClaimsPrincipal());

    private sealed class Fixture
    {
        public required GovernedTreeQueryService Service { get; init; }
        public required ITableAccessResolver Resolver { get; init; }
        public required IAuditLogRepository Audit { get; init; }
        public required CountingFactory Factory { get; init; }

        public Task<JsonDocument> ExecuteAsync(
            ClaimsPrincipal? principal,
            TreeQueryNode root,
            IReadOnlyDictionary<string, string[]>? headers,
            string? opId = null,
            CancellationToken ct = default) =>
            Service.ExecuteAsync(principal, root, headers, opId ?? Guid.NewGuid().ToString("N"), ct);
    }

    private Fixture Create(
        Action<Dictionary<TableIdentifier, ResolvedTableAccess>>? configure = null,
        GatewayOptions? options = null,
        ITableReadConcurrencyGate? gate = null,
        IColumnMaskingProvider? masking = null)
    {
        var access = new Dictionary<TableIdentifier, ResolvedTableAccess>
        {
            [Authors] = Allowed(Meta(Authors, "blog", ("id", "integer"), ("name", "text"), ("email", "text"))),
            [Articles] = Allowed(Meta(Articles, "blog", ("id", "integer"), ("author_id", "integer"), ("title", "text"))),
            [Elsewhere] = Allowed(Meta(Elsewhere, "crm", ("id", "integer"), ("author_id", "integer")))
        };
        configure?.Invoke(access);

        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(ci => access[ci.ArgAt<TableIdentifier>(1)]);

        options ??= new GatewayOptions();
        var effectiveOptions = new GatewayOptions
        {
            GraphQL = options.GraphQL,
            DataMasking = options.DataMasking,
            DataSources = new SqlDataSourceOptions
            {
                MaxConcurrentReadsPerUserAndTable = options.DataSources.MaxConcurrentReadsPerUserAndTable,
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["blog"] = new() { Provider = "Sqlite", ConnectionString = _connectionString },
                    ["crm"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var audit = Substitute.For<IAuditLogRepository>();
        var factory = new CountingFactory(_connectionString);
        var service = new GovernedTreeQueryService(
            resolver, factory, audit, masking ?? Substitute.For<IColumnMaskingProvider>(), Options.Create(effectiveOptions), gate);
        return new Fixture { Service = service, Resolver = resolver, Audit = audit, Factory = factory };
    }

    private static TreeQueryNode AuthorsWithArticles() => new(Authors, ["id", "name"])
    {
        Relations = [new TreeRelationNode("articles", ["id"], ["author_id"], true, new TreeQueryNode(Articles, ["title"]) { OrderBy = [new TreeOrder("id")] })],
        OrderBy = [new TreeOrder("id")]
    };

    [Fact]
    public async Task NestedQuery_RunsInOneRoundTrip_AndReturnsJson()
    {
        var fixture = Create();

        using var result = await fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null);

        fixture.Factory.Opened.ShouldBe(1);
        var root = result.RootElement;
        root.GetArrayLength().ShouldBe(2);
        root[0].GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("title").GetString()).ShouldBe(new[] { "A1", "A2" }, ignoreOrder: false);
        root[1].GetProperty("articles").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task AccessIsResolvedOncePerTable_EvenWhenTheTableAppearsTwice()
    {
        var fixture = Create();
        var tree = new TreeQueryNode(Authors, ["id"])
        {
            Relations =
            [
                new TreeRelationNode("articles", ["id"], ["author_id"], true, new TreeQueryNode(Articles, ["id"])
                {
                    Relations = [new TreeRelationNode("author", ["author_id"], ["id"], false, new TreeQueryNode(Authors, ["name"]))]
                })
            ]
        };

        var opId = "op-shared-test";
        await fixture.ExecuteAsync(new ClaimsPrincipal(), tree, null, opId);
        await fixture.ExecuteAsync(new ClaimsPrincipal(), tree, null, opId); // same request scope: memoized

        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Authors, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Articles, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneAuditEntryPerTableAndRequest()
    {
        var fixture = Create();

        await fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null);

        await fixture.Audit.Received(2).RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        await fixture.Audit.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.TargetTable == Articles.ToString() && e.Decision == "ALLOW"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeniedTable_FailsBeforeAnyDatabaseAccess_AndIsAudited()
    {
        var fixture = Create(a => a[Articles] = a[Articles] with { Decision = TableAccessDecision.Denied(Articles, "no consent") });

        await Should.ThrowAsync<GatewayForbiddenException>(() => fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null));

        fixture.Factory.Opened.ShouldBe(0);
        await fixture.Audit.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.TargetTable == Articles.ToString() && e.Decision == "DENY"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelationAcrossDataSources_IsRejected()
    {
        var fixture = Create();
        var tree = new TreeQueryNode(Authors, ["id"])
        {
            Relations = [new TreeRelationNode("contacts", ["id"], ["author_id"], true, new TreeQueryNode(Elsewhere, ["id"]))]
        };

        await Should.ThrowAsync<GatewayInvalidQueryException>(() => fixture.ExecuteAsync(new ClaimsPrincipal(), tree, null));
        fixture.Factory.Opened.ShouldBe(0);
    }

    private static TableMetadata MetaOf(
        TableIdentifier id,
        string sourceName,
        string sourceType,
        DataSourceType dataSourceType,
        IReadOnlyDictionary<string, MaskingRule>? masking,
        params (string Name, string Type)[] columns) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = sourceName, SchemaName = id.Schema, TableName = id.TableName, SourceType = sourceType, DataSourceType = dataSourceType },
        PrimaryKeyColumns = ["id"],
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList(),
        ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
    };

    private static readonly (string, string)[] AuthorColumns = [("id", "integer"), ("name", "text"), ("email", "text")];
    private static readonly (string, string)[] ArticleColumns = [("id", "integer"), ("author_id", "integer"), ("title", "text")];

    [Theory]
    [MemberData(nameof(RelationExecutabilityTests.Cases), MemberType = typeof(RelationExecutabilityTests))]
    public async Task GovernedTreeQueryService_UsesSharedPredicate(
        string name,
        string parentSource,
        string childSource,
        string childSourceType,
        DataSourceType childDsType,
        bool executable,
        RelationExecutabilityTests.TreeOutcome outcome)
    {
        _ = name;
        var authors = MetaOf(Authors, parentSource, "Sqlite", DataSourceType.Sql, null, AuthorColumns);
        var articles = MetaOf(Articles, childSource, childSourceType, childDsType, null, ArticleColumns);

        // Runtime: the tree service rejects exactly the cases the shared predicate rejects, with the unchanged errors.
        var fixture = Create(a =>
        {
            a[Authors] = Allowed(authors);
            a[Articles] = Allowed(articles);
        });
        var run = () => fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null);
        switch (outcome)
        {
            case RelationExecutabilityTests.TreeOutcome.Runs:
                using (await run())
                {
                }
                fixture.Factory.Opened.ShouldBe(1);
                break;
            case RelationExecutabilityTests.TreeOutcome.AcrossSources:
                (await Should.ThrowAsync<GatewayInvalidQueryException>(run)).Message.ShouldBe("Relations across data sources are not supported in one query.");
                break;
            case RelationExecutabilityTests.TreeOutcome.NotSql:
                (await Should.ThrowAsync<GatewayInvalidQueryException>(run)).Message.ShouldContain("is not a SQL table");
                break;
            case RelationExecutabilityTests.TreeOutcome.DialectMismatch:
                (await Should.ThrowAsync<InvalidOperationException>(run)).Message.ShouldContain("does not match the provider");
                break;
            case RelationExecutabilityTests.TreeOutcome.NoConnection:
                (await Should.ThrowAsync<InvalidOperationException>(run)).Message.ShouldContain("has no database connection");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (outcome != RelationExecutabilityTests.TreeOutcome.Runs)
        {
            fixture.Factory.Opened.ShouldBe(0);
        }

        // Schema: the relation is exposed exactly when the predicate accepts the pair.
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([authors, articles]);
        var relations = Substitute.For<ITableRelationRepository>();
        relations.GetRelationsForTableAsync(Authors, Arg.Any<CancellationToken>()).Returns([new TableRelation
        {
            RelationName = "articles",
            ParentTableIdentifier = Authors,
            ChildTableIdentifier = Articles,
            Cardinality = RelationCardinality.OneToMany,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["author_id"]
        }]);
        relations.GetRelationsForTableAsync(Articles, Arg.Any<CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metadata, relations);

        model.Tables.Single(t => t.Identifier == Authors).Relations.Any().ShouldBe(executable);
    }

    [Fact]
    public async Task Tree_MixedCaseSourceNames_ExecuteAsOneStatement_WithGovernancePerTable()
    {
        var titleMask = new Dictionary<string, MaskingRule> { ["title"] = new MaskingRule { RuleType = "REDACT" } };
        var fixture = Create(a =>
        {
            a[Authors] = Allowed(MetaOf(Authors, "blog", "Sqlite", DataSourceType.Sql, null, AuthorColumns));
            var articles = MetaOf(Articles, "BLOG", "Sqlite", DataSourceType.Sql, titleMask, ArticleColumns);
            a[Articles] = Allowed(articles) with
            {
                Decision = TableAccessDecision.Allowed(
                    Articles,
                    new Dictionary<string, ColumnAccessLevel> { ["title"] = ColumnAccessLevel.Mask },
                    rowFilterSql: "\"author_id\" = 1",
                    hasUnconstrainedColumnAllow: true)
            };
        });

        using var result = await fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null);

        fixture.Factory.Opened.ShouldBe(1);
        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Authors, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Articles, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
        await fixture.Audit.Received(2).RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        var root = result.RootElement;
        root.GetArrayLength().ShouldBe(2);
        var firstArticles = root[0].GetProperty("articles").EnumerateArray().Select(x => x.GetProperty("title").GetString()).ToList();
        firstArticles.Count.ShouldBe(2); // row filter keeps the articles of author 1
        firstArticles.ShouldNotContain("A1"); // mask of the child table applies
        firstArticles.ShouldNotContain("A2");
        root[1].GetProperty("articles").GetArrayLength().ShouldBe(0); // row filter removes the articles of author 2
    }

    private static readonly TableIdentifier CrmArticles = new("crm", "main", "articles");

    private static TreeQueryNode AuthorsWithCrmArticles() => new(Authors, ["id"])
    {
        Relations = [new TreeRelationNode("articles", ["id"], ["author_id"], true, new TreeQueryNode(CrmArticles, ["title"]) { OrderBy = [new TreeOrder("id")] })],
        OrderBy = [new TreeOrder("id")]
    };

    [Fact]
    public async Task Tree_CrossDomainSameSource_DeniedChild_FailsBeforeAnySql()
    {
        var fixture = Create(a =>
        {
            var child = MetaOf(CrmArticles, "blog", "Sqlite", DataSourceType.Sql, null, ArticleColumns);
            a[CrmArticles] = Allowed(child) with { Decision = TableAccessDecision.Denied(CrmArticles, "no consent") };
        });

        var ex = await Should.ThrowAsync<GatewayForbiddenException>(() => fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithCrmArticles(), null));

        ex.Message.ShouldBe("Access denied.");
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_CrossDomainSameSource_AppliesRowFilterAndMaskOfTheChild()
    {
        var titleMask = new Dictionary<string, MaskingRule> { ["title"] = new MaskingRule { RuleType = "REDACT" } };
        var fixture = Create(a =>
        {
            var child = MetaOf(CrmArticles, "blog", "Sqlite", DataSourceType.Sql, titleMask, ArticleColumns);
            a[CrmArticles] = Allowed(child) with
            {
                Decision = TableAccessDecision.Allowed(
                    CrmArticles,
                    new Dictionary<string, ColumnAccessLevel> { ["title"] = ColumnAccessLevel.Mask },
                    rowFilterSql: "\"author_id\" = 1",
                    hasUnconstrainedColumnAllow: true)
            };
        });

        using var result = await fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithCrmArticles(), null);

        fixture.Factory.Opened.ShouldBe(1);
        var root = result.RootElement;
        var titles = root[0].GetProperty("articles").EnumerateArray().Select(x => x.GetProperty("title").GetString()).ToList();
        titles.Count.ShouldBe(2);
        titles.ShouldNotContain("A1");
        titles.ShouldNotContain("A2");
        root[1].GetProperty("articles").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void GovernedTreeQueryService_DoesNotDependOnTheUnwiredCrossSourceResolver()
    {
        // SEC-B75-05: nothing on the GraphQL or tree path may resolve the unwired resolver and its in-memory reader.
        var forbidden = new[]
        {
            typeof(Autheris.Application.Data.Relations.ICrossSourceRelationResolver),
            typeof(Autheris.Application.Data.Relations.ICrossSourceRowReader),
            typeof(Autheris.Application.Data.Relations.CrossSourceRelationResolver),
            typeof(Autheris.Application.Data.Relations.InMemoryCrossSourceRowReader)
        };
        var assemblies = new[] { typeof(GovernedTreeQueryService).Assembly, typeof(CatalogSchemaModel).Assembly };
        var offenders = assemblies
            .SelectMany(asm => asm.GetTypes())
            .Where(t => t.Namespace is { } ns && (ns.StartsWith("Autheris.GraphQL", StringComparison.Ordinal) || ns.StartsWith("Autheris.Application.Sql.Tree", StringComparison.Ordinal)))
            .Where(t => t.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Any(p => forbidden.Contains(p.ParameterType)))
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public async Task HmacColumns_ArePseudonymizedInTheGateway_WithTenantScopedKey()
    {
        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>())
            .Returns(ci => "H(" + ci.ArgAt<object?>(1) + ")");
        var fixture = Create(a =>
        {
            var meta = a[Authors].Metadata;
            a[Authors] = a[Authors] with
            {
                Metadata = new TableMetadata
                {
                    Identifier = meta.Identifier,
                    Table = meta.Table,
                    Columns = meta.Columns,
                    PrimaryKeyColumns = meta.PrimaryKeyColumns,
                    ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "HMAC_SHA256" } }
                }
            };
        }, masking: masking);
        var tree = new TreeQueryNode(Articles, ["id"])
        {
            Relations = [new TreeRelationNode("author", ["author_id"], ["id"], false, new TreeQueryNode(Authors, ["email"]))],
            OrderBy = [new TreeOrder("id")],
            Limit = 1
        };

        using var result = await fixture.ExecuteAsync(new ClaimsPrincipal(), tree, null);

        result.RootElement[0].GetProperty("author").GetProperty("email").GetString().ShouldBe("H(ann@example.com)");
        masking.Received().MaskValue("email", Arg.Any<object?>(), Arg.Is<MaskingRule>(r => r.HmacKeyId != null && r.HmacKeyId.Contains("tenant:t1")));
    }

    [Fact]
    public async Task ResponseAboveByteLimit_IsRejected()
    {
        var fixture = Create(options: new GatewayOptions { GraphQL = new GraphQLOptions { MaxResponseBytes = 16 } });

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null));

        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
    }

    [Fact]
    public async Task RootPageAboveMaxResponseRows_IsRejected()
    {
        var fixture = Create(options: new GatewayOptions { GraphQL = new GraphQLOptions { MaxResponseRows = 10 } });

        await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            fixture.ExecuteAsync(new ClaimsPrincipal(), new TreeQueryNode(Authors, ["id"]) { Limit = 11 }, null));
    }

    [Fact]
    public async Task ConcurrencyGateFull_Throttles()
    {
        var gate = Substitute.For<ITableReadConcurrencyGate>();
        gate.TryEnter(Arg.Any<string>(), Arg.Any<int>()).Returns((IDisposable?)null);
        var fixture = Create(options: new GatewayOptions { DataSources = new SqlDataSourceOptions { MaxConcurrentReadsPerUserAndTable = 1 } }, gate: gate);

        await Should.ThrowAsync<GatewayThrottledException>(() => fixture.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null));
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task CatalogDialectDifferentFromProvider_FailsClosed()
    {
        var fixture = Create(a =>
        {
            var meta = a[Authors].Metadata;
            a[Authors] = a[Authors] with
            {
                Metadata = new TableMetadata
                {
                    Identifier = meta.Identifier,
                    Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "authors", SourceType = "SqlServer" },
                    Columns = meta.Columns,
                    PrimaryKeyColumns = meta.PrimaryKeyColumns
                }
            };
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            fixture.ExecuteAsync(new ClaimsPrincipal(), new TreeQueryNode(Authors, ["id"]), null));
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task NegativeOffset_ThrowsGatewayInvalidQueryException()
    {
        var fixture = Create();

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            fixture.ExecuteAsync(new ClaimsPrincipal(), new TreeQueryNode(Authors, ["id"]) { Offset = -1 }, null));

        ex.Message.ShouldContain("negative");
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task OffsetAboveMaxAllowed_ThrowsGatewayInvalidQueryException()
    {
        var fixture = Create(options: new GatewayOptions { GraphQL = new GraphQLOptions { MaxAllowedOffset = 50 } });

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            fixture.ExecuteAsync(new ClaimsPrincipal(), new TreeQueryNode(Authors, ["id"]) { Offset = 51 }, null));

        ex.Message.ShouldContain("offset cannot exceed 50");
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task AggregateRowBudget_Exceeded_ThrowsGatewayInvalidQueryException()
    {
        var fixture = Create(options: new GatewayOptions { GraphQL = new GraphQLOptions { MaxAggregateRowBudget = 100 } });
        // Root limit 20, relation limit 10 => 20 + 20 * 10 = 220 > 100
        var tree = new TreeQueryNode(Authors, ["id"])
        {
            Limit = 20,
            Relations = [new TreeRelationNode("articles", ["id"], ["author_id"], true, new TreeQueryNode(Articles, ["id"]) { Limit = 10 })]
        };

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            fixture.ExecuteAsync(new ClaimsPrincipal(), tree, null));

        ex.Message.ShouldContain("aggregate row budget");
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task EmptyOrNullOperationId_ThrowsArgumentException()
    {
        var fixture = Create();
        var tree = new TreeQueryNode(Authors, ["id"]);

        await Should.ThrowAsync<ArgumentException>(() =>
            fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null, ""));
        await Should.ThrowAsync<ArgumentException>(() =>
            fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null, "   "));
    }

    [Fact]
    public async Task ClearOperation_RemovesMemoAndAudit_AllowingFreshReEvaluation()
    {
        var fixture = Create();
        var tree = new TreeQueryNode(Authors, ["id"]);
        var opId = "op-clear-test";

        await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null, opId);
        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Authors, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());

        fixture.Service.ClearOperation(opId);

        await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null, opId);
        await fixture.Resolver.Received(2).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Authors, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentOperationIds_DoNotShareTableAccessMemo()
    {
        var fixture = Create();
        var tree = new TreeQueryNode(Authors, ["id"]);

        await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null, "op-A");
        await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null, "op-B");

        await fixture.Resolver.Received(2).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Authors, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameOperationId_DifferentPrincipals_DoNotShareTableAccessMemo_ResolvesIndependently()
    {
        var fixture = Create();
        var tree = new TreeQueryNode(Authors, ["id"]);
        var sameOpId = "shared-op-id";

        var userA = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("objectSid", "S-1-5-21-USER-A"),
            new Claim("tenant_id", "tenant-1")
        }, "TestAuth"));

        var userB = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("objectSid", "S-1-5-21-USER-B"),
            new Claim("tenant_id", "tenant-1")
        }, "TestAuth"));

        await fixture.Service.ExecuteAsync(userA, tree, null, sameOpId);
        await fixture.Service.ExecuteAsync(userB, tree, null, sameOpId);

        // SQL2-13: Both users must have their table access resolved independently, never sharing a cached decision
        await fixture.Resolver.Received(2).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Authors, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameOperationId_DifferentTenants_AuditsSeparatelyPerTenantAndUser()
    {
        var fixture = Create();
        var tree = new TreeQueryNode(Authors, ["id"]);
        var sameOpId = "shared-op-id-tenants";

        var tenant1User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("objectSid", "S-1-5-21-TENANT1-USER"),
            new Claim("tenant_id", "tenant-alpha")
        }, "TestAuth"));

        var tenant2User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("objectSid", "S-1-5-21-TENANT2-USER"),
            new Claim("tenant_id", "tenant-beta")
        }, "TestAuth"));

        await fixture.Service.ExecuteAsync(tenant1User, tree, null, sameOpId);
        await fixture.Service.ExecuteAsync(tenant2User, tree, null, sameOpId);

        // SQL2-13: Auditing must record both tenants separately, never suppressing the second tenant
        await fixture.Audit.Received(2).RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- Issue #151 WP-151-5: composite-key relations ----

    private static readonly TableIdentifier Orders = new("blog", "main", "orders");
    private static readonly TableIdentifier Lines = new("blog", "main", "lines");
    private static readonly (string, string)[] OrderColumns = [("id", "integer"), ("k1", "integer"), ("k2", "integer"), ("k3", "integer"), ("name", "text")];
    private static readonly (string, string)[] LineColumns = [("id", "integer"), ("p1", "integer"), ("p2", "integer"), ("p3", "integer"), ("label", "text")];

    private void SeedComposite()
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE orders (id INTEGER PRIMARY KEY, k1 INTEGER, k2 INTEGER, k3 INTEGER, name TEXT);
            CREATE TABLE lines (id INTEGER PRIMARY KEY, p1 INTEGER, p2 INTEGER, p3 INTEGER, label TEXT);
            INSERT INTO orders VALUES (1, 1, 1, 1, 'o1'), (2, 1, 1, 2, 'o2'), (3, 1, 2, 1, 'o3');
            INSERT INTO lines VALUES (10, 1, 1, 1, 'L10'), (11, 1, 1, 2, 'L11'), (12, 1, 2, 1, 'L12'), (13, 1, 2, 2, 'L13'), (14, 9, 9, 9, 'L14');
            """;
        cmd.ExecuteNonQuery();
    }

    private Fixture CreateComposite(
        Dictionary<string, ColumnAccessLevel>? orderAccess = null,
        Dictionary<string, ColumnAccessLevel>? lineAccess = null,
        string? orderFilter = null,
        string? lineFilter = null)
    {
        SeedComposite();
        return Create(a =>
        {
            var orders = Meta(Orders, "blog", OrderColumns);
            var lines = Meta(Lines, "blog", LineColumns);
            a[Orders] = new ResolvedTableAccess(orders,
                TableAccessDecision.Allowed(Orders, orderAccess ?? new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: orderFilter, hasUnconstrainedColumnAllow: true),
                new TenantId("t1"), new Sid("S-1-5-21-DAVID"), new ClaimsPrincipal());
            a[Lines] = new ResolvedTableAccess(lines,
                TableAccessDecision.Allowed(Lines, lineAccess ?? new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: lineFilter, hasUnconstrainedColumnAllow: true),
                new TenantId("t1"), new Sid("S-1-5-21-DAVID"), new ClaimsPrincipal());
        });
    }

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

    [Fact]
    public async Task CompositeRelation_JoinsOnAllColumns_BothDirections()
    {
        var fixture = CreateComposite();

        using var down = await fixture.ExecuteAsync(new ClaimsPrincipal(), OrdersWithLines(3), null);
        Labels(down.RootElement[0]).ShouldBe(["L10"]);
        Labels(down.RootElement[1]).ShouldBe(["L11"]);
        Labels(down.RootElement[2]).ShouldBe(["L12"]);

        using var up = await fixture.ExecuteAsync(new ClaimsPrincipal(), LinesWithOrder(), null);
        up.RootElement[0].GetProperty("order").GetProperty("name").GetString().ShouldBe("o1");
        up.RootElement[1].GetProperty("order").GetProperty("name").GetString().ShouldBe("o2");
        up.RootElement[2].GetProperty("order").GetProperty("name").GetString().ShouldBe("o3");
        up.RootElement[3].GetProperty("order").ValueKind.ShouldBe(JsonValueKind.Null); // (1,2,2) has no parent
        up.RootElement[4].GetProperty("order").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CompositeRelation_FirstKeyMatchOnly_ReturnsNoChildRows()
    {
        // SEC-REL151-01 / T-REL151-01: lines matching the parent on key 1 only must never be returned.
        var fixture = CreateComposite();
        using (var cmd = _keepAlive.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO orders VALUES (4, 5, 5, 5, 'o4'); INSERT INTO lines VALUES (15, 5, 6, 6, 'L15'), (16, 5, 5, 6, 'L16');";
            cmd.ExecuteNonQuery();
        }

        using var result = await fixture.ExecuteAsync(new ClaimsPrincipal(), OrdersWithLines(3), null);

        Labels(result.RootElement[3]).ShouldBeEmpty();
    }

    [Fact]
    public async Task CompositeRelation_RowFilterAppliedPerLevel()
    {
        var fixture = CreateComposite(orderFilter: "\"name\" <> 'o3'", lineFilter: "\"label\" <> 'L10'");

        using var result = await fixture.ExecuteAsync(new ClaimsPrincipal(), OrdersWithLines(3), null);

        result.RootElement.GetArrayLength().ShouldBe(2); // parent filter removed o3
        Labels(result.RootElement[0]).ShouldBeEmpty(); // child filter removed L10
        Labels(result.RootElement[1]).ShouldBe(["L11"]);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(1, true, ColumnAccessLevel.Deny)]
    [InlineData(2, true, ColumnAccessLevel.Deny)]
    [InlineData(3, true, ColumnAccessLevel.Deny)]
    [InlineData(1, true, ColumnAccessLevel.Mask)]
    [InlineData(2, true, ColumnAccessLevel.Mask)]
    [InlineData(3, true, ColumnAccessLevel.Mask)]
    [InlineData(1, false, ColumnAccessLevel.Deny)]
    [InlineData(2, false, ColumnAccessLevel.Deny)]
    [InlineData(3, false, ColumnAccessLevel.Deny)]
    [InlineData(1, false, ColumnAccessLevel.Mask)]
    [InlineData(2, false, ColumnAccessLevel.Mask)]
    [InlineData(3, false, ColumnAccessLevel.Mask)]
    public async Task CompositeRelation_AnyKeyPositionMasked_FailsClosed(int position, bool parentSide, ColumnAccessLevel level)
    {
        // SEC-REL151-11 / T-REL151-02: a masked or denied key column at any position, on either side, fails the whole relation.
        var restricted = new Dictionary<string, ColumnAccessLevel> { [(parentSide ? "k" : "p") + position] = level };
        var fixture = CreateComposite(
            orderAccess: parentSide ? restricted : null,
            lineAccess: parentSide ? null : restricted);

        await Should.ThrowAsync<GatewayInvalidQueryException>(() => fixture.ExecuteAsync(new ClaimsPrincipal(), OrdersWithLines(3), null));
        fixture.Factory.Opened.ShouldBe(0);
    }
}
