using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Governance;

/// <summary>
/// Issue #107 WP-B (PLAN-GOV-107 A-3, SEC-RF107-01, -06, -07, -08, -11): the engine turns a registered correlated EXISTS into a governed
/// key query. The golden texts are the issue examples 1 (<c>fms.air1</c> through <c>conf.client</c> and the hop <c>md.crane</c>) and 2
/// (<c>md.engine</c> through <c>md.crane</c>). Every derivation rule has a negative test; the mutation tests switch a control off through
/// the compiler hooks and must fail.
/// </summary>
[Trait("Category", "Security")]
public sealed class PolicyKeyQueryCompilerTests
{
    private static readonly TableIdentity Air1 = new("fms", "air1");
    private static readonly TableIdentity Client = new("conf", "client");
    private static readonly TableIdentity Crane = new("md", "crane");
    private static readonly TableIdentity Engine = new("md", "engine");

    private static readonly TenantBinding Tenant = new("__autheris_tenant", "acme", SqlParameterType.String);

    private static TableCatalogEntry Entry(TableIdentity id, params CatalogColumn[] columns) =>
        new(id, columns.ToImmutableArray(), "tenant_id", 1);

    private static TableCatalogEntry Air1Entry(string fkType = "int", string? fkCollation = null) => Entry(Air1,
        new CatalogColumn("id", "int"), new CatalogColumn("client_id", fkType, fkCollation), new CatalogColumn("crane_id", "int"),
        new CatalogColumn("tenant_id", "nvarchar(64)"), new CatalogColumn("valid_from", "datetime2"));

    private static InMemoryTableCatalog Catalog(string pkType = "int", string fkType = "int", string? pkCollation = null, string? fkCollation = null) => new(
    [
        Entry(Air1,
            new CatalogColumn("id", "int"), new CatalogColumn("client_id", fkType, fkCollation), new CatalogColumn("crane_id", "int"),
            new CatalogColumn("tenant_id", "nvarchar(64)"), new CatalogColumn("valid_from", "datetime2")),
        Entry(Client,
            new CatalogColumn("id", pkType, pkCollation), new CatalogColumn("tenant_id", "nvarchar(64)"), new CatalogColumn("country", "nvarchar(20)"),
            new CatalogColumn("tier", "nvarchar(20)")),
        Entry(Crane,
            new CatalogColumn("id", "int"), new CatalogColumn("client_id", "int"), new CatalogColumn("tenant_id", "nvarchar(64)"),
            new CatalogColumn("kind", "nvarchar(20)")),
        Entry(Engine, new CatalogColumn("id", "int"), new CatalogColumn("crane_id", "int"), new CatalogColumn("tenant_id", "nvarchar(64)"))
    ], "dbo");

    private static SqlIdentifier Q(string s) => new(s, true);

    private static ColumnReference Col(string qualified) => new(new SqlQualifiedName(qualified.Split('.').Select(Q).ToList()));

    private static NamedTableSource Table(string schema, string table, string alias) =>
        new(new SqlQualifiedName(new[] { Q(schema), Q(table) }), Q(alias));

    private static Expression Eq(Expression l, Expression r) => new BinaryExpression(l, BinaryOperator.Equal, r);

    private static Expression And(params Expression[] parts) => parts.Aggregate((l, r) => new BinaryExpression(l, BinaryOperator.And, r));

    private static PolicyParameterExpression P(string name, ParameterOrigin origin = ParameterOrigin.Policy) =>
        new(name, SqlParameterType.String, origin);

    private static ExistsExpression Exists(TableSource from, Expression where, GroupByClause? groupBy = null, Expression? having = null,
        PaginationClause? pagination = null, OrderByClause? orderBy = null, bool distinct = false) =>
        new(new SelectStatement(null,
            new QuerySpecification(distinct, [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)], from, where, groupBy, having),
            orderBy, pagination));

    /// <summary>Example 1: <c>EXISTS (SELECT 1 FROM conf.client c JOIN md.crane cr ON c.id = cr.client_id WHERE c.id = target.client_id AND cr.kind = :p)</c>.</summary>
    private static ExistsExpression Example1(Expression? extra = null)
    {
        var from = new JoinedTableSource(Table("conf", "client", "c"), JoinType.Inner, Table("md", "crane", "cr"),
            new OnJoinCondition(Eq(Col("c.id"), Col("cr.client_id"))));
        var where = And(Eq(Col("c.id"), Col("autheris_target.client_id")), Eq(Col("cr.kind"), P("__gql_rls_0")));
        return Exists(from, extra is null ? where : And(where, extra));
    }

    /// <summary>Example 2: <c>EXISTS (SELECT 1 FROM md.crane cr WHERE cr.id = target.crane_id AND cr.kind = :p)</c> over <c>md.engine</c>.</summary>
    private static ExistsExpression Example2() =>
        Exists(Table("md", "crane", "cr"), And(Eq(Col("cr.id"), Col("autheris_target.crane_id")), Eq(Col("cr.kind"), P("__gql_rls_0"))));

    private static readonly IReadOnlyDictionary<string, PolicyValue> KindValue = new Dictionary<string, PolicyValue>
    {
        ["__gql_rls_0"] = new("crane", SqlParameterType.String)
    };

    private static TableCatalogEntry EngineEntry() =>
        Entry(Engine, new CatalogColumn("id", "int"), new CatalogColumn("crane_id", "int"), new CatalogColumn("tenant_id", "nvarchar(64)"));

    private static PolicyKeyQueryRequest Request(ExistsExpression exists, TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        ITableCatalog? catalog = null, TableCatalogEntry? target = null, IReadOnlyDictionary<string, PolicyValue>? parameters = null, int rowLimit = 2001) =>
        new(exists, parameters ?? KindValue, catalog ?? Catalog(), target ?? Air1Entry(), Tenant, dialect, rowLimit);

    private static CompiledKeyQuery Compile(PolicyKeyQueryRequest request) => new PolicyKeyQueryCompiler().CompileKeyQuery(request, CancellationToken.None);

    private static KeyQueryNotDerivableException NotDerivable(PolicyKeyQueryRequest request) =>
        Assert.Throws<KeyQueryNotDerivableException>(() => Compile(request));

    // ---- golden key SQL (AC-2) ----

    [Fact]
    public void Example1_SqlServer_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT [c].[id] FROM (SELECT [id] AS [id], [tenant_id] AS [tenant_id], [country] AS [country], [tier] AS [tier] FROM [conf].[client] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max))))) AS [c] INNER JOIN (SELECT [id] AS [id], [client_id] AS [client_id], [tenant_id] AS [tenant_id], [kind] AS [kind] FROM [md].[crane] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max))))) AS [cr] ON [c].[id] = [cr].[client_id] WHERE [c].[id] IS NOT NULL AND [cr].[kind] = @p1 ORDER BY [c].[id] ASC OFFSET 0 ROWS FETCH NEXT 2001 ROWS ONLY",
            Compile(Request(Example1(), TargetSqlDialect.SqlServer)).Sql);

    [Fact]
    public void Example1_PostgreSql_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT \"c\".\"id\" FROM (SELECT \"id\" AS \"id\", \"tenant_id\" AS \"tenant_id\", \"country\" AS \"country\", \"tier\" AS \"tier\" FROM \"conf\".\"client\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text)))) AS \"c\" INNER JOIN (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"tenant_id\" AS \"tenant_id\", \"kind\" AS \"kind\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text)))) AS \"cr\" ON \"c\".\"id\" = \"cr\".\"client_id\" WHERE \"c\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = $2 ORDER BY \"c\".\"id\" ASC LIMIT 2001",
            Compile(Request(Example1(), TargetSqlDialect.PostgreSql)).Sql);

    [Fact]
    public void Example1_Oracle_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT \"c\".\"id\" FROM (SELECT \"id\" AS \"id\", \"tenant_id\" AS \"tenant_id\", \"country\" AS \"country\", \"tier\" AS \"tier\" FROM \"conf\".\"client\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000))))) \"c\" INNER JOIN (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"tenant_id\" AS \"tenant_id\", \"kind\" AS \"kind\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000))))) \"cr\" ON \"c\".\"id\" = \"cr\".\"client_id\" WHERE \"c\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = :p2 ORDER BY \"c\".\"id\" ASC FETCH FIRST 2001 ROWS ONLY",
            Compile(Request(Example1(), TargetSqlDialect.Oracle)).Sql);

    [Fact]
    public void Example1_Sqlite_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT \"c\".\"id\" FROM (SELECT \"id\" AS \"id\", \"tenant_id\" AS \"tenant_id\", \"country\" AS \"country\", \"tier\" AS \"tier\" FROM \"conf\".\"client\" WHERE (\"tenant_id\" = ?1 AND CAST(\"tenant_id\" AS BLOB) = CAST(?1 AS BLOB))) AS \"c\" INNER JOIN (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"tenant_id\" AS \"tenant_id\", \"kind\" AS \"kind\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = ?1 AND CAST(\"tenant_id\" AS BLOB) = CAST(?1 AS BLOB))) AS \"cr\" ON \"c\".\"id\" = \"cr\".\"client_id\" WHERE \"c\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = ?2 ORDER BY \"c\".\"id\" ASC LIMIT ?3",
            Compile(Request(Example1(), TargetSqlDialect.Sqlite)).Sql);

    [Fact]
    public void Example2_SqlServer_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT [cr].[id] FROM (SELECT [id] AS [id], [client_id] AS [client_id], [tenant_id] AS [tenant_id], [kind] AS [kind] FROM [md].[crane] WHERE ([tenant_id] = @p0 AND CAST(CAST([tenant_id] AS nvarchar(max)) AS varbinary(max)) = CAST(CAST(@p0 AS nvarchar(max)) AS varbinary(max)) AND DATALENGTH(CAST([tenant_id] AS nvarchar(max))) = DATALENGTH(CAST(@p0 AS nvarchar(max))))) AS [cr] WHERE [cr].[id] IS NOT NULL AND [cr].[kind] = @p1 ORDER BY [cr].[id] ASC OFFSET 0 ROWS FETCH NEXT 2001 ROWS ONLY",
            Compile(Request(Example2(), TargetSqlDialect.SqlServer, target: EngineEntry())).Sql);

    [Fact]
    public void Example2_PostgreSql_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT \"cr\".\"id\" FROM (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"tenant_id\" AS \"tenant_id\", \"kind\" AS \"kind\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = $1 AND TEXTSEND(CAST(\"tenant_id\" AS text)) = TEXTSEND(CAST($1 AS text)))) AS \"cr\" WHERE \"cr\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = $2 ORDER BY \"cr\".\"id\" ASC LIMIT 2001",
            Compile(Request(Example2(), TargetSqlDialect.PostgreSql, target: EngineEntry())).Sql);

    [Fact]
    public void Example2_Oracle_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT \"cr\".\"id\" FROM (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"tenant_id\" AS \"tenant_id\", \"kind\" AS \"kind\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = :p1 AND \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(\"tenant_id\" AS VARCHAR2(4000))) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(CAST(:p1 AS VARCHAR2(4000))))) \"cr\" WHERE \"cr\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = :p2 ORDER BY \"cr\".\"id\" ASC FETCH FIRST 2001 ROWS ONLY",
            Compile(Request(Example2(), TargetSqlDialect.Oracle, target: EngineEntry())).Sql);

    [Fact]
    public void Example2_Sqlite_Golden() =>
        Assert.Equal(
            "SELECT DISTINCT \"cr\".\"id\" FROM (SELECT \"id\" AS \"id\", \"client_id\" AS \"client_id\", \"tenant_id\" AS \"tenant_id\", \"kind\" AS \"kind\" FROM \"md\".\"crane\" WHERE (\"tenant_id\" = ?1 AND CAST(\"tenant_id\" AS BLOB) = CAST(?1 AS BLOB))) AS \"cr\" WHERE \"cr\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = ?2 ORDER BY \"cr\".\"id\" ASC LIMIT ?3",
            Compile(Request(Example2(), TargetSqlDialect.Sqlite, target: EngineEntry())).Sql);

    // ---- result shape ----

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY")]
    [InlineData(TargetSqlDialect.PostgreSql, "LIMIT 5")]
    [InlineData(TargetSqlDialect.Oracle, "FETCH FIRST 5 ROWS ONLY")]
    public void RowLimit_IsEmittedInTheDialectSyntax(TargetSqlDialect dialect, string expected)
    {
        var key = Compile(Request(Example1(), dialect, rowLimit: 5));

        Assert.EndsWith(expected, key.Sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "WHERE [c].[id] IS NOT NULL AND [cr].[kind] = @p1 ORDER BY")]
    [InlineData(TargetSqlDialect.PostgreSql, "WHERE \"c\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = $2 ORDER BY")]
    [InlineData(TargetSqlDialect.Oracle, "WHERE \"c\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = :p2 ORDER BY")]
    [InlineData(TargetSqlDialect.Sqlite, "WHERE \"c\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = ?2 ORDER BY")]
    public void KeyQuery_AlwaysCarriesTheIsNotNullConjunct_SEC_RF107_06d(TargetSqlDialect dialect, string fragment) =>
        Assert.Contains(fragment, Compile(Request(Example1(), dialect)).Sql);

    [Fact]
    public void ResultCarriesTypesColumnVersionAndBinds()
    {
        var key = Compile(Request(Example1()));

        Assert.Equal("int", key.KeyColumnType);
        Assert.Equal("int", key.ForeignKeyColumnType);
        Assert.Equal("client_id", key.ForeignKeyColumn);
        Assert.Contains(PolicyKeyQueryCompiler.KeyQueryVersion, key.CompilerVersion);
        Assert.Contains(key.Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "acme"));
        Assert.Contains(key.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "crane") && p.SourceName == "__gql_rls_0");
    }

    [Fact]
    public void ForeignKeyColumn_KeepsTheSpellingOfTheEquivalentExists()
    {
        var exists = Exists(Table("md", "crane", "cr"), And(Eq(Col("cr.id"), Col("autheris_target.CRANE_ID"))));

        Assert.Equal("CRANE_ID", Compile(Request(exists, target: EngineEntry())).ForeignKeyColumn);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void EmitsSingleSelect(TargetSqlDialect dialect)   // SEC-RF107-11
    {
        var sql = Compile(Request(Example1(), dialect)).Sql;

        Assert.StartsWith("SELECT DISTINCT ", sql);
        Assert.DoesNotContain(';', sql);
        Assert.DoesNotContain("--", sql);
        Assert.DoesNotContain("/*", sql);
        Assert.DoesNotContain("'", sql);
        foreach (var word in new[] { "INSERT ", "UPDATE ", "DELETE ", "MERGE ", "DROP ", "EXEC", "UNION ", "autheris_target" })
        {
            Assert.DoesNotContain(word, sql);
        }
    }

    [Fact]
    public void EveryPhysicalTable_IsWrappedWithTheTenantPredicate_IncludingTheHop()
    {
        var sql = Compile(Request(Example1(), TargetSqlDialect.PostgreSql)).Sql;

        Assert.Equal(2, Count(sql, "WHERE (\"tenant_id\" = $1"));
        Assert.Contains("FROM \"conf\".\"client\" WHERE", sql);
        Assert.Contains("FROM \"md\".\"crane\" WHERE", sql);
    }

    private static int Count(string text, string fragment) => (text.Length - text.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    [Fact]
    public void FastSqlEngine_ImplementsTheContractByDelegating()
    {
        IPolicyKeyQueryCompiler engine = new FastSqlEngine();

        Assert.Equal(Compile(Request(Example1())).Sql, engine.CompileKeyQuery(Request(Example1()), CancellationToken.None).Sql);
    }

    [Fact]
    public void ToString_PrintsCountsOnly_SEC_RF107_09()
    {
        var hostile = new Dictionary<string, PolicyValue> { ["__gql_rls_0"] = new("secret-kind-4711", SqlParameterType.String) };
        var request = Request(Example1(), parameters: hostile);
        var key = Compile(request);

        Assert.DoesNotContain("secret-kind-4711", request.ToString());
        Assert.DoesNotContain("secret-kind-4711", key.ToString());
        Assert.DoesNotContain("acme", key.ToString());
        Assert.Contains(key.Parameters, p => Equals(p.Value, "secret-kind-4711"));
    }

    [Fact]
    public void HostileValues_AreBoundNeverRendered_SEC_RF107_07d()
    {
        const string hostile = "x' OR '1'='1' --; DROP TABLE t;\0/*";
        var values = new Dictionary<string, PolicyValue> { ["__gql_rls_0"] = new(hostile, SqlParameterType.String) };

        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.Oracle, TargetSqlDialect.Sqlite })
        {
            var key = Compile(Request(Example1(), dialect, parameters: values));

            Assert.DoesNotContain("OR '1'", key.Sql);
            Assert.DoesNotContain("DROP", key.Sql);
            Assert.DoesNotContain('\0', key.Sql);
            Assert.Contains(key.Parameters, p => Equals(p.Value, hostile));
            EmittedSqlInvariantChecker.Check(key.Sql, dialect, key.Parameters, [], structuralOnly: true);   // the compile already ran the full check
        }
    }

    // ---- A-3 derivation rules, one negative test each (SEC-RF107-06 c) ----

    private static void AssertNotDerivable(ExistsExpression exists, string rule, ITableCatalog? catalog = null, TableCatalogEntry? target = null)
    {
        var ex = NotDerivable(Request(exists, catalog: catalog, target: target ?? EngineEntry()));

        Assert.Equal(KeyQueryNotDerivableReasons.NotDerivable, ex.ReasonCode);
        Assert.Equal(rule, ex.Rule);
    }

    private static readonly Expression KindIsParam = Eq(Col("cr.kind"), P("__gql_rls_0"));

    private static Expression CraneCorrelation() => Eq(Col("cr.id"), Col("autheris_target.crane_id"));

    [Fact]
    public void NotDerivable_ZeroCorrelationConjuncts() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), KindIsParam), "NoCorrelation");

    [Fact]
    public void NotDerivable_TwoCorrelationConjuncts() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), Eq(Col("cr.client_id"), Col("autheris_target.id")))), "MultipleCorrelation");

    [Fact]
    public void NotDerivable_CorrelationThroughAnExpression() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"),
            And(Eq(Col("cr.id"), new BinaryExpression(Col("autheris_target.crane_id"), BinaryOperator.Add, new LiteralExpression(0L, LiteralType.Integer))), KindIsParam)),
            "CorrelationNotColumn");

    [Fact]
    public void NotDerivable_CorrelationOperandIsAFunction() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"),
            And(Eq(new FunctionCallExpression(new SqlQualifiedName("lower"), [Col("cr.id")]), Col("autheris_target.crane_id")), KindIsParam)),
            "CorrelationNotColumn");

    [Fact]
    public void NotDerivable_UnqualifiedCorrelationOperand() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(Eq(Col("id"), Col("autheris_target.crane_id")), KindIsParam)), "CorrelationOperand");

    [Fact]
    public void NotDerivable_RemainingTargetReference_Temporal() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"),
            And(CraneCorrelation(), new BinaryExpression(Col("autheris_target.valid_from"), BinaryOperator.GreaterThanOrEqual, Col("cr.client_id")))),
            "TargetReferenceRemains");

    [Fact]
    public void NotDerivable_RemainingTargetReference_InsideAParenthesizedOr() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"),
            And(CraneCorrelation(), new ParenthesizedExpression(new BinaryExpression(KindIsParam, BinaryOperator.Or, Eq(Col("autheris_target.id"), Col("cr.client_id")))))),
            "TargetReferenceRemains");

    [Fact]
    public void NotDerivable_RemainingTargetReference_InAJoinCondition()
    {
        var from = new JoinedTableSource(Table("md", "crane", "cr"), JoinType.Inner, Table("conf", "client", "c"),
            new OnJoinCondition(Eq(Col("c.id"), Col("autheris_target.client_id"))));

        AssertNotDerivable(Exists(from, And(CraneCorrelation(), KindIsParam)), "TargetReferenceRemains", target: EngineEntry());
    }

    [Fact]
    public void NotDerivable_ATableAliasedAsTheTarget() =>
        AssertNotDerivable(Exists(Table("md", "crane", "autheris_target"), And(CraneCorrelation(), KindIsParam)), "TargetReferenceRemains");

    [Fact]
    public void NotDerivable_OrAtTheTopLevelOfWhere() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), new BinaryExpression(CraneCorrelation(), BinaryOperator.Or, KindIsParam)), "TopLevelOr");

    [Fact]
    public void NotDerivable_OrAtTheTopLevelOfWhere_EvenInParentheses() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), new ParenthesizedExpression(new BinaryExpression(CraneCorrelation(), BinaryOperator.Or, KindIsParam))), "TopLevelOr");

    [Fact]
    public void Derivable_ParenthesizedOrBelowAnd_StaysOneConjunct()
    {
        var exists = Exists(Table("md", "crane", "cr"),
            And(CraneCorrelation(), new ParenthesizedExpression(new BinaryExpression(KindIsParam, BinaryOperator.Or, Eq(Col("cr.client_id"), Col("cr.id"))))));

        var sql = Compile(Request(exists, TargetSqlDialect.PostgreSql, target: EngineEntry())).Sql;

        Assert.Contains("WHERE \"cr\".\"id\" IS NOT NULL AND (\"cr\".\"kind\" = $2 OR \"cr\".\"client_id\" = \"cr\".\"id\")", sql);
    }

    [Fact]
    public void Derivable_ParenthesizedCorrelationAndNestedAndChain()
    {
        var exists = Exists(Table("md", "crane", "cr"),
            new ParenthesizedExpression(And(new ParenthesizedExpression(CraneCorrelation()), KindIsParam)));

        var sql = Compile(Request(exists, TargetSqlDialect.PostgreSql, target: EngineEntry())).Sql;

        Assert.EndsWith("WHERE \"cr\".\"id\" IS NOT NULL AND \"cr\".\"kind\" = $2 ORDER BY \"cr\".\"id\" ASC LIMIT 2001", sql);
    }

    [Fact]
    public void NotDerivable_GroupBy() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), KindIsParam), groupBy: new GroupByClause([Col("cr.kind")])), "GroupBy");

    [Fact]
    public void NotDerivable_Having() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), KindIsParam), having: Eq(Col("cr.kind"), P("__gql_rls_0"))), "Having");

    [Fact]
    public void NotDerivable_Limit() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), KindIsParam),
            pagination: new PaginationClause(null, new LiteralExpression(1L, LiteralType.Integer))), "Limit");

    [Fact]
    public void NotDerivable_OrderBy() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), KindIsParam),
            orderBy: new OrderByClause([new OrderByElement(Col("cr.id"))])), "OrderBy");

    [Fact]
    public void NotDerivable_NoWhere() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), null!), "NoCorrelation");

    // ---- SEC-RF107-01 (a): only policy parameters of the filter definition ----

    [Theory]
    [InlineData(ParameterOrigin.Tenant)]
    [InlineData(ParameterOrigin.Mask)]
    [InlineData(ParameterOrigin.ClientNamed)]
    [InlineData(ParameterOrigin.QueryLiteral)]
    public void NotDerivable_ParameterOfAnotherOrigin_SEC_RF107_01a(ParameterOrigin origin) =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), Eq(Col("cr.kind"), P("__gql_rls_0", origin)))), "ParameterOrigin");

    [Fact]
    public void NotDerivable_ClientParameterReference_SEC_RF107_01a() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), Eq(Col("cr.kind"), new ParameterReference("kind")))), "ClientParameter");

    [Fact]
    public void NotDerivable_PolicyParameterWithoutAValue_SEC_RF107_01a() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), Eq(Col("cr.kind"), P("__gql_rls_9")))), "ParameterValue");

    [Fact]
    public void NotDerivable_PolicyParameterOfAnotherType_SEC_RF107_01a()
    {
        var wrongType = new Dictionary<string, PolicyValue> { ["__gql_rls_0"] = new(1L, SqlParameterType.Int64) };

        var ex = NotDerivable(Request(Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), KindIsParam)), target: EngineEntry(), parameters: wrongType));

        Assert.Equal("ParameterValue", ex.Rule);
    }

    [Theory]
    [InlineData("NOW")]
    [InlineData("newid")]
    public void NotDerivable_FunctionsInTheBody_AreNondeterministicOrCallerDependent(string function) =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"),
            And(CraneCorrelation(), Eq(Col("cr.kind"), new FunctionCallExpression(new SqlQualifiedName(function), [])))), "UnsupportedNode");

    [Fact]
    public void NotDerivable_CurrentTimestampInTheBody() =>
        AssertNotDerivable(Exists(Table("md", "crane", "cr"),
            And(CraneCorrelation(), new BinaryExpression(Col("cr.kind"), BinaryOperator.LessThan, new CurrentDateTimeExpression(CurrentDateTimeKind.CurrentTimestamp)))),
            "UnsupportedNode");

    // ---- SEC-RF107-07 (a), (c): key type pairs ----

    [Theory]
    [InlineData("int", "int")]
    [InlineData("int", "bigint")]
    [InlineData("smallint", "int")]
    [InlineData("tinyint", "smallint")]
    [InlineData("bigint", "bigint")]
    [InlineData("varchar(20)", "varchar(40)")]
    [InlineData("varchar(20)", "nvarchar(40)")]
    [InlineData("nvarchar(20)", "nvarchar(20)")]
    [InlineData("uniqueidentifier", "uniqueidentifier")]
    [InlineData("decimal(10,0)", "numeric(12,0)")]
    [InlineData("numeric(9)", "decimal(18,0)")]
    public void KeyTypePair_OnTheAllowlist_IsDerivable(string pk, string fk)
    {
        var key = Compile(Request(Example1(), catalog: Catalog(pk, fk), target: Air1Entry(fk)));

        Assert.Equal(pk, key.KeyColumnType);
        Assert.Equal(fk, key.ForeignKeyColumnType);
    }

    [Theory]
    [InlineData("bigint", "int")]
    [InlineData("int", "decimal(10,0)")]
    [InlineData("decimal(10,2)", "decimal(10,2)")]
    [InlineData("nvarchar(20)", "varchar(40)")]
    [InlineData("nvarchar(max)", "nvarchar(max)")]
    [InlineData("varchar(max)", "varchar(max)")]
    [InlineData("varchar(20)", "int")]
    [InlineData("int", "varchar(20)")]
    [InlineData("float", "float")]
    [InlineData("datetime2", "datetime2")]
    [InlineData("varbinary(16)", "varbinary(16)")]
    [InlineData("uniqueidentifier", "nvarchar(36)")]
    [InlineData("nvarchar(36)", "uniqueidentifier")]
    [InlineData("sql_variant", "sql_variant")]
    [InlineData("int", "")]
    public void KeyTypePair_OutsideTheAllowlist_IsTypeNotBindable_SEC_RF107_07a(string pk, string fk)
    {
        var ex = NotDerivable(Request(Example1(), catalog: Catalog(pk, fk), target: Air1Entry(fk)));

        Assert.Equal(KeyQueryNotDerivableReasons.TypeNotBindable, ex.ReasonCode);
    }

    [Fact]
    public void DifferingCollations_AreTypeNotBindable_SEC_RF107_07c()
    {
        var ex = NotDerivable(Request(Example1(), catalog: Catalog(pkCollation: "Latin1_General_CS_AS"), target: Air1Entry(fkCollation: "Latin1_General_CI_AS")));

        Assert.Equal(KeyQueryNotDerivableReasons.TypeNotBindable, ex.ReasonCode);
    }

    [Fact]
    public void EqualOrUnknownCollations_AreAccepted()
    {
        Compile(Request(Example1(), catalog: Catalog(pkCollation: "Latin1_General_CS_AS"), target: Air1Entry(fkCollation: "latin1_general_cs_as")));
        Compile(Request(Example1(), catalog: Catalog(pkCollation: "Latin1_General_CS_AS"), target: Air1Entry(fkCollation: null)));
    }

    [Fact]
    public void ForeignKeyColumnMissingFromTheTargetCatalog_IsTypeNotBindable()
    {
        var ex = NotDerivable(Request(Example1(), target: Entry(Air1, new CatalogColumn("id", "int"), new CatalogColumn("tenant_id", "nvarchar(64)"))));

        Assert.Equal(KeyQueryNotDerivableReasons.TypeNotBindable, ex.ReasonCode);
    }

    [Theory]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.Ansi)]
    public void DialectWithoutAProductionCapabilityEntry_IsDialectUnsupported(TargetSqlDialect dialect)
    {
        var ex = NotDerivable(Request(Example1(), dialect));

        Assert.Equal(KeyQueryNotDerivableReasons.DialectUnsupported, ex.ReasonCode);
    }

    [Fact]
    public void RowLimit_MustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Compile(Request(Example1(), rowLimit: 0)));

    [Fact]
    public void Cancellation_Propagates() =>
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new PolicyKeyQueryCompiler().CompileKeyQuery(Request(Example1()), new CancellationToken(canceled: true)));

    // ---- SEC-RF107-08, -11: governance errors are not fallbacks ----

    [Fact]
    public void NestedSubquery_Rejected_SEC_RF107_11()
    {
        var nested = new ExistsExpression(((ExistsExpression)Exists(Table("md", "crane", "n"), Eq(Col("n.kind"), P("__gql_rls_0")))).Subquery);
        var exists = Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), nested));

        var ex = Assert.Throws<SecurityException>(() => Compile(Request(exists, target: EngineEntry())));

        Assert.Equal("Nested policy subqueries are not permitted.", ex.Message);
    }

    [Fact]
    public void DerivedTableInTheBody_Rejected_SEC_RF107_11()
    {
        var derived = new SubqueryTableSource(((ExistsExpression)Exists(Table("md", "crane", "n"), Eq(Col("n.kind"), P("__gql_rls_0")))).Subquery, Q("cr"));

        Assert.Throws<SecurityException>(() => Compile(Request(Exists(derived, And(CraneCorrelation(), KindIsParam)), target: EngineEntry())));
    }

    [Fact]
    public void UnknownTable_IsASecurityException_NotAFallback_SEC_RF107_08()
    {
        var exists = Exists(Table("md", "ghost", "g"), And(Eq(Col("g.id"), Col("autheris_target.crane_id")), Eq(Col("g.kind"), P("__gql_rls_0"))));

        var ex = Assert.Throws<SecurityException>(() => Compile(Request(exists, target: EngineEntry())));

        Assert.IsNotType<KeyQueryNotDerivableException>(ex);
        Assert.Equal("A policy subquery references a table that is not in the catalog.", ex.Message);
    }

    [Fact]
    public void ConflictingTenantParameter_IsAPolicyConflict_NotAFallback()
    {
        var values = new Dictionary<string, PolicyValue> { ["__gql_rls_0"] = new("crane", SqlParameterType.String), [Tenant.ParameterName] = new("other", SqlParameterType.String) };
        var exists = Exists(Table("md", "crane", "cr"), And(CraneCorrelation(), Eq(Col("cr.kind"), P(Tenant.ParameterName))));

        Assert.Throws<PolicyConflictException>(() => Compile(Request(exists, target: EngineEntry(), parameters: values)));
    }

    [Fact]
    public void CompileLimit_IsASqlLimitException_NotAFallback_SEC_RF107_08()
    {
        // The emitted-text limit is a typed limit error of the engine; it must stay what it is.
        var longValue = new Dictionary<string, PolicyValue> { ["__gql_rls_0"] = new(new string('x', 10), SqlParameterType.String) };
        var tiny = new LimitingHooks();

        using var _ = CompilerHooks.Use(tiny);
        Assert.Throws<SqlLimitExceededException>(() => Compile(Request(Example1(), parameters: longValue)));
    }

    private sealed class LimitingHooks : ICompilerHooks
    {
        public bool IsDisabled(SecurityControl control) => false;
        public void OnPass(string name) { }
        public SqlStatement InterceptInjected(SqlStatement secured) => secured;
        public string InterceptEmitted(string sql) => sql + new string(' ', EmittedSqlInvariantChecker.MaxEmittedSqlLength);
        public bool OmitsCacheKeyField(string name) => false;
        public DmlGuardOptions InterceptDmlGuards(DmlGuardOptions requested) => requested;
        public bool BypassesExecutionStackGuard() => false;
        public GovernedSqlException? LeakProviderError(string providerMessage, TargetSqlDialect dialect) => null;
    }

    // ---- SEC-RF107-11 mutation tests: each control is switched off through the hooks and the compile must fail ----

    private static SqlStatement DropTenantWrapper(SqlStatement statement) => (SqlStatement)new TenantWrapperDropper().Visit(statement);

    private sealed class TenantWrapperDropper : TrinoSqlEngine.Ast.Visitors.SqlAstRewriter
    {
        public override SqlNode VisitQuerySpecification(QuerySpecification node) =>
            node.Where is SecurityPredicateExpression ? (QuerySpecification)base.VisitQuerySpecification(node with { Where = null }) : base.VisitQuerySpecification(node);
    }

    private static SelectStatement WithBody(SqlStatement statement, Func<QuerySpecification, QuerySpecification> change)
    {
        var select = (SelectStatement)statement;
        return select with { Body = change((QuerySpecification)select.Body) };
    }

    [Fact]
    public void MutationDropsTenantWrapper_IsDetected()
    {
        using var _ = CompilerHooks.Use(new MutantHooks { FaultyInjector = DropTenantWrapper });

        var ex = Assert.ThrowsAny<SecurityException>(() => Compile(Request(Example1())));

        Assert.IsType<TrinoSqlEngine.Ast.Security.SecurityCoverageException>(ex);
    }

    [Fact]
    public void MutationDropsTenantWrapper_PassesOnlyWhenTheVerifierIsOff_SoTheVerifierIsTheControl()
    {
        using var _ = CompilerHooks.Use(new MutantHooks
        {
            FaultyInjector = DropTenantWrapper,
            DisabledControls = new HashSet<SecurityControl> { SecurityControl.CoverageVerifier }
        });

        var key = Compile(Request(Example1()));

        Assert.DoesNotContain("WHERE ([tenant_id]", key.Sql);   // the mutant really removed the wrapper
        Assert.DoesNotContain(key.Parameters, p => p.Origin == ParameterOrigin.Tenant);
    }

    [Fact]
    public void MutationKeepsCorrelationConjunct_IsDetected()
    {
        using var _ = CompilerHooks.Use(new MutantHooks
        {
            FaultyInjector = s => WithBody(s, spec => spec with
            {
                Where = new BinaryExpression(spec.Where!, BinaryOperator.And, Eq(Col("c.id"), Col("autheris_target.client_id")))
            })
        });

        var ex = Assert.Throws<SecurityException>(() => Compile(Request(Example1())));

        Assert.Equal("The key query still references the target table.", ex.Message);
    }

    [Fact]
    public void MutationDropsIsNotNull_IsDetected()
    {
        using var _ = CompilerHooks.Use(new MutantHooks
        {
            FaultyInjector = s => WithBody(s, spec => spec with { Where = ((BinaryExpression)spec.Where!).Right })
        });

        var ex = Assert.Throws<SecurityException>(() => Compile(Request(Example1())));

        Assert.Equal("The key query lost its IS NOT NULL conjunct.", ex.Message);
    }

    [Fact]
    public void MutationDropsRowLimit_IsDetected()
    {
        using var _ = CompilerHooks.Use(new MutantHooks { FaultyInjector = s => ((SelectStatement)s) with { Pagination = null } });

        var ex = Assert.Throws<SecurityException>(() => Compile(Request(Example1())));

        Assert.Equal("The key query lost its row limit.", ex.Message);
    }

    [Fact]
    public void MutationChangesRowLimit_IsDetected()
    {
        using var _ = CompilerHooks.Use(new MutantHooks
        {
            FaultyInjector = s => ((SelectStatement)s) with { Pagination = new PaginationClause(null, new LiteralExpression(999999L, LiteralType.Integer)) }
        });

        Assert.Throws<SecurityException>(() => Compile(Request(Example1())));
    }

    [Fact]
    public void MutationReplacesTheStatementClass_IsDetected()
    {
        using var _ = CompilerHooks.Use(new MutantHooks
        {
            FaultyInjector = _ => new DeleteStatement(Table("md", "crane", "cr"), null)
        });

        Assert.Throws<SecurityException>(() => Compile(Request(Example1())));
    }

    [Fact]
    public void MutationCorruptsTheEmittedText_IsRejectedByTheInvariantChecker()
    {
        using var _ = CompilerHooks.Use(new MutantHooks { FaultyEmitter = sql => sql + "; DROP TABLE t" });

        Assert.Throws<EmittedSqlInvariantViolationException>(() => Compile(Request(Example1())));
    }
}
