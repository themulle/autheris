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

        using var result = await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null);

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

        await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null);
        await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null); // same request scope: memoized

        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Authors, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Articles, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneAuditEntryPerTableAndRequest()
    {
        var fixture = Create();

        await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null);

        await fixture.Audit.Received(2).RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        await fixture.Audit.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.TargetTable == Articles.ToString() && e.Decision == "ALLOW"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeniedTable_FailsBeforeAnyDatabaseAccess_AndIsAudited()
    {
        var fixture = Create(a => a[Articles] = a[Articles] with { Decision = TableAccessDecision.Denied(Articles, "no consent") });

        await Should.ThrowAsync<GatewayForbiddenException>(() => fixture.Service.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null));

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

        await Should.ThrowAsync<GatewayInvalidQueryException>(() => fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null));
        fixture.Factory.Opened.ShouldBe(0);
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

        using var result = await fixture.Service.ExecuteAsync(new ClaimsPrincipal(), tree, null);

        result.RootElement[0].GetProperty("author").GetProperty("email").GetString().ShouldBe("H(ann@example.com)");
        masking.Received().MaskValue("email", Arg.Any<object?>(), Arg.Is<MaskingRule>(r => r.HmacKeyId != null && r.HmacKeyId.Contains("tenant:t1")));
    }

    [Fact]
    public async Task ResponseAboveByteLimit_IsRejected()
    {
        var fixture = Create(options: new GatewayOptions { GraphQL = new GraphQLOptions { MaxResponseBytes = 16 } });

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => fixture.Service.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null));

        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
    }

    [Fact]
    public async Task RootPageAboveMaxResponseRows_IsRejected()
    {
        var fixture = Create(options: new GatewayOptions { GraphQL = new GraphQLOptions { MaxResponseRows = 10 } });

        await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            fixture.Service.ExecuteAsync(new ClaimsPrincipal(), new TreeQueryNode(Authors, ["id"]) { Limit = 11 }, null));
    }

    [Fact]
    public async Task ConcurrencyGateFull_Throttles()
    {
        var gate = Substitute.For<ITableReadConcurrencyGate>();
        gate.TryEnter(Arg.Any<string>(), Arg.Any<int>()).Returns((IDisposable?)null);
        var fixture = Create(options: new GatewayOptions { DataSources = new SqlDataSourceOptions { MaxConcurrentReadsPerUserAndTable = 1 } }, gate: gate);

        await Should.ThrowAsync<GatewayThrottledException>(() => fixture.Service.ExecuteAsync(new ClaimsPrincipal(), AuthorsWithArticles(), null));
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
            fixture.Service.ExecuteAsync(new ClaimsPrincipal(), new TreeQueryNode(Authors, ["id"]), null));
        fixture.Factory.Opened.ShouldBe(0);
    }
}
