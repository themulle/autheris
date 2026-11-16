using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the emitted SQL invariant checker (INV-4, INV-6, INV-13): each rejection is asserted with its rule name
/// and position, the accepted boundary next to it is asserted too, and every dialect marker style is read exactly. The checker is the
/// last line before the text reaches the provider, so a rule that silently stopped firing would let a literal, a comment or a query
/// hint through.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class EmittedSqlInvariantRuleTests
{
    private static readonly ImmutableArray<BoundParameter> NoParams = ImmutableArray<BoundParameter>.Empty;
    private static readonly IReadOnlyList<EmittedRange> NoRanges = Array.Empty<EmittedRange>();

    private static BoundParameter P(string marker, ParameterOrigin origin = ParameterOrigin.QueryLiteral) =>
        new(marker, marker, 1, "v", SqlParameterType.String, origin);

    private static void Check(
        string sql,
        TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        ImmutableArray<BoundParameter>? parameters = null,
        IReadOnlyList<EmittedRange>? ranges = null,
        bool structuralOnly = false,
        SqlStatementClass statementClass = SqlStatementClass.Select,
        int maxRecursionDepth = 0,
        DialectCapabilities? capabilities = null) =>
        EmittedSqlInvariantChecker.Check(sql, dialect, parameters ?? NoParams, ranges ?? NoRanges, structuralOnly, statementClass, maxRecursionDepth, capabilities);

    private static EmittedSqlInvariantViolationException Violation(
        string sql,
        string rule,
        int position,
        TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        ImmutableArray<BoundParameter>? parameters = null,
        IReadOnlyList<EmittedRange>? ranges = null,
        bool structuralOnly = false,
        SqlStatementClass statementClass = SqlStatementClass.Select,
        int maxRecursionDepth = 0,
        DialectCapabilities? capabilities = null)
    {
        var ex = Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            Check(sql, dialect, parameters, ranges, structuralOnly, statementClass, maxRecursionDepth, capabilities));
        Assert.Equal(rule, ex.Rule);
        Assert.Equal($"Emitted SQL violates invariant '{rule}' at position {position}.", ex.Message);
        return ex;
    }

    [Fact]
    public void EmittedLength_IsBoundedExactly()
    {
        string atLimit = "SELECT [a]" + new string(' ', EmittedSqlInvariantChecker.MaxEmittedSqlLength - 10);
        Assert.Equal(EmittedSqlInvariantChecker.MaxEmittedSqlLength, atLimit.Length);
        Check(atLimit);

        var ex = Assert.Throws<SqlLimitExceededException>(() => Check(atLimit + " ", TargetSqlDialect.PostgreSql));
        Assert.Equal(SqlLimitKind.EmittedSqlLength, ex.Kind);
        Assert.Equal(TargetSqlDialect.PostgreSql, ex.Dialect);
        Assert.Equal(EmittedSqlInvariantChecker.MaxEmittedSqlLength + 1, ex.Requested);
        Assert.Equal(EmittedSqlInvariantChecker.MaxEmittedSqlLength, ex.Maximum);
    }

    [Fact]
    public void NullArguments_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => EmittedSqlInvariantChecker.Check(null!, TargetSqlDialect.SqlServer, NoParams, NoRanges));
        Assert.Throws<ArgumentNullException>(() => EmittedSqlInvariantChecker.Check("SELECT [a]", TargetSqlDialect.SqlServer, NoParams, null!));
    }

    [Theory]
    [InlineData("SELECT 'a'", "forbidden-character", 7)]
    [InlineData("SELECT [a] FROM [t] WHERE [x] = N'a'", "forbidden-character", 33)]
    [InlineData("SELECT [a];", "forbidden-character", 10)]
    [InlineData("SELECT $a", "forbidden-character", 7)]
    [InlineData("SELECT `a`", "forbidden-character", 7)]
    [InlineData("SELECT \"a\"", "forbidden-character", 7)]
    [InlineData("SELECT [a]--x", "comment", 10)]
    [InlineData("SELECT [a] -- x", "comment", 11)]
    [InlineData("SELECT [a]/*x*/", "comment", 10)]
    [InlineData("SELECT [a] /* x */", "comment", 11)]
    [InlineData("SELECT [a]\u0001", "control-character", 10)]
    [InlineData("SELECT [a]\0", "control-character", 10)]
    [InlineData("SELECT [a]\u001f", "control-character", 10)]
    public void ForbiddenText_IsRejectedWithRuleAndPosition(string sql, string rule, int position) => Violation(sql, rule, position);

    [Theory]
    [InlineData("SELECT [a] - [b] FROM [t]")]
    [InlineData("SELECT [a]-[b] FROM [t]")]
    [InlineData("SELECT [a]/[b] FROM [t]")]
    [InlineData("SELECT [a] / [b] FROM [t]")]
    [InlineData("SELECT [a]*[b] FROM [t]")]
    [InlineData("SELECT [a]- -[b] FROM [t]")]
    [InlineData("SELECT [a]\r\n\tFROM [t]\n")]
    [InlineData("SELECT [a] FROM [t] WHERE [a] <> [b]")]
    [InlineData("SELECT [it's; -- /* $ ` \u0001] FROM [t]")]
    [InlineData("SELECT [a]\u007f FROM [t]")]
    [InlineData("SELECT [a] FROM [t]äö")]
    [InlineData("SELECT x_1, #tmp2, @@DATEFIRST FROM [t]")]
    public void OrdinaryText_IsAccepted(string sql) => Check(sql);

    [Theory]
    [InlineData("SELECT \"a\" FROM \"t\" WHERE \"b\" = \"c\"")]
    [InlineData("SELECT \"it's -- /* ; $ \"\"x\" FROM \"t\"")]
    public void DoubleQuotes_AreDelimitersOnlyForDialectsThatUseThem(string sql)
    {
        Check(sql, TargetSqlDialect.PostgreSql);
        Check(sql, TargetSqlDialect.Oracle);
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(sql, TargetSqlDialect.SqlServer));
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(sql, TargetSqlDialect.Databricks));
    }

    [Fact]
    public void BacktickIdentifiers_AreDelimitedForDatabricks()
    {
        Check("SELECT `it's -- /* ; $` FROM `t`", TargetSqlDialect.Databricks);
        Violation("SELECT `abc FROM t", "unterminated-delimited-identifier", 7, TargetSqlDialect.Databricks);
        Check("SELECT `a``b` FROM `t`", TargetSqlDialect.Databricks);
    }

    [Theory]
    [InlineData("SELECT [abc FROM t", 7)]
    [InlineData("SELECT [abc]]", 7)]
    [InlineData("SELECT [abc]] FROM t", 7)]
    [InlineData("SELECT [a] FROM [t", 16)]
    public void UnterminatedIdentifier_IsRejectedAtItsOpeningDelimiter(string sql, int position) =>
        Violation(sql, "unterminated-delimited-identifier", position);

    [Fact]
    public void StructuralOnly_SkipsTheLiteralAndNumberRules_ButNotTheHintOrControlStructure()
    {
        Check("SELECT 'a'; -- 1 /* $ `", structuralOnly: true);
        Check("SELECT 1, 22 FROM [t]\u0001", structuralOnly: true);
        Violation("SELECT 1 OPTION", "unregistered-query-hint", 9, structuralOnly: true);
        Violation("SELECT [a] FROM [t] MAXRECURSION", "unregistered-query-hint", 20, structuralOnly: true);
        Violation("SELECT [abc", "unterminated-delimited-identifier", 7, structuralOnly: true);
    }

    [Theory]
    [InlineData("SELECT [a] FROM [t] OPTION (MAXRECURSION 5)", 20)]
    [InlineData("SELECT [a] FROM [t] option", 20)]
    [InlineData("SELECT [a] FROM [t] OpTiOn", 20)]
    [InlineData("SELECT [a] FROM [t] maxrecursion", 20)]
    [InlineData("SELECT option FROM [t]", 7)]
    public void QueryHints_AreRejectedAnywhereOutsideTheRegisteredTail(string sql, int position) =>
        Violation(sql, "unregistered-query-hint", position);

    [Fact]
    public void IdentifiersContainingAHintWord_AreNotHints()
    {
        Check("SELECT [OPTION], options, my_option, option2 FROM [MAXRECURSION]");
    }

    [Theory]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = 1", 32)]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = 12", 32)]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = 1e5", 32)]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = 0", 32)]
    [InlineData("SELECT 9, [a] FROM [t]", 7)]
    public void UnregisteredNumbers_AreRejectedAtTheirStart(string sql, int position) => Violation(sql, "unregistered-numeric-token", position);

    [Fact]
    public void NumericRanges_MustCoverTheWholeToken_AndBeNumeric()
    {
        const string sql = "SELECT [a] FROM [t] WHERE [a] = 12 AND [b] = 3";
        int first = sql.IndexOf("12", StringComparison.Ordinal);
        int second = sql.IndexOf('3', first);
        var both = new[] { new EmittedRange(first, 2, EmittedRangeKind.Numeric), new EmittedRange(second, 1, EmittedRangeKind.Numeric) };
        Check(sql, ranges: both);
        // the order the ranges are registered in does not matter
        Check(sql, ranges: both.Reverse().ToArray());
        // a range that is wider than the token is fine
        Check(sql, ranges: new[] { new EmittedRange(first - 1, 4, EmittedRangeKind.Numeric), new EmittedRange(second, 1, EmittedRangeKind.Numeric) });
        // a range that starts before the token and covers it is fine
        Check(sql, ranges: new[] { new EmittedRange(first, 2, EmittedRangeKind.Numeric), new EmittedRange(second - 2, 3, EmittedRangeKind.Numeric) });

        // too short
        Violation(sql, "unregistered-numeric-token", first, ranges: new[] { new EmittedRange(first, 1, EmittedRangeKind.Numeric), new EmittedRange(second, 1, EmittedRangeKind.Numeric) });
        // the second token has no range
        Violation(sql, "unregistered-numeric-token", second, ranges: new[] { new EmittedRange(first, 2, EmittedRangeKind.Numeric) });
        // starts after the token
        Violation(sql, "unregistered-numeric-token", first, ranges: new[] { new EmittedRange(first + 1, 1, EmittedRangeKind.Numeric), new EmittedRange(second, 1, EmittedRangeKind.Numeric) });
    }

    [Fact]
    public void ConstantFragments_AreSkippedWholly_ButOnlyWhatTheyCover()
    {
        const string sql = "SELECT HASHBYTES('SHA2_256', [a]) FROM [t]";
        int open = sql.IndexOf('\'', StringComparison.Ordinal);
        int length = "'SHA2_256'".Length;
        Check(sql, ranges: new[] { new EmittedRange(open, length, EmittedRangeKind.ConstantFragment) });

        // not registered
        Violation(sql, "forbidden-character", open);
        // a fragment that stops short leaves the closing quote exposed
        Violation(sql, "forbidden-character", open + length - 1, ranges: new[] { new EmittedRange(open, length - 1, EmittedRangeKind.ConstantFragment) });
        // a fragment that starts late leaves the opening quote exposed
        Violation(sql, "forbidden-character", open, ranges: new[] { new EmittedRange(open + 1, length - 1, EmittedRangeKind.ConstantFragment) });
        // a numeric range is not a skip
        Violation(sql, "forbidden-character", open, ranges: new[] { new EmittedRange(open, length, EmittedRangeKind.Numeric) });
    }

    [Fact]
    public void Markers_MustMatchTheDeclaredParametersOneToOne()
    {
        Check("SELECT [a] FROM [t] WHERE [a] = @p1 AND [b] = @p1", parameters: ImmutableArray.Create(P("@p1")));
        Check("SELECT [a] FROM [t] WHERE [a] = @p1 AND [b] = @p10", parameters: ImmutableArray.Create(P("@p1"), P("@p10")));
        Check("SELECT [a] FROM [t] WHERE [a] = [@p9]");

        Violation("SELECT [a] FROM [t] WHERE [a] = @p1", "marker-without-parameter", 0);
        Violation("SELECT [a] FROM [t] WHERE [a] = @p1", "parameter-without-marker", 0, parameters: ImmutableArray.Create(P("@p2")));
        Violation("SELECT [a] FROM [t]", "parameter-without-marker", 0, parameters: ImmutableArray.Create(P("@p1")));
        Violation("SELECT [a] FROM [t] WHERE [a] = [@p1]", "parameter-without-marker", 0, parameters: ImmutableArray.Create(P("@p1")));
        Violation("SELECT [a] FROM [t] WHERE [a] = @p1", "duplicate-parameter-marker", 0, parameters: ImmutableArray.Create(P("@p1"), P("@p1")));
    }

    [Theory]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = @p")]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = @pa")]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = @p1a")]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = @q1")]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = @@p1")]
    [InlineData("SELECT [a] FROM [t] WHERE [a] = x@p1")]
    public void NonMarkerAtTokens_AreOrdinaryTokensNotMarkers(string sql) => Check(sql);

    [Fact]
    public void DollarOrdinalMarkers_ArePostgreSql()
    {
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = $1 AND \"b\" = $12 AND \"c\" = $1", TargetSqlDialect.PostgreSql,
            ImmutableArray.Create(P("$1"), P("$12")));
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = $", "forbidden-character", 32, TargetSqlDialect.PostgreSql);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = $1a", "forbidden-character", 32, TargetSqlDialect.PostgreSql);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = $a1", "forbidden-character", 32, TargetSqlDialect.PostgreSql);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = $1", "marker-without-parameter", 0, TargetSqlDialect.PostgreSql);
    }

    [Fact]
    public void ColonNamedOrdinalMarkers_AreOracleAndDatabricks()
    {
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = :p1 AND \"b\" = :p12", TargetSqlDialect.Oracle, ImmutableArray.Create(P(":p1"), P(":p12")));
        Check("SELECT `a` FROM `t` WHERE `a` = :p1", TargetSqlDialect.Databricks, ImmutableArray.Create(P(":p1")));
        // a doubled colon is a cast operator, not a marker
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = x::p1", TargetSqlDialect.Oracle);
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = x:p", TargetSqlDialect.Oracle);
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = x:p1a", TargetSqlDialect.Oracle);
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = :q1", TargetSqlDialect.Oracle);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = :p1", "marker-without-parameter", 0, TargetSqlDialect.Oracle);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = :p1", "parameter-without-marker", 0, TargetSqlDialect.Oracle,
            ImmutableArray.Create(P(":p1"), P(":p2")));
    }

    [Fact]
    public void QuestionAndColonOrdinalStyles_AreReadFromTheCapabilityTable()
    {
        var question = DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql) with { MarkerStyle = ParameterMarkerStyle.QuestionOrdinal };
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = ?1 AND \"b\" = ?2", TargetSqlDialect.PostgreSql, ImmutableArray.Create(P("?1"), P("?2")), capabilities: question);
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = ?", TargetSqlDialect.PostgreSql, capabilities: question);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = ?1a", "unregistered-numeric-token", 33, TargetSqlDialect.PostgreSql, capabilities: question);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = ?1", "marker-without-parameter", 0, TargetSqlDialect.PostgreSql, capabilities: question);

        var colon = DialectCapabilityTable.Default.Get(TargetSqlDialect.Oracle) with { MarkerStyle = ParameterMarkerStyle.ColonOrdinal };
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = :1 AND \"b\" = :2", TargetSqlDialect.Oracle, ImmutableArray.Create(P(":1"), P(":2")), capabilities: colon);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = x::1", "unregistered-numeric-token", 35, TargetSqlDialect.Oracle, capabilities: colon);
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = :", TargetSqlDialect.Oracle, capabilities: colon);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = :1a", "unregistered-numeric-token", 33, TargetSqlDialect.Oracle, capabilities: colon);
        Violation("SELECT \"a\" FROM \"t\" WHERE \"a\" = :1", "marker-without-parameter", 0, TargetSqlDialect.Oracle, capabilities: colon);
    }

    [Fact]
    public void MarkerAtTheEndOfTheText_IsRead()
    {
        Check("SELECT [a] FROM [t] WHERE [a] = @p1", parameters: ImmutableArray.Create(P("@p1")));
        Check("SELECT \"a\" FROM \"t\" WHERE \"a\" = $1", TargetSqlDialect.PostgreSql, ImmutableArray.Create(P("$1")));
        Check("SELECT [a] FROM [t] WHERE [a] = @p1", parameters: ImmutableArray.Create(P("@p1")), structuralOnly: true);
    }

    [Fact]
    public void PageOffset_IsOnePerSelect()
    {
        var offset = P("@p1", ParameterOrigin.PageOffset);
        Check("SELECT [a] FROM [t] OFFSET @p1 ROWS", parameters: ImmutableArray.Create(offset));

        var second = P("@p2", ParameterOrigin.PageOffset);
        Violation("SELECT [a] FROM [t] OFFSET @p1 ROWS OFFSET @p2 ROWS", "invalid-page-offset", 0, parameters: ImmutableArray.Create(offset, second));
        foreach (var statementClass in new[] { SqlStatementClass.Insert, SqlStatementClass.Update, SqlStatementClass.Delete, SqlStatementClass.Merge })
        {
            Violation("SELECT [a] FROM [t] OFFSET @p1 ROWS", "invalid-page-offset", 0, parameters: ImmutableArray.Create(offset), statementClass: statementClass);
        }

        // a non-offset parameter never counts as an offset
        Check("SELECT [a] FROM [t] WHERE [a] = @p1 AND [b] = @p2", parameters: ImmutableArray.Create(P("@p1"), P("@p2")), statementClass: SqlStatementClass.Update);
    }

    [Fact]
    public void Terminator_IsOnlyTheFinalSemicolonOfASqlServerMerge()
    {
        const string merge = "MERGE INTO [t] USING [s] ON 1 = 1;";
        string sql = "MERGE INTO [t] USING [s] ON [a] = [b];";
        var range = new EmittedRange(sql.Length - 1, 1, EmittedRangeKind.StatementTerminator);
        Check(sql, ranges: new[] { range }, statementClass: SqlStatementClass.Merge);
        Assert.NotEmpty(merge);

        Violation(sql, "unregistered-terminator", sql.Length - 1, ranges: new[] { range }, statementClass: SqlStatementClass.Select);
        Violation(sql, "unregistered-terminator", sql.Length - 1, ranges: new[] { range }, statementClass: SqlStatementClass.Update);
        Violation(sql, "unregistered-terminator", sql.Length - 1, TargetSqlDialect.PostgreSql, ranges: new[] { range }, statementClass: SqlStatementClass.Merge);
        Violation(sql, "unregistered-terminator", sql.Length - 1, TargetSqlDialect.Oracle, ranges: new[] { range }, statementClass: SqlStatementClass.Merge);
        Violation(sql, "unregistered-terminator", sql.Length - 1, TargetSqlDialect.DuckDb, ranges: new[] { range }, statementClass: SqlStatementClass.Merge);

        // two registrations
        Violation(sql, "unregistered-terminator", sql.Length - 1, ranges: new[] { new EmittedRange(sql.Length - 2, 1, EmittedRangeKind.Numeric), range, range }, statementClass: SqlStatementClass.Merge);
        // wrong length
        Violation(sql, "unregistered-terminator", sql.Length - 2, ranges: new[] { new EmittedRange(sql.Length - 2, 2, EmittedRangeKind.StatementTerminator) }, statementClass: SqlStatementClass.Merge);
        Violation(sql, "unregistered-terminator", sql.Length, ranges: new[] { new EmittedRange(sql.Length, 0, EmittedRangeKind.StatementTerminator) }, statementClass: SqlStatementClass.Merge);
        // not the last character
        string inner = "MERGE INTO [t]; USING [s] ON [a] = [b]";
        Violation(inner, "unregistered-terminator", 14, ranges: new[] { new EmittedRange(14, 1, EmittedRangeKind.StatementTerminator) }, statementClass: SqlStatementClass.Merge);
        // the registered character is not a semicolon
        string notSemicolon = "MERGE INTO [t] USING [s] ON [a] = [b] ";
        Violation(notSemicolon, "unregistered-terminator", notSemicolon.Length - 1, ranges: new[] { new EmittedRange(notSemicolon.Length - 1, 1, EmittedRangeKind.StatementTerminator) }, statementClass: SqlStatementClass.Merge);
        // an unregistered terminator is a forbidden character
        Violation(sql, "forbidden-character", sql.Length - 1, statementClass: SqlStatementClass.Merge);
    }

    private static string Tail(int depth) => $" OPTION (MAXRECURSION {depth})";

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(9999)]
    [InlineData(10000)]
    public void RecursionTail_IsAcceptedWithTheValidatedDepth(int depth)
    {
        string head = "WITH r AS (SELECT [a] FROM [t]) SELECT [a] FROM r";
        string sql = head + Tail(depth);
        Check(sql, ranges: new[] { new EmittedRange(head.Length, Tail(depth).Length, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: depth);
        Assert.Equal(10000, CompileLimits.MaxRecursionDepthLimit);
    }

    [Fact]
    public void RecursionTail_IsRejectedUnlessEveryConditionHolds()
    {
        string head = "WITH r AS (SELECT [a] FROM [t]) SELECT [a] FROM r";
        string tail = Tail(100);
        string sql = head + tail;
        var range = new EmittedRange(head.Length, tail.Length, EmittedRangeKind.RecursionOptionTail);
        const string Rule = "unregistered-recursion-option";

        Check(sql, ranges: new[] { range }, maxRecursionDepth: 100);
        // another dialect or statement class
        foreach (var dialect in new[] { TargetSqlDialect.PostgreSql, TargetSqlDialect.Oracle, TargetSqlDialect.DuckDb, TargetSqlDialect.Databricks })
        {
            Violation(sql, Rule, head.Length, dialect, ranges: new[] { range }, maxRecursionDepth: 100);
        }

        foreach (var statementClass in new[] { SqlStatementClass.Insert, SqlStatementClass.Update, SqlStatementClass.Delete, SqlStatementClass.Merge })
        {
            Violation(sql, Rule, head.Length, ranges: new[] { range }, statementClass: statementClass, maxRecursionDepth: 100);
        }

        // two tails
        Violation(sql, Rule, head.Length, ranges: new[] { range, range }, maxRecursionDepth: 100);
        // depth out of range
        Violation(sql, Rule, head.Length, ranges: new[] { range }, maxRecursionDepth: 0);
        Violation(sql, Rule, head.Length, ranges: new[] { range }, maxRecursionDepth: -100);
        string over = head + Tail(10001);
        Violation(over, Rule, head.Length, ranges: new[] { new EmittedRange(head.Length, Tail(10001).Length, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: 10001);
        // the text does not carry the validated depth
        Violation(sql, Rule, head.Length, ranges: new[] { range }, maxRecursionDepth: 99);
        Violation(sql, Rule, head.Length, ranges: new[] { range }, maxRecursionDepth: 1000);
        string differing = head + " OPTION (MAXRECURSION 101)";
        Violation(differing, Rule, head.Length, ranges: new[] { new EmittedRange(head.Length, tail.Length + 0, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: 100);
        // wrong text of the right length
        string wrongText = head + " OPTION (MAXRECURSION 10x)";
        Violation(wrongText, Rule, head.Length, ranges: new[] { new EmittedRange(head.Length, tail.Length - 1, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: 10);
        // the tail does not end the statement
        Violation(sql + " ", Rule, head.Length, ranges: new[] { range }, maxRecursionDepth: 100);
        Violation(sql + "x", Rule, head.Length, ranges: new[] { range }, maxRecursionDepth: 100);
        // the range is too short or too long for the text
        Violation(sql, Rule, head.Length, ranges: new[] { new EmittedRange(head.Length, tail.Length - 1, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: 100);
        Violation(sql, Rule, head.Length - 1, ranges: new[] { new EmittedRange(head.Length - 1, tail.Length + 1, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: 100);
        Violation(sql, Rule, head.Length + 1, ranges: new[] { new EmittedRange(head.Length + 1, tail.Length - 1, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: 100);
        // a negative start
        Violation(sql, Rule, -1, ranges: new[] { new EmittedRange(-1, tail.Length, EmittedRangeKind.RecursionOptionTail) }, maxRecursionDepth: 100);
        // without a registration the hint is rejected as a hint
        Violation(sql, "unregistered-query-hint", head.Length + 1, maxRecursionDepth: 100);
        Assert.Equal(" OPTION (MAXRECURSION ", EmittedSqlInvariantChecker.MaxRecursionTailPrefix);
    }

    [Fact]
    public void RangesCanBeRegisteredInAnyOrder_AndAreNotMutatedForTheCaller()
    {
        const string sql = "SELECT [a] FROM [t] WHERE [a] = 12 AND [b] = 3";
        int first = sql.IndexOf("12", StringComparison.Ordinal);
        int second = sql.IndexOf('3', first);
        var ranges = new[] { new EmittedRange(second, 1, EmittedRangeKind.Numeric), new EmittedRange(first, 2, EmittedRangeKind.Numeric) };

        Check(sql, ranges: ranges);

        Assert.Equal(second, ranges[0].Start);
        Assert.Equal(first, ranges[1].Start);
    }
}
