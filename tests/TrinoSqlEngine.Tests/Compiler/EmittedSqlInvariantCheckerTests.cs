using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

public class EmittedSqlInvariantCheckerTests
{
    private static readonly ImmutableArray<BoundParameter> NoParams = ImmutableArray<BoundParameter>.Empty;
    private static readonly IReadOnlyList<EmittedRange> NoRanges = Array.Empty<EmittedRange>();

    private static void Check(string sql, ImmutableArray<BoundParameter>? ps = null, IReadOnlyList<EmittedRange>? ranges = null) =>
        EmittedSqlInvariantChecker.Check(sql, TargetSqlDialect.SqlServer, ps ?? NoParams, ranges ?? NoRanges);

    private static BoundParameter P(int ordinal) =>
        new($"@p{ordinal}", $"@p{ordinal}", ordinal, "v", SqlParameterType.String, ParameterOrigin.QueryLiteral);

    [Theory]
    [InlineData("SELECT 'a' FROM [t]")]
    [InlineData("SELECT N'a' FROM [t]")]
    [InlineData("SELECT [a] FROM [t] -- x")]
    [InlineData("SELECT [a] /* x */ FROM [t]")]
    [InlineData("SELECT [a] FROM [t];")]
    [InlineData("SELECT $$a$$")]
    [InlineData("SELECT E'a'")]
    [InlineData("SELECT \"a\" FROM [t]")]
    [InlineData("SELECT [a] FROM [t]\0")]
    public void Rejects_QuoteCommentSemicolonDollarQuote_OutsideDelimitedIdentifiers(string sql)
    {
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(sql));
    }

    [Fact]
    public void Accepts_HostileCharacters_InsideDelimitedIdentifiers_WithEscaping()
    {
        Check("SELECT [it's -- /* ; $$ \" 1] FROM [t]]x; 2]");
    }

    [Fact]
    public void Rejects_UnterminatedDelimitedIdentifier()
    {
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT [abc FROM t"));
    }

    [Fact]
    public void NumericTokens_AreAllowedOnlyAtRegisteredRanges()
    {
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT [a] FROM [t] WHERE [a] = 1"));
        const string sql = "SELECT [a] FROM [t] WHERE [a] = 1";
        Check(sql, ranges: new[] { new EmittedRange(sql.Length - 1, 1, EmittedRangeKind.Numeric) });
    }

    [Fact]
    public void MarkersAndIdentifiersWithDigits_AreNotNumericTokens()
    {
        Check("SELECT [c1], x2 FROM [t3] WHERE [a] = @p12 AND @@DATEFIRST = @p12", new[] { P(12) }.ToImmutableArray());
    }

    [Fact]
    public void ConstantFragmentRange_MayContainQuotesAndDigits()
    {
        const string sql = "SELECT HASHBYTES('SHA2_256', [a]) FROM [t]";
        int start = sql.IndexOf("'SHA2_256'", StringComparison.Ordinal);
        Check(sql, ranges: new[] { new EmittedRange(start, "'SHA2_256'".Length, EmittedRangeKind.ConstantFragment) });
    }

    [Fact]
    public void MarkerInTextWithoutParameter_IsRejected()
    {
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT [a] FROM [t] WHERE [a] = @p0"));
    }

    [Fact]
    public void ParameterNotInText_IsRejected()
    {
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check("SELECT [a] FROM [t]", new[] { P(0) }.ToImmutableArray()));
    }

    [Fact]
    public void EmittedLengthOverLimit_ThrowsTypedLimit()
    {
        var sql = "SELECT [a] FROM [t] WHERE [a] IN (" + string.Join(",", Enumerable.Repeat("@@x", 400_000)) + ")";
        Assert.Throws<TrinoSqlEngine.Ast.Capabilities.SqlLimitExceededException>(() => Check(sql));
    }
}
