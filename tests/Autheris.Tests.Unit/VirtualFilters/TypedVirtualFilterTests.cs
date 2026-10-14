namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0g (D3): virtual filter predicates as typed <see cref="PolicyPredicate"/> values. Every key value is a bound policy
/// parameter, the correlated EXISTS is built as AST (no engine instance, no string replacement), and the legacy string path stays.
/// </summary>
public sealed class TypedVirtualFilterTests
{
    private static readonly PolicyPredicateRenderer Renderer = new();
    private static readonly ISqlEngine Engine = new FastSqlEngine();

    private const string DavidLastDay = """
        from conf.client client
        join md.crane crane on crane.serial_number = client.crane_serial_number
        where crane.is_delivered is null
          and target.client_id = client.client_id
          and target.ts between date_add('day', -1, current_timestamp) and current_timestamp
        """;

    private static TableMetadata Catalogued(string schema, string table, DatabaseDialect dialect = DatabaseDialect.SqlServer, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = dialect.ToString(), IsActive = true },
        Columns = (columns.Length == 0 ? ["id"] : columns).Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static VirtualFilter SqlFilter(string sql) => new()
    {
        TenantId = new TenantId("tenant_lwe"),
        Name = "typed_sql_filter",
        Source = "lwetem_prod",
        Sql = sql
    };

    private static VirtualFilter Validated(string sql, DatabaseDialect dialect)
    {
        var catalog = new List<TableMetadata> { Catalogued("conf", "client", dialect), Catalogued("md", "crane", dialect) };
        return SqlFilterCompiler.Validate(SqlFilter(sql), catalog);
    }

    private static RenderedPolicyPredicate Render(PolicyPredicate predicate, DatabaseDialect dialect) =>
        Renderer.Render(predicate, PolicyParameterNamer.ToTargetDialect(dialect), RowFilterAliases.Target);

    // ------------------------------------------------------------------ SQL-defined filters

    [Fact]
    public void CompileTyped_ReturnsExistsCorrelatedToAutherisTarget()
    {
        var filter = Validated(DavidLastDay, DatabaseDialect.SqlServer);

        var typed = SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.SqlServer, Engine);
        var rendered = Render(typed, DatabaseDialect.SqlServer);

        rendered.Sql.ShouldStartWith("EXISTS (SELECT 1 FROM [conf].[client] AS [client] INNER JOIN [md].[crane] AS [crane]");
        rendered.Sql.ShouldContain("[autheris_target].[client_id] = [client].[client_id]");
        rendered.Sql.ShouldContain("[autheris_target].[ts] BETWEEN DATEADD(day, -1, SYSDATETIMEOFFSET()) AND SYSDATETIMEOFFSET()");
        rendered.Sql.ShouldNotContain("[target]");
        typed.ReferencedColumns.ShouldBe(["client_id", "ts"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.Sqlite)]
    public void CompileTyped_RendersLikeTheLegacyString_WhenTheDefinitionHasNoLiterals(DatabaseDialect dialect)
    {
        var filter = Validated(DavidLastDay, dialect);

        var typed = Render(SqlFilterCompiler.CompileTyped(filter, dialect, Engine), dialect);

        typed.Sql.ShouldBe(SqlFilterCompiler.Compile(filter, dialect));
        typed.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void CompileTyped_Literals_AreBoundParameters_NeverText()
    {
        const string hostile = "O'Brien'; DROP TABLE x";
        var sql = $"""
            from conf.client client
            where target.client_id = client.client_id
              and client.name = '{hostile.Replace("'", "''")}'
              and client.level >= 3
              and client.kind in ('a', 'b')
              and client.code like 'AB%'
            """;
        var filter = Validated(sql, DatabaseDialect.PostgreSql);

        var typed = SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.PostgreSql, Engine);
        var rendered = Render(typed, DatabaseDialect.PostgreSql);

        rendered.Sql.ShouldNotContain("Brien");
        rendered.Sql.ShouldNotContain("DROP");
        rendered.Sql.ShouldNotContain("'a'");
        rendered.Sql.ShouldNotContain("'AB%'");
        rendered.Parameters.Values.ShouldContain(hostile);
        rendered.Parameters.Values.ShouldContain("a");
        rendered.Parameters.Values.ShouldContain("b");
        rendered.Parameters.Values.ShouldContain("AB%");
        rendered.Parameters.Values.ShouldContain(3L);
        rendered.Parameters.Count.ShouldBe(5);
        typed.Parameters.Keys.ShouldAllBe(k => k.StartsWith("__gql_vf_", StringComparison.Ordinal));
    }

    [Fact]
    public void CompileTyped_TimeFunctionsStayFunctions_NoValueIsBakedIn()
    {
        var filter = Validated(DavidLastDay, DatabaseDialect.PostgreSql);

        var first = Render(SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.PostgreSql, Engine), DatabaseDialect.PostgreSql);
        var second = Render(SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.PostgreSql, Engine), DatabaseDialect.PostgreSql);

        second.Sql.ShouldBe(first.Sql);
        first.Sql.ShouldContain("CURRENT_TIMESTAMP");
    }

    [Fact]
    public void CompileTyped_ParameterPrefix_IsHonoured()
    {
        var filter = Validated("from conf.client client where target.client_id = client.client_id and client.name = 'x'", DatabaseDialect.PostgreSql);

        var typed = SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.PostgreSql, Engine, "__gql_vf3_");

        typed.Parameters.Keys.ShouldBe(["__gql_vf3_0"]);
    }

    [Fact]
    public void CompileTyped_WithoutSql_Throws()
    {
        Should.Throw<InvalidOperationException>(() =>
            SqlFilterCompiler.CompileTyped(VirtualFilterModelTests.DavidFilter(), DatabaseDialect.SqlServer, Engine));
    }

    [Fact]
    public void CompileTyped_ExecutedOnSqlite_FiltersWithTheBoundParameter()
    {
        var filter = Validated("from conf.client client where target.client_id = client.client_id and client.region = 'EU' and client.level >= 2", DatabaseDialect.Sqlite);
        var rendered = Render(SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.Sqlite, Engine), DatabaseDialect.Sqlite);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        // The SQLite generator keeps the schema; an attached database named conf carries the table.
        Exec(connection, "ATTACH DATABASE ':memory:' AS conf");
        Exec(connection, "CREATE TABLE conf.client (client_id INTEGER, region TEXT, level INTEGER)");
        Exec(connection, "CREATE TABLE fact (id INTEGER, client_id INTEGER)");
        Exec(connection, "INSERT INTO conf.client VALUES (1, 'EU', 3), (2, 'US', 3), (3, 'EU', 1)");
        Exec(connection, "INSERT INTO fact VALUES (10, 1), (20, 2), (30, 3)");

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM fact AS autheris_target WHERE {rendered.Sql} ORDER BY id";
        foreach (var (name, value) in rendered.Parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var ids = new List<long>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        ids.ShouldBe([10L]);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ structured filters

    private static readonly FilterBinding Binding = new() { FilterName = "nicht_ausgelieferte_krane" };

    private static TableMetadata Air1(DatabaseDialect dialect) =>
        Catalogued("fms", "air1", dialect, "id", "client_id", "ts");

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.Sqlite)]
    public void Structured_BuildTyped_RendersLikeTheLegacyExists(DatabaseDialect dialect)
    {
        var builder = new StructuredFilterSqlBuilder();
        var filter = VirtualFilterModelTests.DavidFilter();

        var typed = builder.BuildTyped(filter, Binding, Air1(dialect), dialect, "__gql_vf_");

        typed.ShouldNotBeNull();
        Render(typed, dialect).Sql.ShouldBe(builder.Build(filter, Binding, Air1(dialect), dialect));
    }

    [Fact]
    public void Structured_BuildTyped_ConditionValues_AreBound_NumbersStayNumbers()
    {
        var filter = VirtualFilterModelTests.DavidFilter() with
        {
            Structured = VirtualFilterModelTests.DavidFilter().Structured! with
            {
                Where =
                [
                    new FilterCondition("crane.region", FilterConditionOperator.Eq, "EU'; DROP TABLE x"),
                    new FilterCondition("crane.level", FilterConditionOperator.NotEq, "7"),
                    new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull)
                ]
            }
        };

        var typed = new StructuredFilterSqlBuilder().BuildTyped(filter, Binding, Air1(DatabaseDialect.PostgreSql), DatabaseDialect.PostgreSql, "__gql_vf_");

        typed.ShouldNotBeNull();
        var rendered = Render(typed, DatabaseDialect.PostgreSql);
        rendered.Sql.ShouldNotContain("EU");
        rendered.Sql.ShouldNotContain("DROP");
        rendered.Parameters.Values.ShouldContain("EU'; DROP TABLE x");
        rendered.Parameters.Values.ShouldContain(7L);
        rendered.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Structured_BuildTyped_WithColumnMap_RenamesTheTargetKey()
    {
        var typed = new StructuredFilterSqlBuilder().BuildTyped(
            VirtualFilterModelTests.DavidFilter(),
            new FilterBinding { FilterName = "nicht_ausgelieferte_krane", ColumnMap = new Dictionary<string, string> { ["client_id"] = "cid" } },
            Catalogued("fms", "air1", DatabaseDialect.PostgreSql, "id", "cid"),
            DatabaseDialect.PostgreSql, "__gql_vf_");

        typed.ShouldNotBeNull();
        Render(typed, DatabaseDialect.PostgreSql).Sql.ShouldContain("\"client\".\"client_id\" = \"autheris_target\".\"cid\"");
    }

    [Fact]
    public void Structured_BuildTyped_InvalidIdentifier_Throws()
    {
        var filter = VirtualFilterModelTests.DavidFilter() with { KeyColumns = ["client.client_id\"; DROP"] };

        Should.Throw<Exception>(() => new StructuredFilterSqlBuilder().BuildTyped(filter, Binding, Air1(DatabaseDialect.PostgreSql), DatabaseDialect.PostgreSql, "__gql_vf_"));
    }

    // ------------------------------------------------------------------ resolver

    private static readonly TenantId Tenant = MandatoryRowFilterResolverTests.Tenant;

    private static VirtualFilter KeyedFilter(string name, string value) => VirtualFilterModelTests.DavidFilter(name) with
    {
        Structured = VirtualFilterModelTests.DavidFilter().Structured! with
        {
            Where = [new FilterCondition("crane.region", FilterConditionOperator.Eq, value)]
        }
    };

    private static MandatoryRowFilterResolver Resolver(IVirtualFilterPredicateBuilder predicates, params VirtualFilter[] filters)
    {
        var profile = MandatoryRowFilterResolverTests.Profile(UncoveredPolicy.Deny,
            filters.Select(f => new FilterBinding { FilterName = f.Name }).ToArray());
        var snapshot = new VirtualFilterSnapshot(1, filters.ToList(), [profile]);
        return new MandatoryRowFilterResolver(new MandatoryRowFilterResolverTests.FixedSnapshot(snapshot), predicates);
    }

    private static MandatoryFilterQuery Query(TableMetadata table) =>
        new(MandatoryRowFilterResolverTests.David, new HashSet<Sid>(), new HashSet<string>(), Tenant, table);

    [Fact]
    public async Task Resolver_TwoFilters_YieldOneTypedPredicate_WithDistinctParameterNames()
    {
        var resolver = Resolver(new StructuredFilterSqlBuilder(), KeyedFilter("filter_a", "EU"), KeyedFilter("filter_b", "US"));

        var outcome = await resolver.ResolveAsync(Query(Air1(DatabaseDialect.PostgreSql)));

        outcome.IsDenied.ShouldBeFalse();
        outcome.PredicateSql.ShouldNotBeNull();   // legacy text stays
        outcome.TypedPredicate.ShouldNotBeNull();
        outcome.TypedPredicate!.Parameters.Count.ShouldBe(2);
        outcome.TypedPredicate.Parameters.Values.Select(v => v.Value).ShouldBe(["EU", "US"], ignoreOrder: true);
        var rendered = Render(outcome.TypedPredicate, DatabaseDialect.PostgreSql);
        rendered.Sql.ShouldNotContain("EU");
        rendered.Sql.ShouldNotContain("US");
    }

    [Fact]
    public async Task Resolver_IdenticalFilters_AreDeduplicatedLikeTheLegacyText()
    {
        var resolver = Resolver(new StructuredFilterSqlBuilder(), KeyedFilter("filter_a", "EU"), KeyedFilter("filter_b", "EU"));

        var outcome = await resolver.ResolveAsync(Query(Air1(DatabaseDialect.PostgreSql)));

        outcome.TypedPredicate.ShouldNotBeNull();
        outcome.TypedPredicate!.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Resolver_BuilderWithoutTypedSupport_LeavesTheTypedPredicateNull()
    {
        var resolver = Resolver(new MandatoryRowFilterResolverTests.PlaceholderPredicates(), MandatoryRowFilterResolverTests.Filter("filter_a", "client_id"));

        var outcome = await resolver.ResolveAsync(Query(MandatoryRowFilterResolverTests.Table("fms", "air1", "client_id")));

        outcome.PredicateSql.ShouldBe("P_filter_a(client_id)");
        outcome.TypedPredicate.ShouldBeNull();
    }

    [Fact]
    public async Task Resolver_TypedBuildFailure_Denies_FailClosed()
    {
        var builder = Substitute.For<IVirtualFilterPredicateBuilder>();
        builder.Build(default!, default!, default!, default).ReturnsForAnyArgs("P");
        builder.BuildTyped(default!, default!, default!, default, default!).ReturnsForAnyArgs(_ => throw new InvalidOperationException("boom"));
        var resolver = Resolver(builder, MandatoryRowFilterResolverTests.Filter("filter_a", "client_id"));

        var outcome = await resolver.ResolveAsync(Query(MandatoryRowFilterResolverTests.Table("fms", "air1", "client_id")));

        outcome.IsDenied.ShouldBeTrue();
        outcome.DenyReason!.ShouldContain(VirtualFilterErrorCodes.BindingMisconfigured);
    }

    // ------------------------------------------------------------------ decision

    private static TableAccessPolicy Policy(IMandatoryRowFilterResolver resolver, RowPolicyFactory? factory) =>
        new(
            Substitute.For<IConsentRepository>(),
            Substitute.For<IConsentResolutionService>(),
            Substitute.For<IConsentCacheService>(),
            Substitute.For<IPolicyEnforcementService>(),
            Substitute.For<IRebacEvaluator>(),
            Substitute.For<IClientIpResolver>(),
            new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true } },
            resolver,
            rowPolicyFactory: factory);

    private static async Task<TableAccessDecision> DecideAsync(RowPolicyFactory? factory)
    {
        var table = Air1(DatabaseDialect.PostgreSql);
        var resolver = Resolver(new StructuredFilterSqlBuilder(), KeyedFilter("filter_a", "EU"));
        var query = new TableAccessQuery(
            MandatoryRowFilterResolverTests.David, Tenant, new HashSet<Sid>(), new HashSet<string>(), table);

        return await Policy(resolver, factory).DecideAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task VfRestrictedDecision_WithFactory_PassesRequireTypedRowPolicy()
    {
        var decision = await DecideAsync(new RowPolicyFactory(Renderer));

        decision.MandatoryRowPolicy.ShouldNotBeNull();
        decision.RowPolicy.ShouldNotBeNull();
        decision.RowFilterParameters!.Values.ShouldContain("EU");
        decision.MandatoryRowPredicateSql!.ShouldNotContain("EU");

        var typed = decision.RequireTypedRowPolicy();

        typed.IsAllowed.ShouldBeTrue();
        typed.MandatoryRowPredicateSql.ShouldBe(decision.MandatoryRowPredicateSql);
    }

    [Fact]
    public async Task VfRestrictedDecision_WithoutFactory_StaysOnTheLegacyStringPath()
    {
        var decision = await DecideAsync(null);

        decision.MandatoryRowPolicy.ShouldBeNull();
        decision.MandatoryRowPredicateSql.ShouldNotBeNullOrWhiteSpace();
        decision.MandatoryRowPredicateSql!.ShouldContain("'EU'");
        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task TypedVfDecision_EnforcedAtStaging_ResidualIsTyped()
    {
        var decision = await DecideAsync(new RowPolicyFactory(Renderer));
        var applied = decision.AppliedVirtualFilterBindings!;

        var residual = decision.WithoutEnforcedVirtualFilters(applied);

        residual.IsAllowed.ShouldBeTrue();
        residual.MandatoryRowPredicateSql.ShouldBeNull();
        residual.RequireTypedRowPolicy().IsAllowed.ShouldBeTrue();
    }
}
