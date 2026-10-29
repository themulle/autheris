using System.Collections.Immutable;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>
/// Issue #107 WP-B (PLAN-GOV-107 AC-2, AC-3, AC-8; SEC-RF107-06 f, -07 d): the statements the engine generates when a correlated filter is
/// substituted by cached keys. The WP-C runtime does not exist yet, so the keys come from the real key query compiler (executed on
/// SQLite for the parity tests) and the substitution is driven directly; the factory and the stage are not involved.
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetGeneratedSqlTests
{
    private static readonly TableIdentity Air1 = new("fms", "air1");
    private static readonly TableIdentity Engine = new("md", "engine");
    private static readonly TenantBinding Tenant = new("__autheris_tenant", "acme", SqlParameterType.String);
    private readonly TypedRowFilterBuilder _builder = new();
    private static readonly FastSqlEngine Compiler = new();

    // ---- catalog and policies ----

    private static InMemoryTableCatalog Catalog(string wholeType, string text) => new(
    [
        new TableCatalogEntry(Air1, ImmutableArray.Create(
            new CatalogColumn("id", wholeType), new CatalogColumn("client_id", wholeType), new CatalogColumn("region", text), new CatalogColumn("tenant_id", text)), "tenant_id", 1),
        new TableCatalogEntry(new TableIdentity("conf", "client"), ImmutableArray.Create(
            new CatalogColumn("id", wholeType), new CatalogColumn("country", text), new CatalogColumn("tenant_id", text)), "tenant_id", 1),
        new TableCatalogEntry(new TableIdentity("md", "crane"), ImmutableArray.Create(
            new CatalogColumn("id", wholeType), new CatalogColumn("client_id", wholeType), new CatalogColumn("kind", text), new CatalogColumn("tenant_id", text)), "tenant_id", 1),
        new TableCatalogEntry(Engine, ImmutableArray.Create(
            new CatalogColumn("id", wholeType), new CatalogColumn("crane_id", wholeType), new CatalogColumn("tenant_id", text)), "tenant_id", 1)
    ], "dbo");

    /// <summary>Issue example 1: <c>fms.air1</c> through <c>conf.client</c> and the hop <c>md.crane</c>.</summary>
    private static ConsentRowFilter Example1(string kind = "crane", int group = 0) => new()
    {
        FilterGroup = group,
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("ds", "conf", "client"),
        DependentTableAlias = "c",
        ForeignKeyColumn = "client_id",
        PrimaryKeyColumn = "id",
        AdditionalHops =
        [
            new SubqueryJoinHop { Table = new TableIdentifier("ds", "md", "crane"), TableAlias = "cr", LeftJoinColumn = "c.id", RightJoinColumn = "cr.client_id" }
        ],
        SubqueryFilterPredicateJson = $"{{\"cr.kind\": \"{kind}\"}}"
    };

    /// <summary>Issue example 2: <c>md.engine</c> through <c>md.crane</c>.</summary>
    private static ConsentRowFilter Example2() => new()
    {
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("ds", "md", "crane"),
        DependentTableAlias = "cr",
        ForeignKeyColumn = "crane_id",
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = "{\"cr.kind\": \"crane\"}"
    };

    private static ConsentRowFilter Simple(string column = "region", string value = "\"US\"") => new() { ColumnName = column, Operator = "EQ", ValueJson = value };

    private static Consent Allow(params ConsentRowFilter[] filters) => new() { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Allow, RowFilters = filters };

    private static Consent Deny(params ConsentRowFilter[] filters) => new() { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Deny, RowFilters = filters };

    private PolicyPredicate Policy(Consent[] allow, Consent[]? deny = null, TableIdentity? table = null) =>
        _builder.BuildCombined(allow, deny ?? [], table ?? Air1)!;

    private static PolicyKeyQueryRequest KeyRequest(ExistsExpression exists, PolicyPredicate predicate, ITableCatalog catalog, TableIdentity target, TargetSqlDialect dialect, int limit = 2001) =>
        new(exists, predicate.Parameters, catalog, catalog.Resolve(target.ToQualifiedName())!, Tenant, dialect, limit);

    private sealed class Policies(Dictionary<TableIdentity, PolicyPredicate> predicates) : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => predicates.ContainsKey(table);

        public PolicyPredicate GetPredicate(TableIdentity table) => predicates[table];

        public bool HasMask(TableIdentity table, string column) => false;

        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private static CompiledSql Compile(string sql, TargetSqlDialect dialect, ITableCatalog catalog, params (TableIdentity Table, PolicyPredicate Predicate)[] policies)
    {
        var provider = new Policies(policies.ToDictionary(p => p.Table, p => p.Predicate));
        return Compiler.Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy { RowFilters = provider, Masks = provider, Catalog = catalog, Tenant = Tenant },
            // a padded key list is long: the short statements of these tests would hit the default expansion budget (64 x input)
            MaxExpansionFactor = CompileLimits.MaxExpansionFactorLimit
        }, CancellationToken.None);
    }

    private static PolicyPredicate Substitute(PolicyPredicate predicate, TargetSqlDialect dialect, params (int Index, KeySetReplacement Replacement)[] replacements)
    {
        var sites = CorrelatedKeySetSubstitution.FindSites(predicate);
        var map = replacements.Select(r => (sites[r.Index].Node, r.Replacement)).ToList();
        return CorrelatedKeySetSubstitution.Substitute(predicate, map, dialect);
    }

    private static KeySetReplacement Keys(string fk, int ordinal, int maxKeys, params long[] keys) =>
        new(fk, keys.Select(k => new PolicyValue(k, SqlParameterType.Int64)).ToList(), KeySetPadding.Bucket(keys.Length, maxKeys), ordinal);

    // ---- AC-2: golden statements per dialect ----

    private const string Select = "SELECT id FROM fms.air1";

    // ---- SqlServer ----

    [Fact]
    public void Example1_SqlServer_ExistsBaseline_Golden()
    {
        var predicate = Policy([Allow(Example1())]);

        Compile(Select, TargetSqlDialect.SqlServer, Catalog("int", "nvarchar(64)"), (Air1, predicate)).Sql.ShouldBe(
            "SELECT [id] FROM (SELECT [id] AS [id], [client_id] AS [client_id], [region] AS [region], [tenant_id] AS [tenant_id] FROM [fms].[air1] AS [autheris_target] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max)))) AND (EXISTS (SELECT @p1 FROM (SELECT [id] AS [id], [country] AS [country], [tenant_id] AS [tenant_id] FROM [conf].[client] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max))))) AS [c] INNER JOIN (SELECT [id] AS [id], [client_id] AS [client_id], [kind] AS [kind], [tenant_id] AS [tenant_id] FROM [md].[crane] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max))))) AS [cr] ON [c].[id] = [cr].[client_id] WHERE [c].[id] = [autheris_target].[client_id] AND [cr].[kind] = @p2))) AS [air1]");
    }

    [Fact]
    public void Example1_SqlServer_KeySet_Golden()
    {
        var predicate = Policy([Allow(Example1())]);
        var substituted = Substitute(predicate, TargetSqlDialect.SqlServer, (0, Keys("client_id", 0, 2000, 11, 12, 13)));

        var compiled = Compile(Select, TargetSqlDialect.SqlServer, Catalog("int", "nvarchar(64)"), (Air1, substituted));

        compiled.Sql.ShouldBe(
            "SELECT [id] FROM (SELECT [id] AS [id], [client_id] AS [client_id], [region] AS [region], [tenant_id] AS [tenant_id] FROM [fms].[air1] AS [autheris_target] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max)))) AND ([autheris_target].[client_id] IN (@p1, @p2, @p3, @p4))) AS [air1]");
        // the keys are binds typed by the foreign key column (D-8) and padded to the bucket of three keys
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Count().ShouldBe(4);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).ShouldAllBe(p => p.ColumnType == "int" && p.Type == SqlParameterType.Int64);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Select(p => p.Value).ShouldBe([11L, 12L, 13L, 13L]);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Select(p => p.SourceName).ShouldBe(["__rls_ks0_0", "__rls_ks0_1", "__rls_ks0_2", "__rls_ks0_3"]);
    }

    [Fact]
    public void Example2_SqlServer_KeySet_Golden()
    {
        var predicate = Policy([Allow(Example2())], null, Engine);
        var substituted = Substitute(predicate, TargetSqlDialect.SqlServer, (0, Keys("crane_id", 0, 2000, 5)));

        Compile("SELECT id FROM md.engine", TargetSqlDialect.SqlServer, Catalog("int", "nvarchar(64)"), (Engine, substituted)).Sql.ShouldBe(
            "SELECT [id] FROM (SELECT [id] AS [id], [crane_id] AS [crane_id], [tenant_id] AS [tenant_id] FROM [md].[engine] AS [autheris_target] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max)))) AND ([autheris_target].[crane_id] IN (@p1))) AS [engine]");
    }

    // ---- PostgreSql ----

    [Fact]
    public void Example1_PostgreSql_ExistsBaseline_Golden()
    {
        var predicate = Policy([Allow(Example1())]);

        Compile(Select, TargetSqlDialect.PostgreSql, Catalog("integer", "varchar(64)"), (Air1, predicate)).Sql.ShouldBe(
            "SELECT \"id\" FROM (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"region\" AS \"region\", \"tenant_id\" AS \"tenant_id\" FROM \"fms\".\"air1\" AS \"autheris_target\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text))) AND (EXISTS (SELECT $2 FROM (SELECT \"id\" AS \"id\", \"country\" AS \"country\", \"tenant_id\" AS \"tenant_id\" FROM \"conf\".\"client\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text)))) AS \"c\" INNER JOIN (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"kind\" AS \"kind\", \"tenant_id\" AS \"tenant_id\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text)))) AS \"cr\" ON \"c\".\"id\" = \"cr\".\"client_id\" WHERE \"c\".\"id\" = \"autheris_target\".\"client_id\" AND \"cr\".\"kind\" = $3))) AS \"air1\"");
    }

    [Fact]
    public void Example1_PostgreSql_KeySet_Golden()
    {
        var predicate = Policy([Allow(Example1())]);
        var substituted = Substitute(predicate, TargetSqlDialect.PostgreSql, (0, Keys("client_id", 0, 2000, 11, 12, 13)));

        var compiled = Compile(Select, TargetSqlDialect.PostgreSql, Catalog("integer", "varchar(64)"), (Air1, substituted));

        compiled.Sql.ShouldBe(
            "SELECT \"id\" FROM (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"region\" AS \"region\", \"tenant_id\" AS \"tenant_id\" FROM \"fms\".\"air1\" AS \"autheris_target\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text))) AND (\"autheris_target\".\"client_id\" IN ($2, $3, $4, $5))) AS \"air1\"");
        // the keys are binds typed by the foreign key column (D-8) and padded to the bucket of three keys
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Count().ShouldBe(4);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).ShouldAllBe(p => p.ColumnType == "integer" && p.Type == SqlParameterType.Int64);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Select(p => p.Value).ShouldBe([11L, 12L, 13L, 13L]);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Select(p => p.SourceName).ShouldBe(["__rls_ks0_0", "__rls_ks0_1", "__rls_ks0_2", "__rls_ks0_3"]);
    }

    [Fact]
    public void Example2_PostgreSql_KeySet_Golden()
    {
        var predicate = Policy([Allow(Example2())], null, Engine);
        var substituted = Substitute(predicate, TargetSqlDialect.PostgreSql, (0, Keys("crane_id", 0, 2000, 5)));

        Compile("SELECT id FROM md.engine", TargetSqlDialect.PostgreSql, Catalog("integer", "varchar(64)"), (Engine, substituted)).Sql.ShouldBe(
            "SELECT \"id\" FROM (SELECT \"id\" AS \"id\", \"crane_id\" AS \"crane_id\", \"tenant_id\" AS \"tenant_id\" FROM \"md\".\"engine\" AS \"autheris_target\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text))) AND (\"autheris_target\".\"crane_id\" IN ($2))) AS \"engine\"");
    }

    // ---- Oracle ----

    [Fact]
    public void Example1_Oracle_ExistsBaseline_Golden()
    {
        var predicate = Policy([Allow(Example1())]);

        Compile(Select, TargetSqlDialect.Oracle, Catalog("NUMBER(10)", "VARCHAR2(64)"), (Air1, predicate)).Sql.ShouldBe(
            "SELECT \"ID\" FROM (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"region\" AS \"region\", \"tenant_id\" AS \"tenant_id\" FROM \"fms\".\"air1\" \"AUTHERIS_TARGET\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000)))) AND (EXISTS (SELECT :p2 FROM (SELECT \"id\" AS \"id\", \"country\" AS \"country\", \"tenant_id\" AS \"tenant_id\" FROM \"conf\".\"client\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000))))) \"c\" INNER JOIN (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"kind\" AS \"kind\", \"tenant_id\" AS \"tenant_id\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000))))) \"cr\" ON \"c\".\"id\" = \"cr\".\"client_id\" WHERE \"c\".\"id\" = \"AUTHERIS_TARGET\".\"client_id\" AND \"cr\".\"kind\" = :p3))) \"AIR1\"");
    }

    [Fact]
    public void Example1_Oracle_KeySet_Golden()
    {
        var predicate = Policy([Allow(Example1())]);
        var substituted = Substitute(predicate, TargetSqlDialect.Oracle, (0, Keys("client_id", 0, 2000, 11, 12, 13)));

        var compiled = Compile(Select, TargetSqlDialect.Oracle, Catalog("NUMBER(10)", "VARCHAR2(64)"), (Air1, substituted));

        compiled.Sql.ShouldBe(
            "SELECT \"ID\" FROM (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"region\" AS \"region\", \"tenant_id\" AS \"tenant_id\" FROM \"fms\".\"air1\" \"AUTHERIS_TARGET\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000)))) AND (\"AUTHERIS_TARGET\".\"client_id\" IN (:p2, :p3, :p4, :p5))) \"AIR1\"");
        // the keys are binds typed by the foreign key column (D-8) and padded to the bucket of three keys
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Count().ShouldBe(4);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).ShouldAllBe(p => p.ColumnType == "NUMBER(10)" && p.Type == SqlParameterType.Int64);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Select(p => p.Value).ShouldBe([11L, 12L, 13L, 13L]);
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Select(p => p.SourceName).ShouldBe(["__rls_ks0_0", "__rls_ks0_1", "__rls_ks0_2", "__rls_ks0_3"]);
    }

    [Fact]
    public void Example2_Oracle_KeySet_Golden()
    {
        var predicate = Policy([Allow(Example2())], null, Engine);
        var substituted = Substitute(predicate, TargetSqlDialect.Oracle, (0, Keys("crane_id", 0, 2000, 5)));

        Compile("SELECT id FROM md.engine", TargetSqlDialect.Oracle, Catalog("NUMBER(10)", "VARCHAR2(64)"), (Engine, substituted)).Sql.ShouldBe(
            "SELECT \"ID\" FROM (SELECT \"id\" AS \"id\", \"crane_id\" AS \"crane_id\", \"tenant_id\" AS \"tenant_id\" FROM \"md\".\"engine\" \"AUTHERIS_TARGET\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000)))) AND (\"AUTHERIS_TARGET\".\"crane_id\" IN (:p2))) \"ENGINE\"");
    }


    [Fact]
    public void Example1_Oracle_ChunksAtTheInListLimit_AndPassesTheCapabilityValidator()
    {
        var predicate = Policy([Allow(Example1())]);
        var keys = Enumerable.Range(1, 1200).Select(i => (long)i).ToArray();
        var substituted = Substitute(predicate, TargetSqlDialect.Oracle, (0, Keys("client_id", 0, 2000, keys)));

        var compiled = Compile(Select, TargetSqlDialect.Oracle, Catalog("NUMBER(10)", "VARCHAR2(64)"), (Air1, substituted));

        System.Text.RegularExpressions.Regex.Count(compiled.Sql, " IN \\(").ShouldBe(2);
        compiled.Sql.ShouldContain(") OR \"AUTHERIS_TARGET\".\"client_id\" IN (");
        compiled.Parameters.Count(p => p.Origin == ParameterOrigin.Policy).ShouldBe(2000);
    }

    // ---- AC-8: without a substitution the statement is the one of before ----

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "int", "nvarchar(64)")]
    [InlineData(TargetSqlDialect.PostgreSql, "integer", "varchar(64)")]
    [InlineData(TargetSqlDialect.Oracle, "NUMBER(10)", "VARCHAR2(64)")]
    public void WithoutSubstitution_RegisteredAndUnregisteredNodes_GiveByteIdenticalSql(TargetSqlDialect dialect, string wholeType, string text)
    {
        var registered = Policy([Allow(Example1()), Allow(Simple())], [Deny(Example1("tower"))]);
        var unregistered = Unregister(registered);
        var catalog = Catalog(wholeType, text);

        var a = Compile(Select, dialect, catalog, (Air1, registered));
        var b = Compile(Select, dialect, catalog, (Air1, unregistered));
        var c = Compile(Select, dialect, catalog, (Air1, CorrelatedKeySetSubstitution.Substitute(registered,
            Array.Empty<(ExistsExpression, KeySetReplacement)>(), dialect)));

        b.Sql.ShouldBe(a.Sql);
        c.Sql.ShouldBe(a.Sql);
        b.Parameters.Select(p => (p.Marker, p.Value)).ShouldBe(a.Parameters.Select(p => (p.Marker, p.Value)));
        CorrelatedKeySetSubstitution.FindSites(unregistered).ShouldBeEmpty();
        CorrelatedKeySetSubstitution.FindSites(registered).Count.ShouldBe(1);
    }

    /// <summary>
    /// The same predicate with every EXISTS node re-created, so no node is registered (a user AST, or a node a rewriter rebuilt). A rewriter
    /// cannot be used for this: it compares children with record equality and keeps the original instance of an equal copy.
    /// </summary>
    private static PolicyPredicate Unregister(PolicyPredicate predicate) => PolicyPredicate.Create(Copy(predicate.Expression), predicate.Parameters);

    private static Expression Copy(Expression expression) => expression switch
    {
        ExistsExpression exists => exists with { },
        ParenthesizedExpression parenthesized => new ParenthesizedExpression(Copy(parenthesized.Expression)),
        BinaryExpression binary => new BinaryExpression(Copy(binary.Left), binary.Operator, Copy(binary.Right)),
        UnaryExpression unary => new UnaryExpression(unary.Operator, Copy(unary.Operand)),
        _ => expression
    };

    // ---- AC-3: two tables, one statement, a substituted and an EXISTS filter ----

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "int", "nvarchar(64)")]
    [InlineData(TargetSqlDialect.PostgreSql, "integer", "varchar(64)")]
    [InlineData(TargetSqlDialect.Oracle, "NUMBER(10)", "VARCHAR2(64)")]
    public void TwoTables_OneSubstitutedAndOneExists_InTheSameStatement(TargetSqlDialect dialect, string wholeType, string text)
    {
        var air1 = Substitute(Policy([Allow(Example1())]), dialect, (0, Keys("client_id", 0, 2000, 11, 12)));
        var engine = Policy([Allow(Example2())], null, Engine);   // stays EXISTS (strategy Exists)

        var compiled = Compile("SELECT a.id FROM fms.air1 a JOIN md.engine e ON a.id = e.id", dialect, Catalog(wholeType, text), (Air1, air1), (Engine, engine));

        System.Text.RegularExpressions.Regex.Count(compiled.Sql, " IN \\(").ShouldBe(1);
        System.Text.RegularExpressions.Regex.Count(compiled.Sql, "EXISTS \\(").ShouldBe(1);
        compiled.Sql.ShouldContain(dialect == TargetSqlDialect.SqlServer ? "[client_id] IN (" : "\"client_id\" IN (");
    }

    // ---- characterization for WP-C: a long key list against the compile expansion budget ----

    [Fact]
    public void LargePaddedKeyList_ExceedsTheDefaultExpansionBudgetOfAShortStatement()
    {
        // The engine budgets the emitted text at MaxExpansionFactor x the user's SQL (floor 64 characters): 2,000 binds do not fit the default
        // factor of 64 for a short statement. The WP-C stage must treat SqlLimitKind.PolicyExpansionFactor like the bind limit (plan changelog 2026-10-17, WP-B (i)).
        var predicate = Policy([Allow(Example1())]);
        var substituted = Substitute(predicate, TargetSqlDialect.PostgreSql, (0, Keys("client_id", 0, 2000, Enumerable.Range(1, 2000).Select(i => (long)i).ToArray())));
        var provider = new Policies(new Dictionary<TableIdentity, PolicyPredicate> { [Air1] = substituted });

        var ex = Should.Throw<SqlLimitExceededException>(() => Compiler.Compile(Select.AsMemory(), new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy { RowFilters = provider, Masks = provider, Catalog = Catalog("integer", "varchar(64)"), Tenant = Tenant }
        }, CancellationToken.None));

        ex.Kind.ShouldBe(SqlLimitKind.PolicyExpansionFactor);
    }

    // ---- SEC-RF107-07 (d): hostile key values are binds only ----

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "int", "nvarchar(64)")]
    [InlineData(TargetSqlDialect.PostgreSql, "integer", "varchar(64)")]
    [InlineData(TargetSqlDialect.Oracle, "NUMBER(10)", "VARCHAR2(64)")]
    public void HostileKeyValues_AreBindsOnly(TargetSqlDialect dialect, string wholeType, string text)
    {
        string[] hostile = ["it's \"quoted\"", "x' OR '1'='1", "a--b", "a;b; DROP TABLE t;--", "nul\0byte", "/* c */ k", "k\u2028l"];
        var predicate = Policy([Allow(Example1())]);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;
        var replacement = new KeySetReplacement("client_id", hostile.Select(h => new PolicyValue(h, SqlParameterType.String)).ToList(), KeySetPadding.Bucket(hostile.Length, 2000), 0);
        var substituted = CorrelatedKeySetSubstitution.Substitute(predicate,
            [(node, replacement)], dialect);

        // the compile runs the emitted-text invariant checker; it throws when any value reaches the text
        var compiled = Compile(Select, dialect, Catalog(wholeType, text), (Air1, substituted));

        foreach (var value in hostile)
        {
            compiled.Sql.ShouldNotContain(value);
            compiled.Parameters.ShouldContain(p => Equals(p.Value, value) && p.Origin == ParameterOrigin.Policy);
        }

        compiled.Sql.ShouldNotContain(";");
        compiled.Sql.ShouldNotContain("--");
        compiled.Sql.ShouldNotContain("'");
        compiled.Sql.ShouldNotContain("\0");
        AstReflectionCollect(substituted).ShouldBeEmpty();   // not a single literal in the substituted predicate
        EmittedSqlInvariantChecker.Check(compiled.Sql, dialect, compiled.Parameters, [], structuralOnly: true);
    }

    private static IReadOnlyList<LiteralExpression> AstReflectionCollect(PolicyPredicate predicate) =>
        TrinoSqlEngine.Ast.Security.AstReflection.Collect<LiteralExpression>(predicate.Expression);

    // ---- SEC-RF107-06 (f): parity of EXISTS and key set, executed on SQLite ----

    private static readonly TableIdentity SAir1 = new("main", "air1");

    private static InMemoryTableCatalog SqliteCatalog() => new(
    [
        new TableCatalogEntry(SAir1, ImmutableArray.Create(
            new CatalogColumn("id", "INTEGER"), new CatalogColumn("client_id", "INTEGER"), new CatalogColumn("region", "TEXT"), new CatalogColumn("tenant_id", "TEXT")), "tenant_id", 1),
        new TableCatalogEntry(new TableIdentity("main", "client"), ImmutableArray.Create(
            new CatalogColumn("id", "INTEGER"), new CatalogColumn("country", "TEXT"), new CatalogColumn("tenant_id", "TEXT")), "tenant_id", 1),
        new TableCatalogEntry(new TableIdentity("main", "crane"), ImmutableArray.Create(
            new CatalogColumn("id", "INTEGER"), new CatalogColumn("client_id", "INTEGER"), new CatalogColumn("kind", "TEXT"), new CatalogColumn("tenant_id", "TEXT")), "tenant_id", 1)
    ], "main");

    private static ConsentRowFilter SqliteExample1(string kind, int group = 0) => new()
    {
        FilterGroup = group,
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("ds", "main", "client"),
        DependentTableAlias = "c",
        ForeignKeyColumn = "client_id",
        PrimaryKeyColumn = "id",
        AdditionalHops = [new SubqueryJoinHop { Table = new TableIdentifier("ds", "main", "crane"), TableAlias = "cr", LeftJoinColumn = "c.id", RightJoinColumn = "cr.client_id" }],
        SubqueryFilterPredicateJson = $"{{\"cr.kind\": \"{kind}\"}}"
    };

    private static SqliteConnection Seed()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE air1 (id INTEGER, client_id INTEGER NULL, region TEXT, tenant_id TEXT);
            CREATE TABLE client (id INTEGER, country TEXT, tenant_id TEXT);
            CREATE TABLE crane (id INTEGER, client_id INTEGER, kind TEXT, tenant_id TEXT);
            INSERT INTO client VALUES (1,'CH','acme'), (2,'DE','acme'), (3,'CH','other'), (4,'CH','acme');
            INSERT INTO crane VALUES (10,1,'crane','acme'), (11,2,'tower','acme'), (12,3,'crane','other'), (13,4,'crane','acme'), (14,2,'crane','other');
            INSERT INTO air1 VALUES (1,1,'EU','acme'), (2,2,'EU','acme'), (3,NULL,'EU','acme'), (4,3,'EU','acme'),
                                    (5,4,'US','acme'), (6,99,'US','acme'), (7,NULL,'US','acme'), (8,1,'EU','other');
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static List<long> Run(SqliteConnection connection, CompiledSql compiled)
    {
        using var command = connection.CreateCommand();
        new SqliteCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());
        var rows = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Convert.ToInt64(reader.GetValue(0)));
        rows.Sort();
        return rows;
    }

    private static List<long> LoadKeys(SqliteConnection connection, ExistsExpression node, PolicyPredicate predicate, ITableCatalog catalog, out CompiledKeyQuery key)
    {
        key = ((IPolicyKeyQueryCompiler)Compiler).CompileKeyQuery(KeyRequest(node, predicate, catalog, SAir1, TargetSqlDialect.Sqlite), CancellationToken.None);
        return Run(connection, new CompiledSql(key.Sql, key.Parameters, TargetSqlDialect.Sqlite, SqlStatementClass.Select, [], key.CompilerVersion));
    }

    /// <summary>Runs the statement with the EXISTS predicate and with every positive site replaced by the keys of its key query; both must agree.</summary>
    private static (List<long> Exists, List<long> KeySet) Parity(SqliteConnection connection, PolicyPredicate predicate, int expectedSites)
    {
        var catalog = SqliteCatalog();
        const string sql = "SELECT id FROM main.air1";
        var viaExists = Run(connection, Compile(sql, TargetSqlDialect.Sqlite, catalog, (SAir1, predicate)));

        var sites = CorrelatedKeySetSubstitution.FindSites(predicate);
        sites.Count.ShouldBe(expectedSites);
        var map = new List<(ExistsExpression, KeySetReplacement)>();
        for (int i = 0; i < sites.Count; i++)
        {
            var keys = LoadKeys(connection, sites[i].Node, predicate, catalog, out var key);
            map.Add((sites[i].Node, new KeySetReplacement(key.ForeignKeyColumn,
                keys.Select(k => new PolicyValue(k, SqlParameterType.Int64)).ToList(), KeySetPadding.Bucket(keys.Count, 2000), i)));
        }

        var substituted = CorrelatedKeySetSubstitution.Substitute(predicate, map, TargetSqlDialect.Sqlite);
        var viaKeys = Run(connection, Compile(sql, TargetSqlDialect.Sqlite, catalog, (SAir1, substituted)));
        return (viaExists, viaKeys);
    }

    private PolicyPredicate SqlitePolicy(Consent[] allow, Consent[]? deny = null) => _builder.BuildCombined(allow, deny ?? [], SAir1)!;

    [Fact]
    public void KeyQuery_ReturnsTheDistinctTenantScopedKeys_WithoutNull()
    {
        using var connection = Seed();
        var predicate = SqlitePolicy([Allow(SqliteExample1("crane"))]);

        var keys = LoadKeys(connection, CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node, predicate, SqliteCatalog(), out var key);

        keys.ShouldBe([1L, 4L]);   // client 3 and crane 12/14 belong to another tenant; client 2 has no crane
        key.ForeignKeyColumn.ShouldBe("client_id");
    }

    [Fact]
    public void Parity_SingleSite_WithNullForeignKeys()
    {
        using var connection = Seed();

        var (viaExists, viaKeys) = Parity(connection, SqlitePolicy([Allow(SqliteExample1("crane"))]), 1);

        viaExists.ShouldBe([1L, 5L]);   // rows with a NULL foreign key (3, 7), a foreign tenant (8) and unknown keys (6) are out
        viaKeys.ShouldBe(viaExists);
    }

    [Fact]
    public void Parity_AllowOrSimpleFilter()
    {
        using var connection = Seed();

        var (viaExists, viaKeys) = Parity(connection, SqlitePolicy([Allow(SqliteExample1("crane")), Allow(Simple("region", "\"US\""))]), 1);

        viaExists.ShouldBe([1L, 5L, 6L, 7L]);
        viaKeys.ShouldBe(viaExists);
    }

    [Fact]
    public void Parity_DenyConsentOnTheSameTable_StaysExists_AndKeepsNullForeignKeyRows()
    {
        using var connection = Seed();
        var predicate = SqlitePolicy([Allow(SqliteExample1("crane")), Allow(Simple("region", "\"EU\""))], [Deny(SqliteExample1("tower"))]);

        var (viaExists, viaKeys) = Parity(connection, predicate, 1);

        // row 3 has a NULL foreign key: NOT EXISTS is true for it, so the DENY must not exclude it (NOT (fk IN ...) would be UNKNOWN)
        viaExists.ShouldBe([1L, 3L, 4L, 5L]);
        viaKeys.ShouldBe(viaExists);
    }

    [Fact]
    public void Parity_EmptyKeySet_IsOneEqualsZero()
    {
        using var connection = Seed();

        var alone = Parity(connection, SqlitePolicy([Allow(SqliteExample1("nonexistent"))]), 1);
        var orSimple = Parity(connection, SqlitePolicy([Allow(SqliteExample1("nonexistent")), Allow(Simple("region", "\"US\""))]), 1);

        alone.Exists.ShouldBeEmpty();
        alone.KeySet.ShouldBeEmpty();
        orSimple.Exists.ShouldBe([5L, 6L, 7L]);
        orSimple.KeySet.ShouldBe(orSimple.Exists);
    }

    [Fact]
    public void Parity_AndGroup_OfASiteAndASimpleFilter()
    {
        using var connection = Seed();

        var (viaExists, viaKeys) = Parity(connection, SqlitePolicy([Allow(SqliteExample1("crane", 0), Simple("region", "\"EU\""))]), 1);

        viaExists.ShouldBe([1L]);
        viaKeys.ShouldBe(viaExists);
    }
}
