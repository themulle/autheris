namespace TrinoSqlEngine.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Security;
using Xunit;

[Trait("Category", "SqlGate")]
public class SecurityRemediationTests
{
    private readonly FastSqlEngine _engine = new();

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    [Fact]
    public void C02_CteWithDottedName_AnalyzerStillReportsQualifiedTable()
    {
        string sql = "WITH \"sales.orders\" AS (SELECT 1 x) SELECT * FROM sales.orders";

        var meta = _engine.Analyze(sql.AsMemory());

        var target = Assert.Single(meta.ReferencedTables);
        Assert.Equal("sales", target.Schema);
        Assert.Equal("orders", target.TableName);
        Assert.Equal("sales.orders", target.FullName);
    }

    [Fact]
    public void C06_DeeplyNestedParentheses_AreRejectedBeforeParsing()
    {
        string sql = "SELECT " + new string('(', 5000) + "1" + new string(')', 5000);

        Assert.ThrowsAny<ParseCanceledException>(() => _engine.Parse(sql.AsMemory()));
    }

    [Fact]
    public void C06_DeeplyNestedCase_IsRejected()
    {
        string sql = "SELECT " + Repeat("CASE WHEN 1 = 1 THEN ", 300) + "1" + Repeat(" END", 300);

        Assert.ThrowsAny<ParseCanceledException>(() => _engine.Parse(sql.AsMemory()));
    }

    [Fact]
    public void C06_LongUnaryOperatorChains_AreRejected()
    {
        string minusChain = "SELECT " + Repeat("- ", 1000) + "1";
        string notChain = "SELECT 1 WHERE " + Repeat("NOT ", 1000) + "true";

        Assert.ThrowsAny<ParseCanceledException>(() => _engine.Parse(minusChain.AsMemory()));
        Assert.ThrowsAny<ParseCanceledException>(() => _engine.Parse(notChain.AsMemory()));
    }

    [Fact]
    public void C06_LeftRecursiveChain_IsRejectedByParseTreeDepthLimit()
    {
        string sql = "SELECT 1" + Repeat(" + 1", 8000);

        Assert.ThrowsAny<ParseCanceledException>(() => _engine.Parse(sql.AsMemory()));
    }

    [Fact]
    public void C06_DeeplyNestedExpression_IsRejectedInParseExpression()
    {
        string expr = new string('(', 5000) + "a = 1" + new string(')', 5000);

        Assert.ThrowsAny<ParseCanceledException>(() => _engine.ParseExpression(expr.AsMemory()));
    }

    [Fact]
    public void C06_ModerateNesting_StillParses()
    {
        string sql = "SELECT " + new string('(', 50) + "1" + new string(')', 50) +
                     " FROM orders WHERE id IN (SELECT id FROM (SELECT id FROM (SELECT id FROM t)))";

        var (tree, _) = _engine.Parse(sql.AsMemory());

        Assert.NotNull(tree);
    }

    [Fact]
    public void C06_NestingDepthLimit_IsConfigurable()
    {
        var strict = new FastSqlEngine { MaxNestingDepth = 10 };
        string sql = "SELECT " + new string('(', 11) + "1" + new string(')', 11);

        Assert.ThrowsAny<ParseCanceledException>(() => strict.Parse(sql.AsMemory()));
        Assert.NotNull(new FastSqlEngine { MaxNestingDepth = 20 }.Parse(sql.AsMemory()).Tree);
    }

    [Fact]
    public void C06_DefaultMaxQueryLength_Is64k()
    {
        Assert.Equal(65_536, _engine.MaxQueryLength);
        // SQ-08: default nesting depth lowered from 200 to 100.
        Assert.Equal(100, _engine.MaxNestingDepth);

        string sql = "SELECT 1 FROM t WHERE x = '" + new string('a', 70_000) + "'";
        Assert.Throws<ArgumentOutOfRangeException>(() => _engine.Parse(sql.AsMemory()));
    }

    // ---------------------------------------------------------------- C-01

    [Fact]
    public void C01_Analyzer_ReportsFunctionCallsAndMissingTables()
    {
        var meta = _engine.Analyze("SELECT query_to_xml('SELECT * FROM hr.salaries', true, false, '')".AsMemory());

        Assert.NotNull(meta.FunctionCalls);
        Assert.Contains("query_to_xml", meta.FunctionCalls!);
        Assert.Empty(meta.ReferencedTables);
    }

    [Fact]
    public void C01_DefaultDenylist_MatchesQualifiedAndPrefixedNames()
    {
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("pg_catalog.query_to_xml"));
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("DBLINK_EXEC"));
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("xp_cmdshell"));
        Assert.False(SqlFunctionPolicy.IsDeniedByDefault("upper"));
        Assert.False(SqlFunctionPolicy.IsDeniedByDefault("count"));
    }

    // ---------------------------------------------------------------- H-14

    [Fact]
    public void M22_Analyzer_IgnoresCteLimitAsRootLimit()
    {
        var meta = _engine.Analyze("WITH x AS (SELECT * FROM customers LIMIT 5) SELECT * FROM x".AsMemory());

        Assert.False(meta.HasExplicitLimit);
    }

    // ---------------------------------------------------------------- M-23

    [Fact]
    public void M24_QuoteIdentifier_EscapesEmbeddedQuotes()
    {
        Assert.Equal("\"a\"\"b\"", SqlIdentifierHelper.QuoteIdentifier("a\"b"));
    }

    // ---------------------------------------------------------------- N1

    [Fact]
    public void N1_ConcurrentFallbackParses_AreIsolated()
    {
        var engine = new FastSqlEngine();
        var unexpected = new ConcurrentBag<Exception>();

        Parallel.For(0, 64, i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    engine.Parse("SELECT * FROM WHERE".AsMemory());
                    unexpected.Add(new InvalidOperationException("Invalid SQL was accepted."));
                }
                else
                {
                    engine.Parse("SELECT id FROM orders WHERE amount > 100".AsMemory());
                }
            }
            catch (ParseCanceledException) when (i % 2 == 0)
            {
                // expected
            }
            catch (Exception ex)
            {
                unexpected.Add(ex);
            }
        });

        Assert.Empty(unexpected);
    }

    // ---------------------------------------------------------------- DML guardrails (RejectUnfilteredDml)

    [Fact]
    public void R4_P03_BackslashCheck_WithNestingDisabled_UsesExplicitTokenOptions()
    {
        var engine = new FastSqlEngine { MaxNestingDepth = 0 };
        var tokenOptions = new SqlTokenSecurityOptions { RejectBackslashInStrings = true };

        var ex = Assert.Throws<ParseCanceledException>(() => engine.Parse("SELECT 'a\\b' FROM orders".AsMemory(), tokenOptions));

        Assert.Contains("Backslash escapes", ex.Message);
        Assert.NotNull(engine.Parse("SELECT 'a\\b' FROM orders".AsMemory()).Tree);
    }

    // ---------------------------------------------------------------- P-04

    [Fact]
    public void R4_P06_DirectParsing_KeepsPermissiveDefaultsForCompliance()
    {
        var engine = new FastSqlEngine();

        Assert.NotNull(engine.Parse("SELECT * FROM orders FOR VERSION AS OF 1".AsMemory()).Tree);
        Assert.NotNull(engine.Parse("SELECT * FROM \"sales.orders\" -- comment".AsMemory()).Tree);
    }

    // ---------------------------------------------------------------- SQ-08

    [Fact]
    public void R4_SQ08_DefaultLimits()
    {
        var engine = new FastSqlEngine();

        Assert.Equal(100, engine.MaxNestingDepth);
        Assert.Equal(TimeSpan.FromSeconds(5), engine.ParseTimeout);
        Assert.Equal(16 * 1024 * 1024, engine.ParseThreadStackSize);
        Assert.Null(engine.ParseConcurrencyLimiter);
        Assert.True(FastSqlEngine.DefaultMaxConcurrentParses >= 2);
    }

    [Fact]
    public void R4_SQ08_ParseTimeout_AbortsLongParse()
    {
        var engine = new FastSqlEngine { ParseTimeout = TimeSpan.FromTicks(1) };
        string sql = "SELECT * FROM orders WHERE id IN (" + string.Join(", ", Enumerable.Range(0, 5000)) + ")";

        var ex = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(sql.AsMemory()));
        Assert.IsType<TimeoutException>(ex.InnerException);

        // The engine stays usable with a regular budget (pooled parsers are returned by the parser thread).
        engine.ParseTimeout = TimeSpan.FromSeconds(30);
        Assert.NotNull(engine.Parse(sql.AsMemory()).Tree);
    }

    [Fact]
    public void R4_SQ08_CanceledToken_AbortsParse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _engine.Parse("SELECT id FROM orders".AsMemory(), null, cts.Token));
    }

    [Fact]
    public void R4_SQ08_ConcurrencyLimiter_BoundsParserThreads()
    {
        using var limiter = new SemaphoreSlim(0, 1);
        var engine = new FastSqlEngine
        {
            ParseConcurrencyLimiter = limiter,
            ParseTimeout = TimeSpan.FromMilliseconds(200)
        };

        var ex = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse("SELECT id FROM orders".AsMemory()));
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("concurrent parser slots", ex.Message);

        limiter.Release();
        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);

        // The parser thread releases its slot before completion is signaled.
        Assert.Equal(1, limiter.CurrentCount);
    }

    [Fact]
    public void R4_SQ08_SyntaxErrorsFromParserThread_KeepTheirExceptionType()
    {
        Assert.ThrowsAny<ParseCanceledException>(() => _engine.Parse("SELECT * FROM WHERE".AsMemory()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FastSqlEngine { MaxQueryLength = 10 }.Parse("SELECT id FROM orders".AsMemory()));
    }

    // ---------------------------------------------------------------- SQ-06 / P-02

    [Fact]
    public void R4_SQ06_DialectAllowlists_ContainCommonFunctions_AndNoDeniedFunction()
    {
        foreach (TargetSqlDialect dialect in Enum.GetValues<TargetSqlDialect>())
        {
            var allowlist = SqlFunctionAllowlists.GetDefault(dialect);
            foreach (var name in new[] { "count", "sum", "avg", "min", "max", "lower", "upper", "coalesce", "nullif", "row_number", "rank", "lag", "lead", "abs", "round" })
            {
                Assert.Contains(name, allowlist);
            }

            foreach (var name in allowlist)
            {
                Assert.False(SqlFunctionPolicy.IsDeniedByDefault(name), $"{dialect}: {name} is denylisted");
            }
        }

        Assert.Contains("date_trunc", SqlFunctionAllowlists.PostgreSql);
        Assert.Contains("string_agg", SqlFunctionAllowlists.PostgreSql);
        Assert.Contains("dateadd", SqlFunctionAllowlists.SqlServer);
        Assert.Contains("datediff", SqlFunctionAllowlists.SqlServer);
        Assert.Contains("strftime", SqlFunctionAllowlists.Sqlite);
        Assert.DoesNotContain("load_extension", SqlFunctionAllowlists.Sqlite);
        Assert.DoesNotContain("session_context", SqlFunctionAllowlists.SqlServer);
    }

    // =================================================================================================================
    // Governed behaviour on the AST path. The legacy rewriter (RewriteRls) and its string options are gone since the X1 cutover.
    // Each test below asserts the outcome of the old test semantically through FastSqlEngine.Compile (ProbeEnv: dbo.Orders with
    // Id, TenantId, Region, Status, Amount, Email; dbo.Entitlements; tenant "acme"). Tests whose subject was legacy-only
    // behaviour (string policy providers, text format of the rewrite, switches that no longer exist) were removed; the
    // covering AST tests are named in the work package log (plan section 28, WP-X1 part B).
    // =================================================================================================================

    private static readonly TargetSqlDialect[] FunctionDialects =
        [TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle];

    private static ProbeEnv Env(TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => new(dialect);

    private static int Tenants(CompiledSql compiled) => compiled.Parameters.Count(p => p.Origin == ParameterOrigin.Tenant);

    // ---------------------------------------------------------------- C-02 / C-06 (governed forms)

    [Fact]
    public void C06_DeeplyNestedSubqueries_AreRejected()
    {
        string sql = Repeat("SELECT * FROM (", 1000) + "SELECT 1" + Repeat(")", 1000);

        Assert.ThrowsAny<ParseCanceledException>(() => Env().Compile(sql));
    }

    // ---------------------------------------------------------------- C-01

    [Theory]
    [InlineData("SELECT query_to_xml('SELECT * FROM hr.salaries', true, false, '')")]
    [InlineData("SELECT table_to_xml('hr.salaries', true, false, '')")]
    [InlineData("SELECT query_to_xml_and_xmlschema('SELECT * FROM hr.salaries', true, false, '')")]
    [InlineData("SELECT pg_catalog.pg_read_file('/etc/passwd')")]
    [InlineData("SELECT \"QUERY_TO_XML\"('SELECT 1', true, false, '')")]
    [InlineData("SELECT dblink('host=evil', 'SELECT * FROM hr.salaries')")]
    [InlineData("SELECT set_config('app.tenant', 'victim', false)")]
    [InlineData("SELECT current_setting('app.tenant')")]
    [InlineData("SELECT lo_import('/etc/passwd')")]
    [InlineData("SELECT pg_sleep(100)")]
    [InlineData("SELECT id FROM orders WHERE id IN (SELECT database_to_xml(true, false, ''))")]
    [InlineData("SELECT sys_context('USERENV', 'SESSION_USER')")]
    [InlineData("SELECT userenv('SCHEMA')")]
    [InlineData("SELECT ora_hash('test')")]
    [InlineData("SELECT dbms_pipe.receive_message('pipe', 5)")]
    [InlineData("SELECT dbms_lock.sleep(5)")]
    [InlineData("SELECT dbms_random.value(0, 100)")]
    public void C01_DangerousFunctions_AreRejected(string sql)
    {
        foreach (var dialect in FunctionDialects)
        {
            Assert.ThrowsAny<SecurityException>(() => Env(dialect).Compile(sql));
        }
    }

    [Fact]
    public void C01_RegularFunctions_StillWork()
    {
        var compiled = Env().Compile("SELECT upper(status), count(*) FROM orders GROUP BY upper(status)");

        Assert.True(Tenants(compiled) > 0);
    }

    [Fact]
    public void C01_CallerAllowlist_PermitsOnlyListedFunctions()
    {
        var env = Env();
        var request = env.Request() with { AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upper" } };

        env.Engine.Compile("SELECT upper(status) FROM orders".AsMemory(), request, CancellationToken.None);
        Assert.ThrowsAny<SecurityException>(() =>
            env.Engine.Compile("SELECT lower(status) FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public void C01_OracleDefaultAllowlist_AllowsStandardOracleFunctionsAndDeniesRest()
    {
        var env = Env(TargetSqlDialect.Oracle);
        var request = env.Request() with { AllowedFunctions = SqlFunctionAllowlists.GetDefault(TargetSqlDialect.Oracle) };

        env.Engine.Compile("SELECT nvl(status, 'default') FROM orders".AsMemory(), request, CancellationToken.None);
        Assert.ThrowsAny<SecurityException>(() =>
            env.Engine.Compile("SELECT sys_context('USERENV', 'ISATT') FROM orders".AsMemory(), request, CancellationToken.None));
    }

    // ---------------------------------------------------------------- H-14

    [Theory]
    [InlineData("SELECT * FROM TABLE(pg.system.query(query => 'SELECT * FROM orders'))")]
    [InlineData("SELECT * FROM TABLE(some_ptf(input => TABLE(orders)))")]
    public void H14_TableFunctionInvocation_IsRejected(string sql)
    {
        Assert.ThrowsAny<SecurityException>(() => Env().Compile(sql));
    }

    [Fact]
    public void H14_WithSession_IsRejected()
    {
        string sql = "WITH SESSION key = 'value' SELECT 1";

        Assert.ThrowsAny<SecurityException>(() => Env().Compile(sql));
        Assert.True(new FastSqlEngine().Analyze(sql.AsMemory()).HasSessionProperties);
    }

    [Fact]
    public void H14_WithFunction_IsRejected()
    {
        string sql = "WITH FUNCTION foo() RETURNS bigint RETURN 42 SELECT 1";

        Assert.ThrowsAny<SecurityException>(() => Env().Compile(sql));
        Assert.True(new FastSqlEngine().Analyze(sql.AsMemory()).HasInlineFunctionDefinitions);
    }

    // ---------------------------------------------------------------- H-15

    [Theory]
    [InlineData("UPDATE orders SET status = email WHERE id = 1")]
    [InlineData("UPDATE orders SET status = orders.email")]
    [InlineData("UPDATE orders SET status = 'x' WHERE email LIKE '1%'")]
    [InlineData("UPDATE orders SET email = 'overwritten' WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE email LIKE '1%'")]
    [InlineData("DELETE FROM orders WHERE id IN (SELECT id FROM orders WHERE \"EMAIL\" = '123')")]
    public void H15_DmlReferencingMaskedColumns_IsRejected(string sql)
    {
        var env = Env();
        env.MaskEmail();

        Assert.ThrowsAny<SecurityException>(() => env.Compile(sql));
    }

    [Fact]
    public void H15_DmlOnUnmaskedColumns_StillWorks()
    {
        var env = Env();
        env.MaskEmail();

        var compiled = env.Compile("UPDATE orders SET status = 'x' WHERE id = 1");

        Assert.Equal(SqlStatementClass.Update, compiled.StatementClass);
        Assert.True(Tenants(compiled) > 0);
    }

    // ---------------------------------------------------------------- M-22

    private static string CompileWithMaxRows(string sql, TargetSqlDialect dialect = TargetSqlDialect.PostgreSql)
    {
        var env = Env(dialect);
        return env.Engine.Compile(sql.AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None).Sql.Trim();
    }

    [Theory]
    [InlineData("WITH x AS (SELECT * FROM orders LIMIT 5) SELECT * FROM x")]
    [InlineData("SELECT * FROM orders WHERE id IN (SELECT id FROM entitlements LIMIT 10)")]
    [InlineData("SELECT * FROM orders WHERE EXISTS (SELECT 1 FROM entitlements LIMIT 1)")]
    [InlineData("SELECT * FROM (SELECT * FROM orders LIMIT 3) c")]
    public void M22_InnerLimit_DoesNotConsumeEnforcedRootLimit(string sql)
    {
        Assert.EndsWith("LIMIT 100", CompileWithMaxRows(sql));
    }

    [Fact]
    public void M22_FetchFirst_IsClampedAtRoot()
    {
        string secured = CompileWithMaxRows("SELECT * FROM orders FETCH FIRST 5000 ROWS ONLY");

        Assert.DoesNotContain("5000", secured);
        Assert.EndsWith("100", secured);
    }

    [Fact]
    public void M22_FetchWithTies_IsDowngradedToOnly_SoTheRowCapHolds()
    {
        string secured = CompileWithMaxRows("SELECT * FROM orders ORDER BY 1 FETCH FIRST 5 ROWS WITH TIES");

        // WITH TIES can return more rows than asked for; under an enforced cap it must not
        Assert.DoesNotContain("TIES", secured);
        Assert.EndsWith("LIMIT 5", secured);
    }

    [Fact]
    public void M22_FetchWithTies_AboveTheCap_IsClampedAndDowngraded()
    {
        string secured = CompileWithMaxRows("SELECT * FROM orders ORDER BY 1 FETCH FIRST 5000 ROWS WITH TIES");

        Assert.DoesNotContain("TIES", secured);
        Assert.DoesNotContain("5000", secured);
        Assert.EndsWith("LIMIT 100", secured);
    }

    // ---------------------------------------------------------------- M-23

    [Theory]
    [InlineData("INSERT INTO orders VALUES (1, 'other')")]
    [InlineData("INSERT INTO orders (id, tenantid) SELECT 1, 'acme' UNION ALL SELECT 2, 'other'")]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, 'ac' || 'me')")]
    [InlineData("INSERT INTO orders (id, tenantid) SELECT 1, 'ac' || 'me'")]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2, 'other'), (3, 'acme')")]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, '''acme''')")]
    [InlineData("INSERT INTO orders (id, tenantid) SELECT id, tenantid FROM entitlements")]
    [InlineData("INSERT INTO orders (id, tenantid) SELECT * FROM entitlements")]
    [InlineData("INSERT INTO orders (id, tenantid) TABLE entitlements")]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, -42)")]
    public void M23_InsertWithCheckBypasses_AreRejected(string sql)
    {
        Assert.ThrowsAny<SecurityException>(() => Env().Compile(sql));
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2, 'acme')")]
    [InlineData("INSERT INTO orders (id, tenantid) SELECT 1, 'acme' UNION ALL SELECT 2, 'acme'")]
    [InlineData("INSERT INTO entitlements (tenantid) VALUES ('acme')")]
    public void M23_LegitimateInserts_StillWork(string sql)
    {
        var compiled = Env().Compile(sql);

        Assert.Equal(SqlStatementClass.Insert, compiled.StatementClass);
    }

    [Fact]
    public void M23_UnquoteStringLiteral_UnescapesQuotes()
    {
        Assert.Equal("'42'", SqlIdentifierHelper.UnquoteStringLiteral("\'\'\'42\'\'\'"));
    }

    // ---------------------------------------------------------------- DML guardrails (RejectUnfilteredDml)

    [Theory]
    [InlineData("DELETE FROM orders")]
    [InlineData("UPDATE orders SET status = 'shipped'")]
    public void DML_UpdateOrDeleteWithoutWhere_IsRejected(string sql)
    {
        // The injected tenant predicate would make the statement look filtered; the guardrail checks the original statement.
        Assert.Throws<UnfilteredDmlException>(() => Env().Compile(sql));
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE 1=1")]
    [InlineData("DELETE FROM orders WHERE 1 = 1.0")]
    [InlineData("DELETE FROM orders WHERE true")]
    [InlineData("DELETE FROM orders WHERE (1=1)")]
    [InlineData("DELETE FROM orders WHERE id = 10 OR 1=1")]
    [InlineData("DELETE FROM orders WHERE 1=1 AND true")]
    [InlineData("DELETE FROM orders WHERE NOT false")]
    [InlineData("DELETE FROM orders WHERE 1 <> 2")]
    [InlineData("DELETE FROM orders WHERE id = id")]
    [InlineData("UPDATE orders SET status = 'shipped' WHERE 'a' = 'a'")]
    [InlineData("UPDATE orders SET status = 'shipped' WHERE 1 = 1")]
    [InlineData("DELETE FROM orders WHERE id = 3 AND 1 = 0")]   // stricter: the constant-false conjunct folds the column reference away
    [InlineData("DELETE FROM orders WHERE 1 = 0")]   // stricter than the legacy rewriter: a literal-only predicate never counts as filtered
    public void DML_TriviallyTrueWhere_IsRejected(string sql)
    {
        Assert.Throws<UnfilteredDmlException>(() => Env().Compile(sql));
    }

    [Theory]
    [InlineData("UPDATE orders SET status = 'shipped' WHERE id = 10")]
    [InlineData("UPDATE orders SET status = 'shipped' WHERE id = 10 AND 1 = 1")]
    [InlineData("DELETE FROM orders WHERE id = 1 OR id = 2")]
    [InlineData("DELETE FROM orders WHERE id = 3 AND amount < 5")]
    [InlineData("DELETE FROM orders WHERE amount < 5")]
    public void DML_RestrictingWhere_IsAllowed_AndTheTenantIsBound(string sql)
    {
        var compiled = Env().Compile(sql);

        Assert.True(Tenants(compiled) > 0);
    }

    // ================================================================ Round 4 (Nachpruefung 2026-10-02)

    // ---------------------------------------------------------------- P-01

    [Theory]
    [InlineData("SELECT (name).f(1) FROM orders")]
    [InlineData("SELECT x[1].f() FROM orders")]
    [InlineData("SELECT mytype::f(1) FROM orders")]
    public void R4_P01_MethodCallSyntax_IsRejectedByCompilerAndAnalyzer(string sql)
    {
        // rejected either by the token guards (brackets) or as a method call; never compiled
        var compileEx = Record.Exception(() => Env().Compile(sql));
        Assert.True(compileEx is SecurityException or ParseCanceledException, compileEx?.GetType().Name ?? "no exception");
        Assert.Throws<SecurityException>(() => new FastSqlEngine().Analyze(sql.AsMemory()));
    }

    [Fact]
    public void R4_P01_QualifiedFunctionCalls_AreOnlyAllowedWhenTheDialectMapsThem()
    {
        const string sql = "SELECT util.\"normalize\"(status), upper(status) FROM orders";
        var meta = new FastSqlEngine().Analyze(sql.AsMemory());

        // the analyzer reports the qualified name; the compiler has no rule for it and fails closed even if a caller lists it
        Assert.Contains("util.normalize", meta.FunctionCalls!);
        var env = Env();
        var request = env.Request() with { AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "util.normalize", "upper" } };
        Assert.ThrowsAny<SecurityException>(() => env.Engine.Compile(sql.AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public void R4_P01_QualifiedXmlStyleMethod_IsRejectedInAllowlistMode()
    {
        var env = Env();
        var request = env.Request() with { AllowedFunctions = SqlFunctionAllowlists.SqlServer };

        Assert.ThrowsAny<SecurityException>(() =>
            env.Engine.Compile("SELECT doc.value('/a', 'int') FROM orders".AsMemory(), request, CancellationToken.None));
    }

    // ---------------------------------------------------------------- P-03

    [Theory]
    [InlineData("SELECT 'a\\b' FROM orders")]
    [InlineData("SELECT E'abc' FROM orders")]
    [InlineData("SELECT id FROM orders -- comment")]
    [InlineData("SELECT $$x$$ FROM orders")]
    [InlineData("SELECT * FROM \"sales.orders\"")]
    [InlineData("SELECT * FROM orders FOR VERSION AS OF 1")]
    public void R4_P03_TokenChecks_RunWhenNestingCheckIsDisabled(string sql)
    {
        var engine = new FastSqlEngine { MaxNestingDepth = 0 };
        var env = Env(TargetSqlDialect.PostgreSql);

        Assert.ThrowsAny<ParseCanceledException>(() => engine.Compile(sql.AsMemory(), env.Request(), CancellationToken.None));
    }

    // ---------------------------------------------------------------- P-04

    [Fact]
    public void R4_P04_Compile_DoesNotMutateEngineSwitches()
    {
        var engine = new FastSqlEngine();
        var env = Env(TargetSqlDialect.PostgreSql);
        var strict = env.Request(guards: SqlTokenSecurityOptions.Strict);

        engine.Compile("SELECT id FROM orders".AsMemory(), strict, CancellationToken.None);
        Assert.Throws<ParseCanceledException>(() => engine.Compile("SELECT id FROM orders -- c".AsMemory(), strict, CancellationToken.None));

        Assert.False(engine.RejectComments);
        Assert.False(engine.RejectBackslashInStrings);
        Assert.False(engine.RejectEscapedStringLiterals);
        Assert.False(engine.RejectDollarQuoting);
        Assert.False(engine.RejectNonAsciiIdentifiers);
        Assert.False(engine.RejectDotsInQuotedIdentifiers);
        Assert.False(engine.RejectTimeTravelQueries);

        // Engine defaults (direct parsing) are unaffected by the strict compile.
        Assert.NotNull(engine.Parse("SELECT id FROM orders -- c".AsMemory()).Tree);
    }

    [Fact]
    public void R4_P04_ConcurrentCompilesWithDifferentGuards_DoNotInfluenceEachOther()
    {
        var engine = new FastSqlEngine();
        var env = Env(TargetSqlDialect.PostgreSql);
        var strict = env.Request(guards: SqlTokenSecurityOptions.Strict);
        var lenient = env.Request(guards: SqlTokenSecurityOptions.None);
        var failures = new ConcurrentBag<string>();

        Parallel.For(0, 64, i =>
        {
            const string sql = "SELECT id FROM orders -- note";
            if (i % 2 == 0)
            {
                try
                {
                    engine.Compile(sql.AsMemory(), strict, CancellationToken.None);
                    failures.Add($"strict call {i} accepted a comment");
                }
                catch (ParseCanceledException)
                {
                    // expected
                }
            }
            else
            {
                try
                {
                    engine.Compile(sql.AsMemory(), lenient, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failures.Add($"lenient call {i} failed: {ex.Message}");
                }
            }
        });

        Assert.Empty(failures);
        Assert.False(engine.RejectComments);
    }

    [Fact]
    public void R4_P04_TokenGuards_RejectDollarQuotingForSqlServer()
    {
        Assert.True(SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.SqlServer).RejectDollarQuoting);
        Assert.Throws<ParseCanceledException>(() => Env(TargetSqlDialect.SqlServer).Compile("SELECT $$x$$ FROM orders"));
    }

    // ---------------------------------------------------------------- P-06 / SQ-11 / SQ-13

    [Fact]
    public void R4_P06_StrictTokenGuards_RejectEveryDifferentialSwitch()
    {
        var strict = SqlTokenSecurityOptions.Strict;

        Assert.True(strict.RejectComments);
        Assert.True(strict.RejectBackslashInStrings);
        Assert.True(strict.RejectEscapedStringLiterals);
        Assert.True(strict.RejectDollarQuoting);
        Assert.True(strict.RejectNonAsciiIdentifiers);
        Assert.True(strict.RejectDotsInQuotedIdentifiers);
        Assert.True(strict.RejectTimeTravelQueries);
    }

    [Theory]
    [InlineData("SELECT * FROM orders FOR TIMESTAMP AS OF TIMESTAMP '2026-01-01 00:00:00'", "Time-travel")]
    [InlineData("SELECT * FROM orders FOR VERSION AS OF 1", "Time-travel")]
    [InlineData("SELECT * FROM \"sales.orders\"", "Dots inside quoted identifiers")]
    [InlineData("SELECT id FROM orders /* hidden */", "SQL comments are not permitted")]
    public void R4_P06_CompileWithStrictGuards_RejectsDifferentialSyntax(string sql, string expectedMessage)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env().Compile(sql));

        Assert.Contains(expectedMessage, ex.Message);
    }

    // ---------------------------------------------------------------- SQ-08 (governed parse thread)

    [Fact]
    public void R4_SQ08_DedicatedParserThread_WithConfiguredStack_ParsesNormalQueries()
    {
        var engine = new FastSqlEngine { ParseThreadStackSize = 4 * 1024 * 1024 };
        string nested = "SELECT " + new string('(', 90) + "1" + new string(')', 90) + " FROM orders";

        Assert.NotNull(engine.Parse(nested.AsMemory()).Tree);
        Assert.NotNull(engine.ParseExpression("a = 1 AND b IN (1, 2, 3)".AsMemory()).Tree);
        var compiled = engine.Compile("SELECT id FROM orders".AsMemory(), Env(TargetSqlDialect.PostgreSql).Request(), CancellationToken.None);
        Assert.True(Tenants(compiled) > 0);

        string tooDeep = "SELECT " + new string('(', 101) + "1" + new string(')', 101);
        var ex = Assert.Throws<ParseCanceledException>(() => engine.Parse(tooDeep.AsMemory()));
        Assert.Contains("nesting depth", ex.Message);

        Assert.Throws<ArgumentOutOfRangeException>(() => new FastSqlEngine { ParseThreadStackSize = 1024 });
    }

    // ---------------------------------------------------------------- SQ-06 / P-02

    [Theory]
    [InlineData("ts_stat")]
    [InlineData("setval")]
    [InlineData("nextval")]
    [InlineData("pg_logical_slot_get_changes")]
    [InlineData("has_table_privilege")]
    [InlineData("has_foo_privilege")]
    [InlineData("to_regclass")]
    [InlineData("inet_client_addr")]
    [InlineData("current_database")]
    [InlineData("version")]
    [InlineData("SESSION_CONTEXT")]
    [InlineData("CONTEXT_INFO")]
    [InlineData("OBJECT_DEFINITION")]
    [InlineData("ORIGINAL_LOGIN")]
    [InlineData("HOST_NAME")]
    [InlineData("load_extension")]
    [InlineData("readfile")]
    [InlineData("writefile")]
    [InlineData("zeroblob")]
    [InlineData("randomblob")]
    public void R4_P02_SensitiveFunctions_AreOnDenylist(string functionName)
    {
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault(functionName));
        foreach (var dialect in new[] { TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer })
        {
            Assert.ThrowsAny<SecurityException>(() => Env(dialect).Compile($"SELECT {functionName}('x'), id FROM orders"));
        }
    }

    [Fact]
    public void R4_SQ06_AnalyticsQuery_WithPostgreSqlAllowlist_Works()
    {
        var env = Env(TargetSqlDialect.PostgreSql);
        var request = env.Request() with { AllowedFunctions = SqlFunctionAllowlists.PostgreSql };
        const string sql = "SELECT region, count(*), sum(amount), round(avg(amount), 2), coalesce(max(email), 'n/a'), " +
                           "row_number() OVER (ORDER BY region) FROM orders GROUP BY region";

        var compiled = env.Engine.Compile(sql.AsMemory(), request, CancellationToken.None);

        Assert.True(Tenants(compiled) > 0);
        Assert.ThrowsAny<SecurityException>(() =>
            env.Engine.Compile("SELECT md5(email) FROM orders".AsMemory(), env.Request() with { AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "count" } }, CancellationToken.None));
    }

    [Fact]
    public void R4_SQ06_DenylistBeatsAllowlist()
    {
        var env = Env();
        var request = env.Request() with
        {
            AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ts_stat", "load_extension", "upper" }
        };

        Assert.ThrowsAny<SecurityException>(() => env.Engine.Compile("SELECT ts_stat('SELECT 1'), id FROM orders".AsMemory(), request, CancellationToken.None));
        Assert.ThrowsAny<SecurityException>(() => env.Engine.Compile("SELECT load_extension('x'), id FROM orders".AsMemory(), request, CancellationToken.None));
        env.Engine.Compile("SELECT upper(status) FROM orders".AsMemory(), request, CancellationToken.None);

        var built = SqlFunctionAllowlists.Build(TargetSqlDialect.PostgreSql, new[] { "md5", " TS_STAT ", "setval", "" });
        Assert.Contains("md5", built);
        Assert.DoesNotContain("ts_stat", built);
        Assert.DoesNotContain("setval", built);
    }
}
