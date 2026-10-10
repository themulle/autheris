using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

public class DialectCapabilitiesTests
{
    private static readonly IDialectCapabilityProvider Provider = DialectCapabilityTable.Default;

    [Fact]
    public void SqlServer_HasExactValuesFromPlanSection5()
    {
        var caps = Provider.Get(TargetSqlDialect.SqlServer);
        Assert.Equal(DialectSupportTier.Production, caps.Tier);
        Assert.Equal(2100, caps.MaxBindParameters);
        Assert.Null(caps.MaxInListItems);
        Assert.Equal(128, caps.MaxIdentifierLength);
        Assert.Equal(IdentifierLengthUnit.Characters, caps.IdentifierLengthUnit);
        Assert.Equal('[', caps.IdentifierOpenQuote);
        Assert.Equal(']', caps.IdentifierCloseQuote);
        Assert.Equal(ParameterMarkerStyle.AtNamedOrdinal, caps.MarkerStyle);
        Assert.True(caps.SupportsMarkerReuse);
        Assert.False(caps.SupportsNullsFirstLast);
        Assert.False(caps.SupportsLateral);
        Assert.True(caps.LimitGuaranteed);
        Assert.True(caps.InDbHmac);
        Assert.Empty(caps.AllowedTableFunctions);
    }

    [Theory]
    [InlineData(TargetSqlDialect.Snowflake)]
    [InlineData(TargetSqlDialect.Ansi)]
    [InlineData((TargetSqlDialect)999)]
    public void UnknownOrUnsupportedDialect_Throws(TargetSqlDialect dialect)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Provider.Get(dialect));
    }

    [Fact]
    public void BindExpressionTemplates_AreConstantAndContainExactlyOnePlaceholder()
    {
        var caps = Provider.Get(TargetSqlDialect.SqlServer);
        foreach (var (_, template) in caps.BindExpressionTemplates)
        {
            Assert.Equal(1, CountOccurrences(template, "{0}"));
            Assert.DoesNotContain('\'', template.Replace("{0}", string.Empty));
        }
        Assert.Equal(Enum.GetValues<SqlParameterType>().Length, caps.BindExpressionTemplates.Count);
    }

    [Fact]
    public void TenantPredicate_IsBinaryExact_ForSqlServer()
    {
        var caps = Provider.Get(TargetSqlDialect.SqlServer);
        Assert.Equal(TenantComparisonStyle.Utf16BinaryCast, caps.TenantComparison);
    }

    [Fact]
    public void ProductionDialects_AreLimitGuaranteed()
    {
        var caps = Provider.Get(TargetSqlDialect.SqlServer);
        Assert.True(caps.Tier != DialectSupportTier.Production || caps.LimitGuaranteed);
    }

    [Fact]
    public void FunctionMap_NoMatch_Rejects()
    {
        var caps = Provider.Get(TargetSqlDialect.SqlServer);
        Assert.False(caps.Functions.TryGetRule("definitely_not_a_function", out _));
    }

    [Fact]
    public void SqlLimitExceeded_CarriesKindDialectRequestedMaximum_AndIsSecurityException()
    {
        var ex = new SqlLimitExceededException(SqlLimitKind.InListItems, TargetSqlDialect.Oracle, 1001, 1000);
        Assert.IsAssignableFrom<SecurityException>(ex);
        Assert.Equal(SqlLimitKind.InListItems, ex.Kind);
        Assert.Equal(TargetSqlDialect.Oracle, ex.Dialect);
        Assert.Equal(1001, ex.Requested);
        Assert.Equal(1000, ex.Maximum);
        Assert.DoesNotContain("'", ex.Message);
    }

    [Fact]
    public void LargeInList_IsNeverCompactedToRange_LimitKindsHaveNoCompaction()
    {
        // AP-11 / B-3: over-limit lists are rejected with a typed error, there is no compaction or array-binding kind.
        Assert.DoesNotContain(Enum.GetNames<SqlLimitKind>(), n => n.Contains("Compact", StringComparison.OrdinalIgnoreCase));
    }

    private static int CountOccurrences(string s, string needle)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
