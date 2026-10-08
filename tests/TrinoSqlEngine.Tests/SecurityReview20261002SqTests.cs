namespace TrinoSqlEngine.Tests;

using System;
using System.Collections.Generic;
using System.Security;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

public class SecurityReview20261002SqTests
{
    private readonly FastSqlEngine _engine = new();

    private static RlsOptions CreateGovernedOptions(TargetSqlDialect dialect = TargetSqlDialect.Ansi) => new()
    {
        TargetDialect = dialect,
        RejectComments = false,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectDollarQuoting = dialect == TargetSqlDialect.SqlServer,
        RejectConsentFilteredInsert = true,
        RejectWholeRowReferencesInDml = true,
        // SEC P-06: dots in quoted identifiers are rejected by default; SQ-03 tests use "schema.table" whole-row references.
        RejectDotsInQuotedIdentifiers = false,
        EnforceReadOnlyQueries = false,
        EnforceWithCheckOption = true,
        ExpectedTenantValue = "42",
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42")
    };

    // =========================================================================
    // SQ-01: String Literal Lexer-Differential (PostgreSQL E'...' & Backslashes)
    // =========================================================================

    [Theory]
    [InlineData("SELECT 'hello\\nworld' FROM orders")]
    [InlineData("SELECT 'abc\\'def' FROM orders")]
    [InlineData("SELECT '\\' FROM orders")]
    public void SQ01_BackslashInStringLiteral_IsRejected(string sql)
    {
        var options = CreateGovernedOptions(TargetSqlDialect.PostgreSql);
        options.RejectBackslashInStrings = true;

        var ex = Assert.Throws<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory(), options));
        Assert.Contains("Backslash escapes in string literals", ex.Message);
    }

    [Theory]
    [InlineData("SELECT E'hello' FROM orders")]
    [InlineData("SELECT e'test' FROM orders")]
    [InlineData("SELECT E'foo bar' FROM orders")]
    public void SQ01_TypeConstructorWithE_IsRejected(string sql)
    {
        var options = CreateGovernedOptions(TargetSqlDialect.PostgreSql);
        options.RejectEscapedStringLiterals = true;

        var ex = Assert.Throws<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory(), options));
        Assert.Contains("Escaped string literal type constructors", ex.Message);
    }

    [Fact]
    public void SQ01_StandardEscapedSingleQuotes_AreAllowed()
    {
        var options = CreateGovernedOptions(TargetSqlDialect.PostgreSql);
        options.RejectBackslashInStrings = true;
        options.RejectEscapedStringLiterals = true;

        string sql = "SELECT 'O''Reilly' FROM orders";
        string secured = _engine.RewriteRls(sql.AsMemory(), options);
        Assert.Contains("'O''Reilly'", secured);
    }

    // =========================================================================
    // SQ-02: Block Comment Lexer-Differential & Comments In WebSql
    // =========================================================================

    [Theory]
    [InlineData("SELECT /* outer /* nested */ comment */ 1 FROM orders")]
    [InlineData("SELECT /* /* double */ */ 1 FROM orders")]
    public void SQ02_NestedBlockComments_AreStrictlyRejected(string sql)
    {
        var options = CreateGovernedOptions();
        var ex = Assert.Throws<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory(), options));
        Assert.Contains("Nested block comments", ex.Message);
    }

    [Theory]
    [InlineData("SELECT 1 FROM orders -- end of line comment")]
    [InlineData("SELECT /* standard comment */ 1 FROM orders")]
    public void SQ02_RejectComments_WhenEnabled_RejectsAllComments(string sql)
    {
        var options = CreateGovernedOptions();
        options.RejectComments = true;

        var ex = Assert.Throws<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory(), options));
        Assert.Contains("SQL comments are not permitted", ex.Message);
    }

    [Theory]
    [InlineData("SELECT $$dollar string$$ FROM orders")]
    [InlineData("SELECT $$line1\nline2$$ FROM orders")]
    public void SQ02_DollarQuoting_IsRejected_ForSqlServer(string sql)
    {
        var options = CreateGovernedOptions(TargetSqlDialect.SqlServer);
        var ex = Assert.Throws<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory(), options));
        Assert.Contains("Dollar-quoted strings", ex.Message);
    }

    // =========================================================================
    // SQ-03: Whole-Row Reference in UPDATE/DELETE Bypassing Masking
    // =========================================================================

    [Theory]
    [InlineData("UPDATE customers SET notes = CAST(customers AS VARCHAR) WHERE id = 1")]
    [InlineData("UPDATE sales.customers SET notes = CAST(\"sales.customers\" AS VARCHAR) WHERE id = 1")]
    [InlineData("DELETE FROM customers WHERE customers IS NOT NULL AND id = 1")]
    public void SQ03_WholeRowReference_InDml_IsRejected_WhenMaskedColumnsExist(string sql)
    {
        var options = CreateGovernedOptions();
        options.RejectWholeRowReferencesInDml = true;
        options.TablesWithMaskedColumns.Add("customers");
        options.TablesWithMaskedColumns.Add("sales.customers");
        options.ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
            hasMaskPredicate: (tbl, col) => col.Equals("ssn", StringComparison.OrdinalIgnoreCase),
            maskExpressionProvider: (tbl, col) => "'***'");

        var ex = Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory(), options));
        Assert.Contains("Whole-row reference", ex.Message);
    }

    [Fact]
    public void SQ03_DmlWithoutMaskedColumns_AllowsRowReference_OrUnmaskedColumns()
    {
        var options = CreateGovernedOptions();
        options.RejectWholeRowReferencesInDml = true;
        options.ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
            hasMaskPredicate: (tbl, col) => false,
            maskExpressionProvider: (tbl, col) => "'***'");

        string secured = _engine.RewriteRls("UPDATE customers SET notes = 'clean' WHERE id = 1".AsMemory(), options);
        Assert.Contains("UPDATE customers SET notes = 'clean'", secured);
    }

    // =========================================================================
    // SQ-04: Cross-Schema Policy Confusion via Short Table Name
    // =========================================================================

    [Fact]
    public void SQ04_FallbackToSimpleName_False_PreventsCrossSchemaPolicyConfusion()
    {
        var provider = new DefaultRlsPolicyProvider(
            defaultFilter: "1 = 0",
            predicate: tbl => tbl == "schema_a.orders",
            filterFunc: tbl => tbl == "schema_a.orders" ? "tenant_id = 'A'" : "1 = 0")
        {
            FallbackToSimpleName = false
        };

        // Fully qualified table matches
        Assert.True(provider.ShouldApplyPolicy("schema_a.orders"));
        Assert.Equal("tenant_id = 'A'", provider.GetPolicyFilter("schema_a.orders"));

        // Other schema with same short table name does NOT match and gets fail-closed filter
        Assert.False(provider.ShouldApplyPolicy("schema_b.orders"));
        Assert.Equal("1 = 0", provider.GetPolicyFilter("schema_b.orders"));
    }

    // =========================================================================
    // SQ-05: Dialect-Specific AST Rewriting (Aliases & LIMIT / FETCH NEXT)
    // =========================================================================

    [Fact]
    public void SQ05_WrapTableSource_EmitsSimpleUnqualifiedAlias()
    {
        var options = CreateGovernedOptions();
        options.AppendTableAlias = true;

        string secured = _engine.RewriteRls("SELECT * FROM sales.orders".AsMemory(), options);

        // Alias must be AS orders, not AS sales.orders
        Assert.Contains("AS orders", secured);
        Assert.DoesNotContain("AS sales.orders", secured);
    }

    [Fact]
    public void SQ05_SqlServer_RewritesRootLimit_ToOffsetFetchNext()
    {
        var options = CreateGovernedOptions(TargetSqlDialect.SqlServer);
        options.EnforcedMaxRows = 100;

        string secured = _engine.RewriteRls("SELECT id, name FROM orders ORDER BY id".AsMemory(), options);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY", secured);
        Assert.DoesNotContain("LIMIT", secured);

        // When query already has a LIMIT clause, it should be rewritten to FETCH NEXT
        string securedWithLimit = _engine.RewriteRls("SELECT id FROM orders LIMIT 20".AsMemory(), options);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY", securedWithLimit);
        Assert.DoesNotContain("LIMIT", securedWithLimit);
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
        var options = CreateGovernedOptions();
        options.EnforceFunctionPolicy = true;

        var ex = Assert.Throws<SecurityException>(() => _engine.RewriteRls(sql.AsMemory(), options));
        Assert.Contains("is not permitted", ex.Message);
    }

    // =========================================================================
    // SQ-07: Reject INSERT on Tables with Consent Row Filters
    // =========================================================================

    [Fact]
    public void SQ07_InsertIntoTableWithConsentRowFilter_IsRejected()
    {
        var options = CreateGovernedOptions();
        options.RejectConsentFilteredInsert = true;
        options.TablesWithConsentRowFilter.Add("patients");
        options.TablesWithConsentRowFilter.Add("health.records");

        // Table with consent filter is rejected
        var ex1 = Assert.Throws<SecurityException>(() =>
            _engine.RewriteRls("INSERT INTO patients (id, tenant_id) VALUES (1, 42)".AsMemory(), options));
        Assert.Contains("custom row-level consent filter", ex1.Message);

        var ex2 = Assert.Throws<SecurityException>(() =>
            _engine.RewriteRls("INSERT INTO health.records (id, tenant_id) VALUES (1, 42)".AsMemory(), options));
        Assert.Contains("custom row-level consent filter", ex2.Message);

        // Table without consent filter can insert normally
        string secured = _engine.RewriteRls("INSERT INTO orders (id, tenant_id) VALUES (1, 42)".AsMemory(), options);
        Assert.Contains("INSERT INTO orders", secured);
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
        var options = CreateGovernedOptions();
        options.RejectDotsInQuotedIdentifiers = true;

        var ex = Assert.Throws<ParseCanceledException>(() =>
            _engine.RewriteRls("SELECT * FROM \"my.table\"".AsMemory(), options));
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
        var options = CreateGovernedOptions();
        options.RejectTimeTravelQueries = true;

        var ex = Assert.Throws<ParseCanceledException>(() =>
            _engine.RewriteRls(sql.AsMemory(), options));
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
