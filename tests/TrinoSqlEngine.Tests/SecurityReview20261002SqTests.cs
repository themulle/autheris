namespace TrinoSqlEngine.Tests;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Security;
using Xunit;

[Trait("Category", "SqlGate")]
public class SecurityReview20261002SqTests
{
    private readonly FastSqlEngine _engine = new();

    // The governed behaviour is asserted on the AST path (FastSqlEngine.Compile through ProbeEnv). The legacy rewriter (RewriteRls)
    // and its string policy providers are gone since the X1 cutover; assertions are semantic (exception type and message, bound
    // values, the structure of the emitted statement), never the legacy text format.
    private static ProbeEnv Env(TargetSqlDialect dialect = TargetSqlDialect.PostgreSql) => new(dialect);

    // =========================================================================
    // SQ-01: String Literal Lexer-Differential (PostgreSQL E'...' & Backslashes)
    // =========================================================================

    [Theory]
    [InlineData("SELECT 'hello\\nworld' FROM orders")]
    [InlineData("SELECT 'abc\\'def' FROM orders")]
    [InlineData("SELECT '\\' FROM orders")]
    public void SQ01_BackslashInStringLiteral_IsRejected(string sql)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env().Compile(sql));
        Assert.Contains("Backslash escapes in string literals", ex.Message);
    }

    [Theory]
    [InlineData("SELECT E'hello' FROM orders")]
    [InlineData("SELECT e'test' FROM orders")]
    [InlineData("SELECT E'foo bar' FROM orders")]
    public void SQ01_TypeConstructorWithE_IsRejected(string sql)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env().Compile(sql));
        Assert.Contains("Escaped string literal type constructors", ex.Message);
    }

    [Fact]
    public void SQ01_StandardEscapedSingleQuotes_AreAllowed()
    {
        var compiled = Env().Compile("SELECT 'O''Reilly' FROM orders");

        // the value is bound, never part of the text
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "O'Reilly"));
        Assert.DoesNotContain("Reilly", compiled.Sql);
    }

    // =========================================================================
    // SQ-02: Block Comment Lexer-Differential & Comments In WebSql
    // =========================================================================

    [Theory]
    [InlineData("SELECT /* outer /* nested */ comment */ 1 FROM orders")]
    [InlineData("SELECT /* /* double */ */ 1 FROM orders")]
    public void SQ02_NestedBlockComments_AreStrictlyRejected(string sql)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env().Compile(sql));
        Assert.Contains("Nested block comments", ex.Message);
    }

    [Theory]
    [InlineData("SELECT 1 FROM orders -- end of line comment")]
    [InlineData("SELECT /* standard comment */ 1 FROM orders")]
    public void SQ02_RejectComments_RejectsAllComments(string sql)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env().Compile(sql));
        Assert.Contains("SQL comments are not permitted", ex.Message);
    }

    [Theory]
    [InlineData("SELECT $$dollar string$$ FROM orders")]
    [InlineData("SELECT $$line1\nline2$$ FROM orders")]
    public void SQ02_DollarQuoting_IsRejected_ForSqlServer(string sql)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env(TargetSqlDialect.SqlServer).Compile(sql));
        Assert.Contains("Dollar-quoted strings", ex.Message);
    }

    // =========================================================================
    // SQ-03: Whole-Row Reference in UPDATE/DELETE Bypassing Masking
    // =========================================================================

    [Theory]
    [InlineData("UPDATE orders SET status = CAST(orders AS VARCHAR) WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE orders IS NOT NULL AND id = 1")]
    public void SQ03_WholeRowReference_InDml_IsRejected_WhenMaskedColumnsExist(string sql)
    {
        var env = Env(TargetSqlDialect.SqlServer);
        env.MaskEmail();

        var ex = Assert.ThrowsAny<SecurityException>(() => env.Compile(sql));
        Assert.Contains("whole-row", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SQ03_DmlWithoutMaskedColumns_AllowsUnmaskedColumns()
    {
        var compiled = Env(TargetSqlDialect.SqlServer).Compile("UPDATE orders SET status = 'clean' WHERE id = 1");

        Assert.Equal(SqlStatementClass.Update, compiled.StatementClass);
    }

    // =========================================================================
    // SQ-04: Cross-Schema Policy Confusion via Short Table Name
    //   The string policy provider (FallbackToSimpleName) is gone; the typed catalog resolves a table by its full identity, and
    //   AstSecurityRegressionTests.SQ04_ATableInAnotherSchema_NeverResolvesToTheCatalogTableOfTheSameShortName guards the outcome.
    // =========================================================================

    // =========================================================================
    // SQ-05: Dialect-Specific AST Rewriting (Aliases & LIMIT / FETCH NEXT)
    //   The simple table alias is asserted by AstSecurityRegressionTests.SQ05_TableAlias_IsTheSimpleName_NeverSchemaQualified.
    // =========================================================================

    [Fact]
    public void SQ05_SqlServer_RewritesAnExistingLimit_ToOffsetFetchNext()
    {
        var env = Env(TargetSqlDialect.SqlServer);

        var capped = env.Engine.Compile("SELECT id FROM orders ORDER BY id".AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY", capped.Sql);
        Assert.DoesNotContain("LIMIT", capped.Sql);

        // a smaller user limit survives the clamp and is emitted as FETCH NEXT, never as LIMIT
        var withLimit = env.Engine.Compile("SELECT id FROM orders LIMIT 20".AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY", withLimit.Sql);
        Assert.DoesNotContain("LIMIT", withLimit.Sql);
    }

    // =========================================================================
    // SQ-06: Sensitive Function Denylist (PostgreSQL & SQL Server)
    // =========================================================================

    [Theory]
    // PostgreSQL
    [InlineData("SELECT pg_notify('chan', 'payload')")]
    [InlineData("SELECT inet_server_addr()")]
    [InlineData("SELECT version()")]
    [InlineData("SELECT txid_current()")]
    [InlineData("SELECT pg_stat_reset()")]
    [InlineData("SELECT pg_get_expr('1', 1)")]
    // SQL Server
    [InlineData("SELECT fn_dblog(NULL, NULL)")]
    [InlineData("SELECT has_dbaccess('master')")]
    [InlineData("SELECT suser_sname()")]
    [InlineData("SELECT is_srvrolemember('sysadmin')")]
    public void SQ06_SensitiveBuiltinFunctions_AreDenied(string sql)
    {
        foreach (var dialect in new[] { TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer })
        {
            var ex = Assert.ThrowsAny<SecurityException>(() => Env(dialect).Compile(sql));
            Assert.Contains("is not permitted", ex.Message);
        }
    }

    // =========================================================================
    // SQ-07: Reject INSERT on Tables with Consent Row Filters
    // =========================================================================

    [Fact]
    public void SQ07_InsertIntoTableWithConsentRowFilter_IsRejected()
    {
        var env = Env(TargetSqlDialect.SqlServer);
        env.RegionPolicy();   // a consent-style row filter on orders
        var baseRequest = env.Request();
        var request = baseRequest with
        {
            Policy = baseRequest.Policy with { TablesWithConsentRowFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "dbo.Orders" } }
        };

        var ex = Assert.ThrowsAny<SecurityException>(() =>
            env.Engine.Compile("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')".AsMemory(), request, CancellationToken.None));
        Assert.Contains("consent", ex.Message, StringComparison.OrdinalIgnoreCase);

        // a table without a consent filter inserts normally
        var secured = Env(TargetSqlDialect.SqlServer).Compile("INSERT INTO entitlements (id, tenantid) VALUES (1, 'acme')");
        Assert.Equal(SqlStatementClass.Insert, secured.StatementClass);
    }

    // =========================================================================
    // SQ-08: Native Thread Stack Protection
    // =========================================================================

    [Fact]
    public void SQ08_StackCheckAndNestingLimit_WorkCorrectly()
    {
        var engine = new FastSqlEngine { MaxNestingDepth = 10 };
        string deeplyNested = "SELECT " + new string('(', 15) + "1" + new string(')', 15);

        var ex = Assert.Throws<ParseCanceledException>(() => engine.Parse(deeplyNested.AsMemory()));
        Assert.Contains("nesting depth", ex.Message);
    }

    // =========================================================================
    // SQ-09: Multi-Part Table Names (Catalog / Linked Servers)
    // =========================================================================

    [Theory]
    [InlineData("SELECT * FROM server.database.schema.table")]
    [InlineData("SELECT * FROM srv.db.dbo.orders")]
    public void SQ09_LinkedServer_4PartNames_AreRejected(string sql)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => _engine.Analyze(sql.AsMemory()));
        Assert.True(ex.Message.Contains("Four-part table names") ||
                    ex.Message.Contains("Linked server") ||
                    ex.Message.Contains("mismatched input"));
    }

    [Fact]
    public void SQ09_3PartNames_ExtractsCatalogCorrectly()
    {
        var meta = _engine.Analyze("SELECT * FROM mycatalog.myschema.mytable".AsMemory());
        var target = Assert.Single(meta.ReferencedTables);
        Assert.Equal("mycatalog", target.Catalog);
        Assert.Equal("myschema", target.Schema);
        Assert.Equal("mytable", target.TableName);
        Assert.Equal("mycatalog.myschema.mytable", target.FullName);
    }

    // =========================================================================
    // SQ-10: Non-ASCII Case Insensitivity Preservation in Stream
    // =========================================================================

    [Fact]
    public void SQ10_ZeroCopyCaseInsensitiveStream_PreservesNonAsciiCharacters()
    {
        // German sharp S (ß), umlaut (ä), Turkish dotless i (ı) must NOT be folded
        string text = "select * from test_ß_ä_ı";
        var stream = new ZeroCopyCaseInsensitiveStream(text.AsMemory());

        // 's' -> 'S' (ASCII folded)
        Assert.Equal('S', (char)stream.LA(1));
        stream.Consume(); // 'e' -> 'E'
        Assert.Equal('E', (char)stream.LA(1));
        stream.Consume(); // 'l' -> 'L'
        Assert.Equal('L', (char)stream.LA(1));

        // Skip to 'ß'
        while (stream.Index < text.IndexOf('ß'))
        {
            stream.Consume();
        }

        // 'ß' must remain 'ß', not folded or mutated
        Assert.Equal('ß', (char)stream.LA(1));

        // 'ä' must remain 'ä'
        while (stream.Index < text.IndexOf('ä'))
        {
            stream.Consume();
        }
        Assert.Equal('ä', (char)stream.LA(1));

        // 'ı' must remain 'ı'
        while (stream.Index < text.IndexOf('ı'))
        {
            stream.Consume();
        }
        Assert.Equal('ı', (char)stream.LA(1));
    }

    // =========================================================================
    // SQ-11: Dots in Quoted Identifiers
    // =========================================================================

    [Fact]
    public void SQ11_DotsInQuotedIdentifiers_WhenStrict_IsRejected()
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env().Compile("SELECT * FROM \"my.table\""));
        Assert.Contains("Dots inside quoted identifiers", ex.Message);
    }

    // =========================================================================
    // SQ-13: Time Travel Queries
    // =========================================================================

    [Theory]
    [InlineData("SELECT * FROM orders FOR TIMESTAMP AS OF '2026-01-01'")]
    [InlineData("SELECT * FROM orders FOR VERSION AS OF 100")]
    public void SQ13_TimeTravelQueries_WhenStrict_AreRejected(string sql)
    {
        var ex = Assert.Throws<ParseCanceledException>(() => Env().Compile(sql));
        Assert.Contains("Time-travel queries", ex.Message);
    }

    // =========================================================================
    // SQ-14: Parser Pool Cleans TokenStream Reference
    // =========================================================================

    [Fact]
    public void SQ14_ParserPooledObjectPolicy_ClearsTokenStreamOnReturn()
    {
        var policy = new ParserPooledObjectPolicy();
        var parser = policy.Create();

        var lexer = new SqlBaseLexer(new ZeroCopyCaseInsensitiveStream("SELECT 1".AsMemory()));
        parser.TokenStream = new CommonTokenStream(lexer);
        Assert.NotNull(parser.TokenStream);

        policy.Return(parser);
        Assert.Null(parser.TokenStream);
    }
}
