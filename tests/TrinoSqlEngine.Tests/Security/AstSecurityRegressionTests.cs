using System.Security;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// WP-X0v (G7): the legacy SQ-/SQL- security regressions, asserted on the AST path (<c>Compile</c>) with the same or a stricter
/// outcome. The legacy suites (<c>SecurityReview20261002SqTests</c>, <c>Sql1BracketLexerDifferentialTests</c>,
/// <c>DmlLimitAndAliasSql78Tests</c>, <c>SecurityRemediationTests</c>) call <c>RewriteRls</c> and go away with the legacy rewriter
/// (WP-X1); these tests keep every ID guarded afterwards. <see cref="SecurityRegressionCatalog"/> maps each ID to the tests below,
/// and <see cref="SecurityRegressionTraceabilityTests"/> keeps that map complete. Assertions are semantic (exception type, bound
/// values, the structure of the emitted statement), never the legacy text format.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "SqlGate")]
public class AstSecurityRegressionTests
{
    private static readonly TargetSqlDialect[] Dialects =
    {
        TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks
    };

    public static IEnumerable<object[]> AllDialects() => Dialects.Select(d => new object[] { d });

    // ---- SQ-01: string literal lexer differential (E'...' and backslashes) ------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ01_BackslashInAStringLiteral_IsRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        foreach (string sql in new[]
                 {
                     "SELECT 'hello\\nworld' FROM orders", "SELECT 'abc\\'def' FROM orders", "SELECT '\\' FROM orders"
                 })
        {
            Assert.ThrowsAny<ParseCanceledException>(() => env.Compile(sql));
        }
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ01_EscapedStringTypeConstructor_IsRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        foreach (string sql in new[] { "SELECT E'hello' FROM orders", "SELECT e'test' FROM orders", "SELECT E'foo bar' FROM orders" })
        {
            Assert.ThrowsAny<ParseCanceledException>(() => env.Compile(sql));
        }
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ01_StandardEscapedSingleQuote_IsAllowedAndBound(TargetSqlDialect dialect)
    {
        var compiled = new ProbeEnv(dialect).Compile("SELECT id FROM orders WHERE status = 'O''Reilly'");
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "O'Reilly"));
        Assert.DoesNotContain("Reilly", compiled.Sql);
    }

    // ---- SQ-02: comment lexer differential ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ02_NestedBlockCommentsAndAllComments_AreRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        foreach (string sql in new[]
                 {
                     "SELECT /* outer /* nested */ comment */ 1 FROM orders", "SELECT /* /* double */ */ 1 FROM orders",
                     "SELECT 1 FROM orders -- end of line comment", "SELECT /* standard comment */ 1 FROM orders"
                 })
        {
            Assert.ThrowsAny<ParseCanceledException>(() => env.Compile(sql));
        }
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ02_DollarQuotedStrings_AreRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        Assert.ThrowsAny<ParseCanceledException>(() => env.Compile("SELECT $$dollar string$$ FROM orders"));
        Assert.ThrowsAny<ParseCanceledException>(() => env.Compile("SELECT $$line1\nline2$$ FROM orders"));
    }

    // ---- SQ-03: whole-row reference in UPDATE/DELETE bypassing masking ----------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ03_WholeRowReferenceInDml_IsRejected_WhenTheTableHasMaskedColumns(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        env.MaskEmail();
        Assert.ThrowsAny<SecurityException>(() => env.Compile("UPDATE orders SET status = CAST(orders AS VARCHAR) WHERE id = 1"));
        Assert.ThrowsAny<SecurityException>(() => env.Compile("DELETE FROM orders WHERE orders IS NOT NULL AND id = 1"));
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ03_DmlWithoutMaskedColumns_StillCompiles(TargetSqlDialect dialect)
    {
        var compiled = new ProbeEnv(dialect).Compile("UPDATE orders SET status = 'clean' WHERE id = 1");
        Assert.Equal(SqlStatementClass.Update, compiled.StatementClass);
    }

    // ---- SQ-04: cross-schema policy confusion via a short table name -------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ04_ATableInAnotherSchema_NeverResolvesToTheCatalogTableOfTheSameShortName(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        Assert.ThrowsAny<Exception>(() => env.Compile("SELECT id FROM schema_b.orders"));
        Assert.ThrowsAny<Exception>(() => env.Compile("SELECT id FROM \"schema_b\".\"Orders\""));

        // the short name resolves to the default schema of the catalog only, and is secured there
        var compiled = env.Compile("SELECT id FROM orders");
        ProbeAsserts.TenantPredicates(compiled, 1);
    }

    // ---- SQ-05: dialect rewriting: aliases and the row cap -----------------------------------------------------------------

    [Fact]
    public void SQ05_TableAlias_IsTheSimpleName_NeverSchemaQualified()
    {
        var compiled = new ProbeEnv(TargetSqlDialect.SqlServer).Compile("SELECT * FROM dbo.orders");
        Assert.Contains(") AS [orders]", compiled.Sql);
        Assert.DoesNotContain("[dbo.orders]", compiled.Sql);
    }

    [Fact]
    public void SQ05_SqlServer_RootRowCap_IsOffsetFetch_NotLimit()
    {
        var env = new ProbeEnv(TargetSqlDialect.SqlServer);
        var capped = env.Engine.Compile("SELECT id FROM orders ORDER BY id".AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY", capped.Sql);
        Assert.DoesNotContain("LIMIT", capped.Sql);

        var withLimit = env.Engine.Compile("SELECT id FROM orders LIMIT 20".AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);
        Assert.Contains("FETCH NEXT 20 ROWS ONLY", withLimit.Sql);
        Assert.DoesNotContain("LIMIT", withLimit.Sql);
    }

    // ---- SQ-06: sensitive function denylist --------------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT pg_notify('chan', 'payload')")]
    [InlineData("SELECT inet_server_addr()")]
    [InlineData("SELECT version()")]
    [InlineData("SELECT txid_current()")]
    [InlineData("SELECT pg_stat_reset()")]
    [InlineData("SELECT pg_get_expr('1', 1)")]
    [InlineData("SELECT fn_dblog(NULL, NULL)")]
    [InlineData("SELECT has_dbaccess('master')")]
    [InlineData("SELECT suser_sname()")]
    [InlineData("SELECT is_srvrolemember('sysadmin')")]
    public void SQ06_SensitiveBuiltinFunctions_AreRejected_OnEveryDialect(string sql)
    {
        foreach (var dialect in Dialects)
        {
            Assert.ThrowsAny<SecurityException>(() => new ProbeEnv(dialect).Compile(sql));
        }
    }

    // ---- SQ-07: INSERT into a table with a consent row filter -------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ07_InsertIntoATableWithAConsentRowFilter_IsRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        var request = env.Request();
        request = request with { Policy = request.Policy with { TablesWithConsentRowFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.orders", "orders" } } };
        Assert.ThrowsAny<SecurityException>(() =>
            env.Engine.Compile("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')".AsMemory(), request, CancellationToken.None));

        // a table without a consent filter takes the insert
        var ok = env.Engine.Compile("INSERT INTO entitlements (id, tenantid) VALUES (1, 'acme')".AsMemory(), request, CancellationToken.None);
        Assert.Equal(SqlStatementClass.Insert, ok.StatementClass);
    }

    // ---- SQ-08: native thread stack protection ---------------------------------------------------------------------------

    [Fact]
    public void SQ08_DeepNesting_IsRejectedBeforeTheStackRunsOut()
    {
        var engine = new FastSqlEngine { MaxNestingDepth = 10 };
        string nested = "SELECT " + new string('(', 15) + "1" + new string(')', 15);
        var ex = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(nested.AsMemory()));
        Assert.Contains("nesting depth", ex.Message);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ08_VeryDeepNesting_FailsClosedWithATypedError_OnTheAstPath(TargetSqlDialect dialect)
    {
        string deep = "SELECT id FROM orders WHERE " + new string('(', 3000) + "id = 1" + new string(')', 3000);
        var ex = Assert.ThrowsAny<Exception>(() => new ProbeEnv(dialect).Compile(deep));
        Assert.True(ex is ParseCanceledException or SqlLimitExceededException or SecurityException, ex.GetType().Name);
    }

    // ---- SQ-09: multi-part names (linked servers, catalogs) ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ09_FourPartNames_AreRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        Assert.ThrowsAny<Exception>(() => env.Compile("SELECT * FROM server.database.schema.table"));
        Assert.ThrowsAny<Exception>(() => env.Compile("SELECT * FROM srv.db.dbo.orders"));
    }

    /// <summary>
    /// FINDING (open, see the WP-X0v log): <c>Compile</c> ignores the catalog part of a three-part name. It resolves
    /// <c>othercatalog.dbo.orders</c> to the cataloged table and secures it, so isolation holds, but a foreign catalog is neither
    /// rejected (legacy SQL-5 rejected a mismatch in the WebSQL service) nor ever emitted. The gate pins the safe part.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ09_ThreePartName_NeverEmitsTheForeignCatalog_AndIsSecuredAsTheCatalogTable(TargetSqlDialect dialect)
    {
        var compiled = new ProbeEnv(dialect).Compile("SELECT id FROM othercatalog.dbo.orders");
        Assert.DoesNotContain("othercatalog", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        ProbeAsserts.TenantPredicates(compiled, 1);
    }

    // ---- SQ-10: non-ASCII case insensitivity is not folded ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ10_NonAsciiLookalikes_DoNotResolveToACatalogTable(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        // U+0131 (dotless i) upper-cases to I in the invariant culture, U+212A (Kelvin sign) lower-cases to k
        Assert.ThrowsAny<Exception>(() => env.Compile("SELECT id FROM entıtlements"));
        Assert.ThrowsAny<Exception>(() => env.Compile("SELECT id FROM ENTITLEMENTS_ß"));
        // ASCII folding still works
        ProbeAsserts.TenantPredicates(env.Compile("SELECT id FROM EnTiTlEmEnTs"), 1);
    }

    // ---- SQ-11: dots in quoted identifiers --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ11_DotsInQuotedIdentifiers_AreRejected(TargetSqlDialect dialect)
    {
        Assert.ThrowsAny<ParseCanceledException>(() => new ProbeEnv(dialect).Compile("SELECT * FROM \"my.table\""));
    }

    // ---- SQ-13: time travel ------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQ13_TimeTravelQueries_AreRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        Assert.ThrowsAny<ParseCanceledException>(() => env.Compile("SELECT * FROM orders FOR TIMESTAMP AS OF '2026-01-01'"));
        Assert.ThrowsAny<ParseCanceledException>(() => env.Compile("SELECT * FROM orders FOR VERSION AS OF 100"));
    }

    // ---- SQ-14: a pooled parser does not carry state from one compile to the next ----------------------------------------

    [Fact]
    public void SQ14_PooledParsers_DoNotLeakStateAcrossCompiles()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        Parallel.For(0, 64, i =>
        {
            var orders = env.Compile("SELECT id FROM orders WHERE id = " + i);
            var entitlements = env.Compile("SELECT id FROM entitlements WHERE id = " + i);
            Assert.Contains("\"Orders\"", orders.Sql);
            Assert.DoesNotContain("\"Entitlements\"", orders.Sql);
            Assert.Contains("\"Entitlements\"", entitlements.Sql);
            Assert.DoesNotContain("\"Orders\"", entitlements.Sql);
        });
    }

    // ---- SQL-1: bracket lexer differential --------------------------------------------------------------------------------

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT ARRAY[1,2][1] FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT '[' , x FROM orders WHERE a = ']--'")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT 'a/*b' FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT \"a[b\" FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT [id] FROM orders")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT U&'d!0061t' UESCAPE '!' FROM orders")]
    public void SQL1_BracketsAndBracketLiterals_AreRejected(TargetSqlDialect dialect, string sql)
    {
        Assert.ThrowsAny<ParseCanceledException>(() => new ProbeEnv(dialect).Compile(sql));
    }

    // ---- SQL-3: safe token emission ----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQL3_QuotedOrUnknownCastTypesAndExtractFields_AreRejected(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        foreach (string sql in new[]
                 {
                     "SELECT CAST(id AS \"int) FROM secrets --\") FROM orders", "SELECT CAST(id AS \"varchar\") FROM orders",
                     "SELECT EXTRACT(\"year) FROM secrets --\" FROM ts) FROM orders", "SELECT EXTRACT(foo FROM ts) FROM orders"
                 })
        {
            Assert.ThrowsAny<Exception>(() => env.Compile(sql));
        }
    }

    // ---- SQL-7: the row cap never ends up in a DML subquery ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQL7_DmlSubquery_HasNoRowCap_AndTheRootSelectKeepsIt(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        const string delete = "DELETE FROM orders WHERE id IN (SELECT id FROM orders o WHERE o.amount > 3)";
        if (dialect == TargetSqlDialect.Databricks)
        {
            // stricter than legacy: Delta has no subquery in a DML condition, so the statement is not compiled at all
            Assert.Throws<SqlCompileNotSupportedException>(() =>
                env.Engine.Compile(delete.AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None));
        }
        else
        {
            var dml = env.Engine.Compile(delete.AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);
            Assert.DoesNotContain("LIMIT", dml.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FETCH", dml.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ROWNUM", dml.Sql, StringComparison.OrdinalIgnoreCase);
            ProbeAsserts.TenantPredicates(dml, 2);
        }

        var select = env.Engine.Compile("SELECT id FROM orders".AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);
        Assert.Matches("LIMIT|FETCH|ROWNUM|TOP", select.Sql);
    }

    // ---- SQL-8: the alias of a rewritten table keeps the quoting of the written name ---------------------------------------

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "AS \"Orders\"")]
    [InlineData(TargetSqlDialect.SqlServer, "AS [Orders]")]
    public void SQL8_QuotedTableName_KeepsAQuotedAlias(TargetSqlDialect dialect, string expectedAlias)
    {
        var compiled = new ProbeEnv(dialect).Compile("SELECT \"Orders\".id FROM \"Orders\"");
        Assert.Contains(expectedAlias, compiled.Sql);
    }

    [Fact]
    public void SQL8_UnquotedTableName_KeepsAnUnquotedStyleAlias()
    {
        var compiled = new ProbeEnv(TargetSqlDialect.PostgreSql).Compile("SELECT orders.id FROM orders");
        Assert.Contains("AS \"orders\"", compiled.Sql);
    }

    // ---- SQL-2 and SQL-5: spelling variants and catalog parts of a name -------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQL2_TwoSpellingsOfOneTable_AreBothSecured_NeverOnePolicyForTwoRelations(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        env.RegionPolicy("EU");
        var compiled = env.Compile("SELECT a.id FROM dbo.Orders a JOIN dbo.orders b ON a.id = b.id");
        ProbeAsserts.TenantPredicates(compiled, 2);   // both references carry the tenant predicate
        Assert.Equal(1, compiled.Parameters.Count(p => p.Origin == ParameterOrigin.Policy));
    }

    /// <summary>The SQL-5 catalog check of the WebSQL service has no AST-path equivalent yet (see <see cref="SQ09_ThreePartName_NeverEmitsTheForeignCatalog_AndIsSecuredAsTheCatalogTable"/>).</summary>
    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQL5_ACatalogPart_IsNeverEmitted_AndEveryTableReferenceIsSecured(TargetSqlDialect dialect)
    {
        var compiled = new ProbeEnv(dialect).Compile("SELECT a.id FROM orders a JOIN otherdb.dbo.entitlements b ON a.id = b.orderid");
        Assert.DoesNotContain("otherdb", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        ProbeAsserts.TenantPredicates(compiled, 2);
    }

    // ---- SQL-6: the join guardrail (no false positive, no masked join) ---------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQL6_MaskedColumnInAJoin_IsRejected_ButAnUnrelatedJoinIsNotAFalsePositive(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        env.MaskEmail();
        Assert.ThrowsAny<SecurityException>(() => env.Compile("SELECT a.id FROM orders a JOIN orders b ON a.email = b.email"));

        // orders (with a masked column) is referenced, but the join is on clear columns of other tables
        var compiled = env.Compile("SELECT b.id FROM entitlements b JOIN entitlements c ON b.orderid = c.orderid, orders a WHERE a.id = 1");
        ProbeAsserts.TenantPredicates(compiled, 3);
    }

    // ---- SQL-1 (custom tenant column in DML): the tenant column of the catalog can never be written ------------------------

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SQL1_TenantColumnOfTheCatalog_CannotBeUpdated_AndAnInsertMustCarryTheBoundTenant(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        Assert.ThrowsAny<SecurityException>(() => env.Compile("UPDATE orders SET status = 'x', TenantId = 'hacked' WHERE id = 1"));
        Assert.ThrowsAny<SecurityException>(() => env.Compile("INSERT INTO orders (id, status, tenantid) VALUES (1, 'x', 'tenant-hacked')"));
        Assert.ThrowsAny<SecurityException>(() => env.Compile("INSERT INTO orders (id, status) VALUES (1, 'x')"));
    }
}

