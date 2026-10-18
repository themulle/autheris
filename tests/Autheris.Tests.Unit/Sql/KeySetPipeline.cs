namespace Autheris.Tests.Unit.Sql;

using System.Security;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Sql.Policy;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>The governed pipeline over fakes for the key load (shared by the rewriter and diagnostics tests of issue #107).</summary>
    internal sealed class KeySetPipeline : IDisposable
    {
        public GovernedSqlRewriter Rewriter { get; set; } = null!;
        public required FakeKeySetLoader Loader { get; init; }
        public required FakeEpochs Epochs { get; init; }
        public required RowFilterKeySetCache Cache { get; init; }
        public required List<(CompileRequest Request, CancellationToken Token)> Compiles { get; init; }
        public required List<(LogLevel Level, string Message)> Logs { get; init; }
        public Func<int, Exception?> Failure { get; set; } = _ => null;

    public void Dispose() => Cache.Dispose();

    private static TableMetadata Meta(string name, params (string Column, string Type)[] columns) => new()
    {
        Identifier = new TableIdentifier("default", "public", name),
        Table = new Table { TableName = name, SchemaName = "public", SourceType = "PostgreSQL", SourceName = "default", IsActive = true },
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Column, DataType = c.Type }).ToList()
    };

    private static readonly TableIdentifier OrdersId = new("default", "public", "orders");

    public static KeySetPipeline Create(bool optIn = true, int keyCount = 3, int maxKeys = 2000, bool wireStage = true, string correlatedFk = "client_id", bool logSql = false)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var logs = new List<(LogLevel, string)>();
        var gateway = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" },
            Logging = new LoggingOptions { LogGeneratedSql = logSql },
            RowFilters = new RowFilterOptions
            {
                KeySetCache = new RowFilterKeySetCacheOptions
                {
                    DefaultStrategy = optIn ? RowFilterEvaluationStrategy.KeySetCache : RowFilterEvaluationStrategy.Exists,
                    MaxKeys = maxKeys
                }
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=x", KeySetCacheAllowed = optIn }
                }
            }
        };
        var options = Options.Create(gateway);
        var orders = Meta("orders", ("id", "bigint"), ("client_id", "bigint"), ("order_ref", "bigint"), ("tenant_id", "varchar"));
        var customers = Meta("customers", ("id", "bigint"), ("country", "varchar"), ("tenant_id", "varchar"));
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().TableName switch { "orders" => orders, "customers" => customers, _ => null }));

        var filter = new ConsentRowFilter
        {
            Id = Guid.NewGuid(),
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("default", "public", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = correlatedFk,
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };
        var predicate = new TypedRowFilterBuilder().BuildCombined(
            [new Consent { Effect = ConsentEffect.Allow, RowFilters = [filter] }], [], new TableIdentity("public", "orders"))!;
        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(new PolicyPredicateRowPolicy(predicate, TargetSqlDialect.PostgreSql, new PolicyPredicateRenderer()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(_ => decision);
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));

        var loader = new FakeKeySetLoader(time) { KeyCount = keyCount };
        var epochs = new FakeEpochs();
        var cache = new RowFilterKeySetCache(64L * 1024 * 1024, 4, time);
        var stage = new RowFilterKeySetStage(new MutableGatewayOptionsMonitor(gateway), new PolicyKeyQueryCompiler(), epochs, cache, loader);
        var fixture = new KeySetPipeline { Loader = loader, Epochs = epochs, Cache = cache, Compiles = [], Logs = logs };

        var engine = Substitute.For<ISqlEngine>();
        engine.Analyze(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<SqlTokenSecurityOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => FastSqlEngine.Default.Analyze(ci.ArgAt<ReadOnlyMemory<char>>(0), ci.ArgAt<SqlTokenSecurityOptions?>(1), ci.ArgAt<CancellationToken>(2)));
        engine.Compile(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<CompileRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                fixture.Compiles.Add((ci.Arg<CompileRequest>(), ci.Arg<CancellationToken>()));
                if (fixture.Failure(fixture.Compiles.Count) is { } failure)
                {
                    throw failure;
                }

                return FastSqlEngine.Default.Compile(ci.ArgAt<ReadOnlyMemory<char>>(0), ci.Arg<CompileRequest>(), ci.Arg<CancellationToken>());
            });

        fixture.Rewriter = new GovernedSqlRewriter(
            options, consentResolution: resolution, tableRepository: repo, consentRepository: consents, sqlEngine: engine,
            policyFactory: GovernedTestSupport.PolicyFactory(repo, options, wireStage ? stage : null),
            typedPolicies: GovernedTestSupport.TypedPolicies(), policyEnforcement: TestDoubles.NullPolicyEnforcementService.Instance,
            logger: new ListLogger<GovernedSqlRewriter>(logs));
        return fixture;
    }
}
