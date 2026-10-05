namespace TrinoSqlEngine.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime.Misc;
using Xunit;

public class SecurityRemediationTests
{
    private readonly FastSqlEngine _engine = new();

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    // SEC P-06: RlsOptions rejects dots in quoted identifiers by default (SQ-11). The C-02 tests below verify the CTE
    // scoping logic for such identifiers and therefore opt out explicitly.
    private static RlsOptions AllowDottedQuotedIdentifiers() => new() { RejectDotsInQuotedIdentifiers = false };

    private static RlsOptions DmlOptions() => new()
    {
        EnforceReadOnlyQueries = false,
        EnforceWithCheckOption = true,
        ExpectedTenantValue = "42"
    };

    // ---------------------------------------------------------------- C-02

    [Fact]
    public void C02_CteWithDottedName_DoesNotShadowQualifiedTable()
    {
        string sql = "WITH \"sales.orders\" AS (SELECT 1 x) SELECT * FROM sales.orders";

        string secured = _engine.RewriteRls(sql.AsMemory(), AllowDottedQuotedIdentifiers());

        Assert.Contains("(SELECT * FROM sales.orders WHERE tenant_id = 42)", secured);
    }

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
    public void C02_QuotedCteWithDifferentCase_DoesNotShadowUnquotedTable()
    {
        string sql = "WITH \"Orders\" AS (SELECT 1 x) SELECT * FROM orders";

        string secured = _engine.RewriteRls(sql.AsMemory(), AllowDottedQuotedIdentifiers());

        Assert.Contains("(SELECT * FROM orders WHERE tenant_id = 42)", secured);
    }

    [Fact]
    public void C02_SinglePartCteReference_IsStillTreatedAsCte()
    {
        string sql = "WITH \"sales.orders\" AS (SELECT 1 x) SELECT * FROM \"sales.orders\"";

        string secured = _engine.RewriteRls(sql.AsMemory(), AllowDottedQuotedIdentifiers());
        var meta = _engine.Analyze(sql.AsMemory());

        Assert.Equal(sql, secured);
        Assert.Empty(meta.ReferencedTables);
    }

    // ---------------------------------------------------------------- C-06

    [Fact]
    public void C06_DeeplyNestedParentheses_AreRejectedBeforeParsing()
    {
        string sql = "SELECT " + new string('(', 5000) + "1" + new string(')', 5000);

        Assert.ThrowsAny<ParseCanceledException>(() => _engine.Parse(sql.AsMemory()));
    }

    [Fact]
    public void C06_DeeplyNestedSubqueries_AreRejected()
    {
        string sql = Repeat("SELECT * FROM (", 1000) + "SELECT 1" + Repeat(")", 1000);

        Assert.ThrowsAny<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory()));
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
    public void C01_DangerousFunctions_AreRejected(string sql)
    {
        Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory()));
    }

    [Fact]
    public void C01_RegularFunctions_StillWork()
    {
        string sql = "SELECT upper(name), count(*) FROM orders GROUP BY upper(name)";

        string secured = _engine.RewriteRls(sql.AsMemory());

        Assert.Contains("(SELECT * FROM orders WHERE tenant_id = 42)", secured);
    }

    [Fact]
    public void C01_FunctionPolicy_CanBeDisabled()
    {
        string sql = "SELECT current_setting('app.tenant')";

        string secured = _engine.RewriteRls(sql.AsMemory(), new RlsOptions { EnforceFunctionPolicy = false });

        Assert.Equal(sql, secured);
    }

    [Fact]
    public void C01_AllowlistMode_PermitsOnlyListedFunctions()
    {
        var options = new RlsOptions
        {
            AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upper" }
        };

        _engine.RewriteRls("SELECT upper(name) FROM orders".AsMemory(), options);
        Assert.Throws<SecurityException>(() => _engine.RewriteRls("SELECT lower(name) FROM orders".AsMemory(), options));
    }

    [Fact]
    public void C01_AdditionalDeniedFunctions_AreRejected()
    {
        var options = new RlsOptions
        {
            AdditionalDeniedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lower" }
        };

        Assert.Throws<SecurityException>(() => _engine.RewriteRls("SELECT lower(name) FROM orders".AsMemory(), options));
    }

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
    public void H14_TableFunctionInvocation_IsRejectedByDefault()
    {
        string sql = "SELECT * FROM TABLE(pg.system.query(query => 'SELECT * FROM orders'))";

        Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory()));
    }

    [Fact]
    public void H14_TableFunctionInvocation_AllowlistedFunctionWorks()
    {
        string sql = "SELECT * FROM TABLE(some_ptf(input => TABLE(orders)))";
        var options = new RlsOptions { AllowedTableFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "some_ptf" } };

        string secured = _engine.RewriteRls(sql.AsMemory(), options);

        Assert.Contains("(SELECT * FROM orders WHERE tenant_id = 42)", secured);
        Assert.Throws<SecurityException>(() =>
            _engine.RewriteRls("SELECT * FROM TABLE(system.query(query => 'SELECT 1'))".AsMemory(), options));
    }

    [Fact]
    public void H14_WithSession_IsRejectedByDefault()
    {
        string sql = "WITH SESSION key = 'value' SELECT 1";

        Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory()));
        Assert.True(_engine.Analyze(sql.AsMemory()).HasSessionProperties);
    }

    [Fact]
    public void H14_WithSession_AllowlistedPropertyWorks()
    {
        string sql = "WITH SESSION key = 'value' SELECT 1";
        var options = new RlsOptions { AllowedSessionProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "key" } };

        string secured = _engine.RewriteRls(sql.AsMemory(), options);

        Assert.Equal(sql, secured);
    }

    [Fact]
    public void H14_WithFunction_IsRejectedByDefault()
    {
        string sql = "WITH FUNCTION foo() RETURNS bigint RETURN 42 SELECT 1";

        Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory()));
        Assert.True(_engine.Analyze(sql.AsMemory()).HasInlineFunctionDefinitions);
        Assert.Equal(sql, _engine.RewriteRls(sql.AsMemory(), new RlsOptions { AllowInlineFunctionDefinitions = true }));
    }

    // ---------------------------------------------------------------- H-15

    private static RlsOptions MaskedDmlOptions()
    {
        var options = DmlOptions();
        options.ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
            hasMaskPredicate: (tbl, col) => tbl.Equals("customers", StringComparison.OrdinalIgnoreCase) &&
                                            col.Equals("ssn", StringComparison.OrdinalIgnoreCase),
            maskExpressionProvider: (_, _) => "'***'");
        return options;
    }

    [Theory]
    [InlineData("UPDATE customers SET notes = ssn WHERE id = 1")]
    [InlineData("UPDATE customers SET notes = customers.ssn")]
    [InlineData("UPDATE customers SET notes = 'x' WHERE ssn LIKE '1%'")]
    [InlineData("UPDATE customers SET ssn = 'overwritten' WHERE id = 1")]
    [InlineData("DELETE FROM customers WHERE ssn LIKE '1%'")]
    [InlineData("DELETE FROM customers WHERE id IN (SELECT id FROM customers WHERE \"SSN\" = '123')")]
    public void H15_DmlReferencingMaskedColumns_IsRejected(string sql)
    {
        Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory(), MaskedDmlOptions()));
    }

    [Fact]
    public void H15_DmlOnUnmaskedColumns_StillWorks()
    {
        string secured = _engine.RewriteRls("UPDATE customers SET notes = 'x' WHERE id = 1".AsMemory(), MaskedDmlOptions());

        Assert.Equal("UPDATE customers SET notes = 'x' WHERE (tenant_id = 42) AND (id = 1)", secured);
    }

    // ---------------------------------------------------------------- M-22

    private static RlsOptions MaxRowsOptions() => new()
    {
        EnforcedMaxRows = 100,
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'")
    };

    [Theory]
    [InlineData("WITH x AS (SELECT * FROM customers LIMIT 5) SELECT * FROM x")]
    [InlineData("SELECT * FROM customers WHERE id IN (SELECT id FROM orders LIMIT 10)")]
    [InlineData("SELECT * FROM customers WHERE EXISTS (SELECT 1 FROM orders LIMIT 1)")]
    [InlineData("SELECT * FROM (SELECT * FROM customers LIMIT 3) c")]
    public void M22_InnerLimit_DoesNotConsumeEnforcedRootLimit(string sql)
    {
        string secured = _engine.RewriteRls(sql.AsMemory(), MaxRowsOptions());

        Assert.EndsWith("LIMIT 100", secured.Trim());
    }

    [Fact]
    public void M22_FetchFirst_IsClampedAtRoot()
    {
        string secured = _engine.RewriteRls("SELECT * FROM customers FETCH FIRST 5000 ROWS ONLY".AsMemory(), MaxRowsOptions());

        Assert.Contains("FETCH FIRST 100 ROWS ONLY", secured);
        Assert.DoesNotContain("5000", secured);
        Assert.DoesNotContain("LIMIT", secured);
    }

    [Fact]
    public void M22_FetchWithTies_IsDowngradedToOnly()
    {
        string secured = _engine.RewriteRls("SELECT * FROM customers ORDER BY 1 FETCH FIRST 5 ROWS WITH TIES".AsMemory(), MaxRowsOptions());

        Assert.DoesNotContain("TIES", secured);
        Assert.EndsWith("FETCH FIRST 5 ROWS ONLY", secured.Trim());
    }

    [Fact]
    public void M22_Analyzer_IgnoresCteLimitAsRootLimit()
    {
        var meta = _engine.Analyze("WITH x AS (SELECT * FROM customers LIMIT 5) SELECT * FROM x".AsMemory());

        Assert.False(meta.HasExplicitLimit);
    }

    // ---------------------------------------------------------------- M-23

    [Fact]
    public void M23_RequireTenantColumnInInsert_DefaultsToTrue()
    {
        Assert.True(new RlsOptions().RequireTenantColumnInInsert);
    }

    [Theory]
    [InlineData("INSERT INTO orders VALUES (1, 99)")]
    [InlineData("INSERT INTO orders (id, tenant_id) SELECT 1, 42 UNION ALL SELECT 2, 99")]
    [InlineData("INSERT INTO orders (id, tenant_id) VALUES (1, 40+2)")]
    [InlineData("INSERT INTO orders (id, tenant_id) SELECT 1, 40+3")]
    [InlineData("INSERT INTO orders (id, tenant_id) VALUES (1, 42), (2, 41), (3, 42)")]
    [InlineData("INSERT INTO orders (id, tenant_id) VALUES (1, '''42''')")]
    [InlineData("INSERT INTO orders (id, tenant_id) SELECT id, tenant_id FROM staging")]
    [InlineData("INSERT INTO orders (id, tenant_id) SELECT * FROM staging")]
    [InlineData("INSERT INTO orders (id, tenant_id) TABLE staging")]
    [InlineData("INSERT INTO orders (id, tenant_id) VALUES (1, -42)")]
    public void M23_InsertWithCheckBypasses_AreRejected(string sql)
    {
        Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory(), DmlOptions()));
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, tenant_id) VALUES (1, '42'), (2, 42)")]
    [InlineData("INSERT INTO orders (id, tenant_id) SELECT 1, 42 UNION ALL SELECT 2, '42'")]
    [InlineData("INSERT INTO orders (tenant_id) VALUES (42)")]
    public void M23_LegitimateInserts_StillWork(string sql)
    {
        string secured = _engine.RewriteRls(sql.AsMemory(), DmlOptions());

        Assert.Equal(sql, secured);
    }

    [Fact]
    public void M23_QuotedExpectedTenantValue_IsUnquotedCorrectly()
    {
        var options = DmlOptions();
        options.ExpectedTenantValue = "'t''1'";

        _engine.RewriteRls("INSERT INTO orders (id, tenant_id) VALUES (1, 't''1')".AsMemory(), options);
        Assert.Throws<SecurityException>(() =>
            _engine.RewriteRls("INSERT INTO orders (id, tenant_id) VALUES (1, 't1')".AsMemory(), options));
        Assert.Equal("'42'", SqlIdentifierHelper.UnquoteStringLiteral("'''42'''"));
    }

    // ---------------------------------------------------------------- M-24

    [Fact]
    public void M24_CatalogColumnNames_AreQuotedAsIdentifiers()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TableColumnsProvider = _ => new[] { "id, ssn AS id2", "na\"me", "ssn" },
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                hasMaskPredicate: (_, col) => col == "ssn",
                maskExpressionProvider: (_, _) => "substr(ssn, 1, 2)")
        };

        string secured = _engine.RewriteRls("SELECT * FROM customers".AsMemory(), options);

        Assert.Equal(
            "SELECT * FROM (SELECT \"id, ssn AS id2\", \"na\"\"me\", substr(ssn, 1, 2) AS \"ssn\" FROM customers WHERE tenant_id = 't1')",
            secured);
    }

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
    public void DML_RejectUnfilteredDml_IsEnabledByDefault()
    {
        Assert.True(new RlsOptions().RejectUnfilteredDml);
    }

    [Theory]
    [InlineData("DELETE FROM orders")]
    [InlineData("UPDATE orders SET status = 'shipped'")]
    public void DML_UpdateOrDeleteWithoutWhere_IsRejected(string sql)
    {
        // The RLS rewrite would append its own WHERE (tenant filter); the guardrail checks the original statement.
        Assert.Throws<UnfilteredDmlException>(() => _engine.RewriteRls(sql.AsMemory(), DmlOptions()));
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
    public void DML_TriviallyTrueWhere_IsRejected(string sql)
    {
        Assert.Throws<UnfilteredDmlException>(() => _engine.RewriteRls(sql.AsMemory(), DmlOptions()));
    }

    [Fact]
    public void DML_DeleteWithRealWhere_IsAllowed_AndTenantFilterIsInjected()
    {
        string secured = _engine.RewriteRls("DELETE FROM orders WHERE id = 10".AsMemory(), DmlOptions());

        Assert.Equal("DELETE FROM orders WHERE (tenant_id = 42) AND (id = 10)", secured);
    }

    [Theory]
    [InlineData("UPDATE orders SET status = 'shipped' WHERE id = 10")]
    [InlineData("UPDATE orders SET status = 'shipped' WHERE id = 10 AND 1 = 1")]
    [InlineData("DELETE FROM orders WHERE id = 1 OR id = 2")]
    [InlineData("DELETE FROM orders WHERE 1 = 0")]
    [InlineData("DELETE FROM orders WHERE created_at < '2020-01-01'")]
    public void DML_RestrictingWhere_IsAllowed(string sql)
    {
        string secured = _engine.RewriteRls(sql.AsMemory(), DmlOptions());

        Assert.Contains("(tenant_id = 42) AND (", secured);
    }

    [Fact]
    public void DML_GuardrailCanBeDisabledExplicitly()
    {
        var options = DmlOptions();
        options.RejectUnfilteredDml = false;

        string secured = _engine.RewriteRls("DELETE FROM orders".AsMemory(), options);

        Assert.Equal("DELETE FROM orders WHERE (tenant_id = 42)", secured);
    }

    // ================================================================ Round 4 (Nachprüfung 2026-10-02)

    // ---------------------------------------------------------------- P-01

    [Theory]
    [InlineData("SELECT (name).f(1) FROM orders")]
    [InlineData("SELECT x[1].f() FROM orders")]
    [InlineData("SELECT mytype::f(1) FROM orders")]
    public void R4_P01_MethodCallSyntax_IsRejectedByRewriterAndAnalyzer(string sql)
    {
        Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory()));
        Assert.Throws<SecurityException>(() => _engine.Analyze(sql.AsMemory()));
    }

    [Fact]
    public void R4_P01_MethodCallSyntax_IsRejectedEvenWithoutFunctionPolicy()
    {
        var options = new RlsOptions { EnforceFunctionPolicy = false };

        Assert.Throws<SecurityException>(() => _engine.RewriteRls("SELECT (name).f(1) FROM orders".AsMemory(), options));
        Assert.Throws<SecurityException>(() => _engine.RewriteRls("SELECT mytype::f(1) FROM orders".AsMemory(), options));
    }

    [Fact]
    public void R4_P01_QualifiedFunctionCalls_StillWork()
    {
        var options = new RlsOptions
        {
            AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "util.normalize", "upper" }
        };

        string secured = _engine.RewriteRls("SELECT util.\"normalize\"(name), upper(name) FROM orders".AsMemory(), options);
        var meta = _engine.Analyze("SELECT util.\"normalize\"(name), upper(name) FROM orders".AsMemory());

        Assert.Contains("(SELECT * FROM orders WHERE tenant_id = 42)", secured);
        Assert.Contains("util.normalize", meta.FunctionCalls!);
    }

    [Fact]
    public void R4_P01_QualifiedXmlStyleMethod_IsRejectedInAllowlistMode()
    {
        // a.f(x) is parsed as a qualified function call; with an allowlist only listed (qualified) names pass.
        var options = new RlsOptions { AllowedFunctions = SqlFunctionAllowlists.SqlServer };

        Assert.Throws<SecurityException>(() =>
            _engine.RewriteRls("SELECT doc.value('/a', 'int') FROM orders".AsMemory(), options));
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

        Assert.ThrowsAny<ParseCanceledException>(() => engine.RewriteRls(sql.AsMemory(), new RlsOptions()));
    }

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
    public void R4_P04_RewriteRls_DoesNotMutateEngineSwitches()
    {
        var engine = new FastSqlEngine();
        var strict = new RlsOptions
        {
            RejectComments = true,
            RejectBackslashInStrings = true,
            RejectEscapedStringLiterals = true,
            RejectDollarQuoting = true,
            RejectNonAsciiIdentifiers = true,
            RejectDotsInQuotedIdentifiers = true,
            RejectTimeTravelQueries = true
        };

        engine.RewriteRls("SELECT id FROM orders".AsMemory(), strict);
        Assert.Throws<ParseCanceledException>(() => engine.RewriteRls("SELECT id FROM orders -- c".AsMemory(), strict));

        Assert.False(engine.RejectComments);
        Assert.False(engine.RejectBackslashInStrings);
        Assert.False(engine.RejectEscapedStringLiterals);
        Assert.False(engine.RejectDollarQuoting);
        Assert.False(engine.RejectNonAsciiIdentifiers);
        Assert.False(engine.RejectDotsInQuotedIdentifiers);
        Assert.False(engine.RejectTimeTravelQueries);

        // Engine defaults (direct parsing) are unaffected by the strict rewrite.
        Assert.NotNull(engine.Parse("SELECT id FROM orders -- c".AsMemory()).Tree);
    }

    [Fact]
    public void R4_P04_ConcurrentRewritesWithDifferentOptions_DoNotInfluenceEachOther()
    {
        var engine = new FastSqlEngine();
        var strict = new RlsOptions { RejectComments = true };
        var lenient = new RlsOptions { RejectComments = false };
        var failures = new ConcurrentBag<string>();

        Parallel.For(0, 64, i =>
        {
            const string sql = "SELECT id FROM orders -- note";
            if (i % 2 == 0)
            {
                try
                {
                    engine.RewriteRls(sql.AsMemory(), strict);
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
                    engine.RewriteRls(sql.AsMemory(), lenient);
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
    public void R4_P04_TokenOptionsFromRlsOptions_RejectDollarQuotingForSqlServer()
    {
        var tokenOptions = SqlTokenSecurityOptions.FromRlsOptions(new RlsOptions
        {
            RejectDollarQuoting = false,
            TargetDialect = TargetSqlDialect.SqlServer
        });

        Assert.True(tokenOptions.RejectDollarQuoting);
    }

    // ---------------------------------------------------------------- P-06 / SQ-11 / SQ-13

    [Fact]
    public void R4_P06_RlsOptions_HaveSecureTokenDefaults()
    {
        var options = new RlsOptions();

        Assert.True(options.RejectComments);
        Assert.True(options.RejectBackslashInStrings);
        Assert.True(options.RejectEscapedStringLiterals);
        Assert.True(options.RejectDollarQuoting);
        Assert.True(options.RejectNonAsciiIdentifiers);
        Assert.True(options.RejectDotsInQuotedIdentifiers);
        Assert.True(options.RejectTimeTravelQueries);
    }

    [Theory]
    [InlineData("SELECT * FROM orders FOR TIMESTAMP AS OF TIMESTAMP '2026-01-01 00:00:00'", "Time-travel")]
    [InlineData("SELECT * FROM orders FOR VERSION AS OF 1", "Time-travel")]
    [InlineData("SELECT * FROM \"sales.orders\"", "Dots inside quoted identifiers")]
    [InlineData("SELECT id FROM orders /* hidden */", "SQL comments are not permitted")]
    public void R4_P06_RewriteWithDefaultOptions_RejectsDifferentialSyntax(string sql, string expectedMessage)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory()));

        Assert.Contains(expectedMessage, ex.Message);
    }

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
    public void R4_SQ08_DedicatedParserThread_WithConfiguredStack_ParsesNormalQueries()
    {
        var engine = new FastSqlEngine { ParseThreadStackSize = 4 * 1024 * 1024 };
        string nested = "SELECT " + new string('(', 90) + "1" + new string(')', 90) + " FROM orders";

        Assert.NotNull(engine.Parse(nested.AsMemory()).Tree);
        Assert.NotNull(engine.ParseExpression("a = 1 AND b IN (1, 2, 3)".AsMemory()).Tree);
        Assert.Contains("tenant_id = 42", engine.RewriteRls("SELECT id FROM orders".AsMemory()));

        string tooDeep = "SELECT " + new string('(', 101) + "1" + new string(')', 101);
        var ex = Assert.Throws<ParseCanceledException>(() => engine.Parse(tooDeep.AsMemory()));
        Assert.Contains("nesting depth", ex.Message);

        Assert.Throws<ArgumentOutOfRangeException>(() => new FastSqlEngine { ParseThreadStackSize = 1024 });
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
        Assert.Throws<SecurityException>(() =>
            _engine.RewriteRls($"SELECT {functionName}('x'), id FROM orders".AsMemory()));
    }

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

    [Fact]
    public void R4_SQ06_AnalyticsQuery_WithPostgreSqlAllowlist_Works()
    {
        var options = new RlsOptions { AllowedFunctions = SqlFunctionAllowlists.PostgreSql, TargetDialect = TargetSqlDialect.PostgreSql };
        string sql = "SELECT region, count(*), sum(amount), round(avg(amount), 2), date_trunc('month', created_at), " +
                     "coalesce(max(note), 'n/a'), row_number() OVER (ORDER BY region) FROM orders GROUP BY region, created_at";

        string secured = _engine.RewriteRls(sql.AsMemory(), options);

        Assert.Contains("(SELECT * FROM orders WHERE tenant_id = 42)", secured);
        Assert.Throws<SecurityException>(() => _engine.RewriteRls("SELECT md5(note) FROM orders".AsMemory(), options));
    }

    [Fact]
    public void R4_SQ06_DenylistBeatsAllowlist()
    {
        var options = new RlsOptions
        {
            AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ts_stat", "load_extension", "upper" }
        };

        Assert.Throws<SecurityException>(() => _engine.RewriteRls("SELECT ts_stat('SELECT 1'), id FROM orders".AsMemory(), options));
        Assert.Throws<SecurityException>(() => _engine.RewriteRls("SELECT load_extension('x'), id FROM orders".AsMemory(), options));
        _engine.RewriteRls("SELECT upper(name) FROM orders".AsMemory(), options);

        var built = SqlFunctionAllowlists.Build(TargetSqlDialect.PostgreSql, new[] { "md5", " TS_STAT ", "setval", "" });
        Assert.Contains("md5", built);
        Assert.DoesNotContain("ts_stat", built);
        Assert.DoesNotContain("setval", built);
    }
}
