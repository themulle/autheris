using System.Collections.Immutable;
using System.Security;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>WP-X0h: SQLite on the AST path at tier Production (plan 28.3, 28.5, SEC-X1-09, SEC-X1-10).</summary>
public class SqliteAstSupportTests
{
    private static readonly TableIdentity Orders = new("main", "Orders");
    private static readonly FastSqlEngine Engine = new();

    internal static InMemoryTableCatalog Catalog(string tenantType = "TEXT") => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", tenantType), new CatalogColumn("Region", "TEXT"),
            new CatalogColumn("Status", "TEXT"), new CatalogColumn("Amount", "NUMERIC"), new CatalogColumn("Email", "TEXT")),
            "TenantId", 1)
    }, "main");

    internal static CompileRequest Request(DictPolicyProvider? filters = null, DictMaskProvider? masks = null,
        StatementPermissions statements = StatementPermissions.ReadOnly, string tenantType = "TEXT") => new()
    {
        TargetDialect = TargetSqlDialect.Sqlite,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = statements,
        Policy = new GovernancePolicy
        {
            RowFilters = filters ?? new DictPolicyProvider(),
            Masks = masks ?? new DictMaskProvider(),
            Catalog = Catalog(tenantType),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    };

    internal static CompiledSql Compile(string sql, CompileRequest? request = null) =>
        Engine.Compile(sql.AsMemory(), request ?? Request(), CancellationToken.None);

    internal static DictPolicyProvider NoRowPolicy()
    {
        var filters = new DictPolicyProvider();
        filters.NoPolicy.Add(Orders);
        return filters;
    }

    // ---- capability entry ----

    [Fact]
    public void Compile_Sqlite_Production()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite);
        Assert.Equal(DialectSupportTier.Production, caps.Tier);

        var compiled = Compile("SELECT id FROM orders");   // no AllowExperimentalDialect

        Assert.Equal(TargetSqlDialect.Sqlite, compiled.Dialect);
        Assert.Contains("?1", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant && (string?)p.Value == "acme");
    }

    [Fact]
    public void SqliteCapabilities_DeclareTheTypedLimitsAndFlags()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite);
        Assert.Equal(32766, caps.MaxBindParameters);       // bundled SQLITE_MAX_VARIABLE_NUMBER
        Assert.Equal(500, caps.MaxCompoundSelectTerms);    // SQLITE_MAX_COMPOUND_SELECT
        Assert.Equal(1000, caps.MaxExpressionDepth);       // SQLITE_MAX_EXPR_DEPTH
        Assert.Equal(TenantComparisonStyle.CastBlob, caps.TenantComparison);
        Assert.Equal(ParameterMarkerStyle.QuestionOrdinal, caps.MarkerStyle);
        Assert.True(caps.InDbHmac);                        // gateway_hmac_sha256 UDF
        Assert.True(caps.LikeIsCaseInsensitive);           // SEC-ADG-18
        Assert.Empty(caps.AllowedTableFunctions);
        Assert.Equal(StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete, caps.DmlStatements & ~StatementPermissions.Merge);
    }

    [Fact]
    public void TenantPredicate_IsABinaryBlobComparison_NotTheCollatedEquality()
    {
        var compiled = Compile("SELECT id FROM orders");
        Assert.Contains("CAST(\"TenantId\" AS BLOB)", compiled.Sql);
        Assert.Contains("CAST(?1 AS BLOB)", compiled.Sql);
    }

    // ---- binder ----

    [Fact]
    public void SqliteBinder_RefusesCheckedStatement()
    {
        var binder = new SqliteCompiledSqlBinder();
        var checkedStatement = new CompiledSql("INSERT INTO t SELECT 1", ImmutableArray<BoundParameter>.Empty, TargetSqlDialect.Sqlite,
            SqlStatementClass.Insert, ImmutableArray<TrinoSqlEngine.Ast.Nodes.SecurityPredicateId>.Empty, "test") { ExpectedAffectedRows = 1 };
        using var cmd = new SqliteCommand();
        Assert.Throws<CheckedExecutionRequiredException>(() => binder.Bind(cmd, checkedStatement, new Dictionary<string, object?>()));
    }

    [Fact]
    public void CompiledSqlBinders_Sqlite_IsTheClosedSingleton_AndHasACheckedExecutor()
    {
        var binder = CompiledSqlBinders.For(TargetSqlDialect.Sqlite);
        Assert.IsType<SqliteCompiledSqlBinder>(binder);
        Assert.Same(binder, CompiledSqlBinders.For(TargetSqlDialect.Sqlite));
        Assert.NotNull(CompiledSqlBinders.CheckedExecutorFor(TargetSqlDialect.Sqlite));
    }

    [Fact]
    public void SqliteBinder_BindsTheMarkersOfACompiledStatement_AndTheStatementRuns()
    {
        var compiled = Compile("SELECT id FROM orders WHERE status = 'open'", Request(filters: NoRowPolicy()));
        Assert.DoesNotContain("'open'", compiled.Sql);   // the literal is bound too
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE \"Orders\" (\"Id\" INTEGER, \"TenantId\" TEXT COLLATE NOCASE, \"Region\" TEXT, \"Status\" TEXT, \"Amount\" NUMERIC, \"Email\" TEXT);" +
                "INSERT INTO \"Orders\" VALUES (1,'acme','EU','open',1,NULL),(2,'ACME','EU','open',1,NULL),(3,'other','EU','open',1,NULL),(4,'acme','EU','closed',1,NULL);";
            create.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        new SqliteCompiledSqlBinder().Bind(cmd, compiled, new Dictionary<string, object?>());
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));

        Assert.Equal(new long[] { 1 }, ids);   // not the NOCASE variant, not the other tenant, not the closed order
    }

    // ---- closed function allowlist (SEC-X1-10) ----

    [Theory]
    [InlineData("SELECT load_extension('x') FROM orders")]
    [InlineData("SELECT randomblob(16) FROM orders")]
    [InlineData("SELECT zeroblob(1000000000) FROM orders")]
    [InlineData("SELECT printf('%1000000d', id) FROM orders")]
    [InlineData("SELECT format('%1000000d', id) FROM orders")]
    [InlineData("SELECT sqlite_version() FROM orders")]
    [InlineData("SELECT sqlite_source_id() FROM orders")]
    [InlineData("SELECT sqlite_compileoption_used('x') FROM orders")]
    [InlineData("SELECT gateway_hmac_sha256(email, 'k') FROM orders")]
    [InlineData("SELECT readfile('/etc/passwd') FROM orders")]
    [InlineData("SELECT json_each.value FROM orders, json_each('[1,2]')")]
    [InlineData("SELECT * FROM orders, json_tree('[1,2]')")]
    [InlineData("SELECT name FROM pragma_table_info('orders')")]
    [InlineData("SELECT * FROM orders WHERE id IN (SELECT rowid FROM pragma_database_list)")]
    public void UserSql_WithAFunctionOutsideTheClosedAllowlist_IsRejected(string sql)
    {
        Assert.ThrowsAny<Exception>(() => Compile(sql, Request(filters: NoRowPolicy())));
    }

    [Theory]
    [InlineData("SELECT lower(email) FROM orders")]
    [InlineData("SELECT coalesce(email, 'x') FROM orders")]
    [InlineData("SELECT substr(email, 1, 3) FROM orders")]
    [InlineData("SELECT instr(email, '@') FROM orders")]
    [InlineData("SELECT strftime('%Y', status) FROM orders")]
    [InlineData("SELECT ifnull(email, 'x'), abs(amount) FROM orders")]
    [InlineData("SELECT count(*), max(amount) FROM orders")]
    public void UserSql_WithAnAllowlistedFunction_Compiles(string sql)
    {
        var compiled = Compile(sql, Request(filters: NoRowPolicy()));
        Assert.Equal(TargetSqlDialect.Sqlite, compiled.Dialect);
    }

    [Fact]
    public void TheSqliteAllowlist_ExcludesTheDangerousNames_AndTheCapabilityMapMatchesIt()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite);
        foreach (var name in new[] { "load_extension", "randomblob", "zeroblob", "printf", "format", "json_each", "json_tree",
                     "pragma_table_info", "sqlite_version", "sqlite_source_id", "gateway_hmac_sha256", "readfile", "writefile" })
        {
            Assert.DoesNotContain(name, SqlFunctionAllowlists.Sqlite);
            Assert.False(caps.Functions.TryGetRule(name, out _), name);
            // printf and format are legitimate on other dialects (format on SQL Server and PostgreSQL), so only SQLite excludes them.
            if (name is not ("printf" or "format")) Assert.True(SqlFunctionPolicy.IsDeniedByDefault(name), name);
        }
    }

    // ---- typed capability limits (SEC-X1-10) ----

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private static QuerySpecification Term() => new(false, new SelectItem[] { new ColumnSelectItem(Col("a"), null) },
        new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("t", true) }), null), null, null, null);

    private static SelectStatement UnionAllChain(int terms)
    {
        QueryBody body = Term();
        for (int i = 1; i < terms; i++) body = new SetOperationQuery(body, SetOperator.Union, false, Term());
        return new SelectStatement(null, body, null, null);
    }

    [Fact]
    public void CompoundSelect_WithMoreThan500Terms_IsATypedLimit_BeforeExecution()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite);

        DialectCapabilityValidator.Validate(UnionAllChain(500), caps);
        var ex = Assert.Throws<SqlLimitExceededException>(() => DialectCapabilityValidator.Validate(UnionAllChain(501), caps));
        Assert.Equal(SqlLimitKind.CompoundSelectTerms, ex.Kind);
        Assert.Equal(TargetSqlDialect.Sqlite, ex.Dialect);
        Assert.Equal(501, ex.Requested);
        Assert.Equal(500, ex.Maximum);
    }

    [Fact]
    public void CompoundSelect_HasNoLimitOnDialectsThatDeclareNone()
    {
        DialectCapabilityValidator.Validate(UnionAllChain(600), DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql));
    }

    private static SelectStatement DeepExpression(int depth)
    {
        Expression expr = Col("a");
        for (int i = 0; i < depth; i++) expr = new BinaryExpression(expr, BinaryOperator.Add, Col("b"));
        var spec = new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(expr, null) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("t", true) }), null), null, null, null);
        return new SelectStatement(null, spec, null, null);
    }

    [Fact]
    public void Expression_DeeperThan1000_IsATypedLimit()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite);

        DialectCapabilityValidator.Validate(DeepExpression(900), caps);
        var ex = Assert.Throws<SqlLimitExceededException>(() => DialectCapabilityValidator.Validate(DeepExpression(1100), caps));
        Assert.Equal(SqlLimitKind.ExpressionDepth, ex.Kind);
        Assert.Equal(1000, ex.Maximum);
    }

    // ---- policy LIKE (SEC-ADG-18) ----

    private static DictPolicyProvider LikeFilter(Expression pattern, Dictionary<string, PolicyValue> values)
    {
        var filters = new DictPolicyProvider();
        filters.Predicates[Orders] = PolicyPredicate.Create(new LikeExpression(Col("Region"), pattern), values);
        return filters;
    }

    [Fact]
    public void PolicyLike_OnSqlite_IsRejected_BecauseLikeIsCaseInsensitiveThere()
    {
        var filters = LikeFilter(new PolicyParameterExpression("__pol_like", SqlParameterType.String),
            new() { ["__pol_like"] = new("EU%", SqlParameterType.String) });

        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile("SELECT id FROM orders", Request(filters: filters)));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    [Fact]
    public void PolicyLike_OnADialectWithACaseSensitiveLike_StillCompiles()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql);
        var statement = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("a"), null) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("t", true) }), null),
            new LikeExpression(Col("Region"), new PolicyParameterExpression("p", SqlParameterType.String)), null, null), null, null);
        DialectCapabilityValidator.Validate(statement, caps);
    }

    [Fact]
    public void UserLike_OnSqlite_IsNotARejectedPolicyConstruct()
    {
        // The user's own LIKE only narrows their own result, so it is not a policy bypass (SEC-ADG-18 concerns policy patterns).
        Assert.Equal(TargetSqlDialect.Sqlite, Compile("SELECT id FROM orders WHERE email LIKE 'a%'", Request(filters: NoRowPolicy())).Dialect);
    }

    // ---- tenant column affinity (SEC-X1-09) ----

    [Theory]
    [InlineData("TEXT")]
    [InlineData("VARCHAR(64)")]
    [InlineData("NVARCHAR(64)")]
    [InlineData("CHARACTER(20)")]
    [InlineData("CLOB")]
    public void TenantColumn_WithTextAffinity_Compiles(string declaredType)
    {
        Assert.Contains("AS BLOB", Compile("SELECT id FROM orders", Request(filters: NoRowPolicy(), tenantType: declaredType)).Sql);
    }

    [Theory]
    [InlineData("INTEGER")]
    [InlineData("NUMERIC")]
    [InlineData("DECIMAL(10,2)")]
    [InlineData("REAL")]
    [InlineData("BLOB")]
    [InlineData("DATETIME")]
    [InlineData("BOOLEAN")]
    public void TenantColumn_WithoutTextAffinity_IsRejected_BecauseTheStoredValueWasAlreadyConverted(string declaredType)
    {
        Assert.Throws<SecurityException>(() => Compile("SELECT id FROM orders", Request(filters: NoRowPolicy(), tenantType: declaredType)));
    }

    [Theory]
    [InlineData("TEXT", true)]
    [InlineData("varchar(10)", true)]
    [InlineData("MEDIUMINT", false)]
    [InlineData("POINT", false)]
    [InlineData("", false)]
    [InlineData("INTERESTING TEXT", false)]   // contains INT first: the affinity rules test INT before CHAR/CLOB/TEXT
    public void SqliteAffinityRules_AreFollowedInOrder(string declared, bool text)
    {
        Assert.Equal(text, TrinoSqlEngine.Ast.Security.SqliteTypeAffinity.IsText(declared));
    }

    // ---- INSERT check option cast targets (CR-ADG-42) ----

    [Theory]
    [InlineData("TEXT", "TEXT", true)]
    [InlineData("VARCHAR(255)", "TEXT", true)]
    [InlineData("INTEGER", "INTEGER", false)]
    [InlineData("BIGINT", "INTEGER", false)]
    [InlineData("DOUBLE", "REAL", false)]
    [InlineData("REAL", "REAL", false)]
    [InlineData("NUMERIC", "NUMERIC", false)]
    [InlineData("DECIMAL(18,2)", "NUMERIC", false)]
    [InlineData("BOOLEAN", "NUMERIC", false)]
    [InlineData("DATE", "TEXT", true)]            // ISO text by convention, not NUMERIC (CAST('2026-01-02' AS NUMERIC) is 2026)
    [InlineData("DATETIME", "TEXT", true)]
    public void CheckOptionCastTarget_IsTheAffinityKeyword(string declared, string expected, bool text)
    {
        Assert.True(TrinoSqlEngine.Ast.Security.CatalogTypeMap.TryResolve(TargetSqlDialect.Sqlite, declared, out string native, out bool isText));
        Assert.Equal(expected, native);
        Assert.Equal(text, isText);
        // idempotent: the emitter resolves its own output again and must get the same spelling
        Assert.True(TrinoSqlEngine.Ast.Security.CatalogTypeMap.TryResolve(TargetSqlDialect.Sqlite, native, out string again, out _));
        Assert.Equal(native, again);
    }

    [Theory]
    [InlineData("BLOB")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("TEXT); DROP TABLE x; --")]
    public void CheckOptionCastTarget_ForBlobAffinityOrForeignText_FailsClosed(string? declared)
    {
        Assert.False(TrinoSqlEngine.Ast.Security.CatalogTypeMap.TryResolve(TargetSqlDialect.Sqlite, declared, out _, out _));
    }

    // ---- provider errors (CR-ADG-34) ----

    [Theory]
    [InlineData(19, 1555, DmlConstraintKind.Unique)]
    [InlineData(19, 2067, DmlConstraintKind.Unique)]
    [InlineData(19, 787, DmlConstraintKind.ForeignKey)]
    [InlineData(19, 1299, DmlConstraintKind.NotNull)]
    [InlineData(19, 275, DmlConstraintKind.Check)]
    public void SqliteConstraintErrors_MapToTheValueFreeTypedError(int code, int extended, DmlConstraintKind kind)
    {
        var provider = new SqliteException("UNIQUE constraint failed: Orders.Id secret-value", code, extended);

        var mapped = DmlErrorSanitizer.TryMap(TargetSqlDialect.Sqlite, provider);

        Assert.NotNull(mapped);
        Assert.Equal(kind, mapped!.Kind);
        Assert.Null(mapped.InnerException);
        Assert.DoesNotContain("secret-value", mapped.ToString());
    }

    [Fact]
    public void SqliteOtherErrors_AreGenericOrDataErrors_NeverTheDriverText()
    {
        var generic = DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteException("no such table: Secret", 1));
        Assert.Equal(GovernedSqlErrorCodes.ProviderError, generic.Code);
        Assert.DoesNotContain("Secret", generic.ToString());

        var data = DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, new SqliteException("string or blob too big", 18));
        Assert.Equal(GovernedSqlErrorCodes.DataError, data.Code);
    }
}
