using System.Globalization;
using System.Text;
using System.Text.Json;
using Autheris.Application.Sql;
using Autheris.Application.Sql.ListParameters;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.Sql.ListParameters;

/// <summary>REQ-SQL-128 WP-3: normalization of list values (PLAN-SQL-128 A-4; Phase 3 SEC-128-06, -07, -08, -09).</summary>
public class ListParameterNormalizerTests
{
    private static readonly ListParameterLimits Limits = new(ConfiguredMaxValues: 1000, MaxListParameters: 8);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ClientListValue Normalize(string json, ListParameterLimits? limits = null, SqlParameterType? declared = null) =>
        ListParameterNormalizer.Normalize(Json(json), limits ?? Limits, declared);

    private static string Code(Action action) => Should.Throw<ClientListException>(action).Code;

    private static object[] Elements(ClientListValue value) => Enumerable.Range(0, value.Count).Select(i => value[i]).ToArray();

    [Fact]
    public void Normalizer_ConvertsJsonArrays_DeduplicatesKeepsOrder()
    {
        var strings = Normalize("[\"b\",\"a\",\"b\",\"c\",\"a\"]");
        strings.ElementType.ShouldBe(SqlParameterType.String);
        Elements(strings).ShouldBe(new object[] { "b", "a", "c" });

        var longs = Normalize("[3,1,3,2]");
        longs.ElementType.ShouldBe(SqlParameterType.Int64);
        Elements(longs).ShouldBe(new object[] { 3L, 1L, 2L });

        var bools = Normalize("[true,false,true]");
        bools.ElementType.ShouldBe(SqlParameterType.Boolean);
        bools.Count.ShouldBe(2);
    }

    [Fact]
    public void Normalizer_MixedIntegralAndFractionalNumbers_BecomeOneDecimalType()
    {
        var value = Normalize("[1, 2.5, 3]");
        value.ElementType.ShouldBe(SqlParameterType.Decimal);
        Elements(value).ShouldBe(new object[] { 1m, 2.5m, 3m });
    }

    [Fact]
    public void Normalizer_EmptyArray_IsAnEmptyList()
    {
        Normalize("[]").Count.ShouldBe(0);
    }

    [Fact]
    public void Normalizer_IsCaseSensitiveAndOrdinalForStrings()
    {
        Normalize("[\"A\",\"a\"]").Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("[1, null]", "SQL_LIST_PARAM_NULL")]
    [InlineData("[\"a\", null]", "SQL_LIST_PARAM_NULL")]
    [InlineData("[[1], [2]]", "SQL_LIST_PARAM_TYPE")]
    [InlineData("[{\"a\":1}]", "SQL_LIST_PARAM_TYPE")]
    [InlineData("[1, \"a\"]", "SQL_LIST_PARAM_TYPE")]
    [InlineData("[true, 1]", "SQL_LIST_PARAM_TYPE")]
    [InlineData("[\"a\", false]", "SQL_LIST_PARAM_TYPE")]
    [Trait("Category", "Security")]
    public void Normalizer_RejectsNullElement_Nested_MixedClasses(string json, string expectedCode)
    {
        Code(() => Normalize(json)).ShouldBe(expectedCode);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Normalizer_DistinctCountAboveConfiguredMaximum_IsRefused_AfterDeduplication()
    {
        var limits = new ListParameterLimits(3, 8);
        Normalize("[1,2,3,3,3,3]", limits).Count.ShouldBe(3);
        Code(() => Normalize("[1,2,3,4]", limits)).ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    // SEC-128-09 / M-5
    [Fact]
    [Trait("Category", "Security")]
    public void Normalizer_RawCeiling_RefusesBeforeReadingElements()
    {
        limitsFor(1000).RawCeiling.ShouldBe(10_000);
        limitsFor(10_000).RawCeiling.ShouldBe(100_000);

        // 100,001 elements are above the ceiling of the largest configuration. The elements are not even valid for any type: the
        // refusal must come from the length, so it is TOO_LARGE and not TYPE.
        var big = new StringBuilder("[");
        for (int i = 0; i < 100_001; i++)
        {
            big.Append(i == 0 ? "{}" : ",{}");
        }

        big.Append(']');
        Code(() => Normalize(big.ToString(), limitsFor(10_000))).ShouldBe("SQL_LIST_PARAM_TOO_LARGE");

        static ListParameterLimits limitsFor(int configured) => new(configured, 8);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Normalizer_TwoMegabyteArrayOfDuplicates_IsRefusedByTheRawCeiling()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 700_000; i++)
        {
            sb.Append(i == 0 ? "\"a\"" : ",\"a\"");
        }

        sb.Append(']');
        sb.Length.ShouldBeGreaterThan(2_000_000);
        Code(() => Normalize(sb.ToString())).ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    [Fact]
    public void Normalizer_RawLengthAtTheCeiling_IsAcceptedWhenDistinctCountFits()
    {
        var limits = new ListParameterLimits(10, 8);   // ceiling 100
        string json = "[" + string.Join(",", Enumerable.Repeat("1", 100)) + "]";
        Normalize(json, limits).Count.ShouldBe(1);
        string tooLong = "[" + string.Join(",", Enumerable.Repeat("1", 101)) + "]";
        Code(() => Normalize(tooLong, limits)).ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    // SEC-128-08 / M-15
    [Theory]
    [InlineData("[1e999]")]
    [InlineData("[123456789012345678901234567890123]")]   // integral token beyond decimal: never rounded through double
    [InlineData("[-123456789012345678901234567890123]")]
    [Trait("Category", "Security")]
    public void Numbers_IntegralBeyondDecimal_AndOverflow_AreRejected_NoException(string json)
    {
        Code(() => Normalize(json)).ShouldBe("SQL_LIST_PARAM_TYPE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Numbers_IntegralBeyondInt64_ButInsideDecimal_StayExact()
    {
        var value = Normalize("[12345678901234567890]");
        value.ElementType.ShouldBe(SqlParameterType.Decimal);
        Elements(value).ShouldBe(new object[] { 12345678901234567890m });
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Numbers_FiniteDouble_WithExponentBeyondDecimal_IsAllowedOnlyAlone()
    {
        var value = Normalize("[1e300]");
        value.ElementType.ShouldBe(SqlParameterType.Double);
        Code(() => Normalize("[1e300, 1]")).ShouldBe("SQL_LIST_PARAM_TYPE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Date_WithoutOffset_UsesUtc()
    {
        var value = ListParameterNormalizer.Normalize(new[] { "2026-01-02T03:04:05" }, Limits, SqlParameterType.TimestampTz);
        var date = (DateTimeOffset)value[0];
        date.Offset.ShouldBe(TimeSpan.Zero);
        date.ShouldBe(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    }

    [Fact]
    public void Date_WithOffset_KeepsTheInstant()
    {
        var value = ListParameterNormalizer.Normalize(new[] { "2026-01-02T03:04:05+02:00" }, Limits, SqlParameterType.TimestampTz);
        ((DateTimeOffset)value[0]).ToUniversalTime().ShouldBe(new DateTimeOffset(2026, 1, 2, 1, 4, 5, TimeSpan.Zero));
    }

    [Fact]
    public void Declared_Int32_ConvertsStringsAndNumbers_AndRefusesOutOfRange()
    {
        var value = ListParameterNormalizer.Normalize(new object?[] { "5", 6L, "5" }, Limits, SqlParameterType.Int32);
        value.ElementType.ShouldBe(SqlParameterType.Int32);
        Elements(value).ShouldBe(new object[] { 5, 6 });
        Code(() => ListParameterNormalizer.Normalize(new object?[] { "3000000000" }, Limits, SqlParameterType.Int32)).ShouldBe("SQL_LIST_PARAM_TYPE");
        Code(() => ListParameterNormalizer.Normalize(new object?[] { "abc" }, Limits, SqlParameterType.Int32)).ShouldBe("SQL_LIST_PARAM_TYPE");
    }

    [Fact]
    public void Declared_JsonArray_IsCoercedElementWise()
    {
        var value = Normalize("[1, 2, 3]", declared: SqlParameterType.Int32);
        value.ElementType.ShouldBe(SqlParameterType.Int32);
        Elements(value).ShouldBe(new object[] { 1, 2, 3 });
        Elements(Normalize("[1, 2]", declared: SqlParameterType.Decimal)).ShouldBe(new object[] { 1m, 2m });
        Elements(Normalize("[\"1.5\"]", declared: SqlParameterType.Double)).ShouldBe(new object[] { 1.5 });
        Elements(Normalize("[\"true\", false]", declared: SqlParameterType.Boolean)).ShouldBe(new object[] { true, false });
    }

    [Fact]
    public void Declared_Double_RefusesNonFinite()
    {
        Code(() => ListParameterNormalizer.Normalize(new object?[] { "NaN" }, Limits, SqlParameterType.Double)).ShouldBe("SQL_LIST_PARAM_TYPE");
        Code(() => ListParameterNormalizer.Normalize(new object?[] { "Infinity" }, Limits, SqlParameterType.Double)).ShouldBe("SQL_LIST_PARAM_TYPE");
    }

    [Fact]
    public void Declared_String_RefusesNonStrings_AndGuidIsCanonicalText()
    {
        Code(() => Normalize("[1]", declared: SqlParameterType.String)).ShouldBe("SQL_LIST_PARAM_TYPE");
        var guid = ListParameterNormalizer.Normalize(new[] { "0F8FAD5B-D9CB-469F-A165-70867728950E" }, Limits, SqlParameterType.String, guidStrings: true);
        guid[0].ShouldBe("0f8fad5b-d9cb-469f-a165-70867728950e");
        Code(() => ListParameterNormalizer.Normalize(new[] { "not-a-guid" }, Limits, SqlParameterType.String, guidStrings: true)).ShouldBe("SQL_LIST_PARAM_TYPE");
    }

    [Fact]
    public void ClrCollections_AreAccepted_ByTheEndpointLayer()
    {
        Elements(ListParameterNormalizer.Normalize(new List<object?> { "x", "y" }, Limits, null)).ShouldBe(new object[] { "x", "y" });
        Elements(ListParameterNormalizer.Normalize(new[] { 1L, 2L }, Limits, null)).ShouldBe(new object[] { 1L, 2L });
        Code(() => ListParameterNormalizer.Normalize(new List<object?> { "x", null }, Limits, null)).ShouldBe("SQL_LIST_PARAM_NULL");
    }

    [Fact]
    public void IsListCandidate_RecognisesArraysAndCollectionsOnly()
    {
        ListParameterNormalizer.IsListCandidate(Json("[1]")).ShouldBeTrue();
        ListParameterNormalizer.IsListCandidate(new[] { "a" }).ShouldBeTrue();
        ListParameterNormalizer.IsListCandidate(new List<object?>()).ShouldBeTrue();
        ListParameterNormalizer.IsListCandidate("abc").ShouldBeFalse();
        ListParameterNormalizer.IsListCandidate(new byte[] { 1 }).ShouldBeFalse();
        ListParameterNormalizer.IsListCandidate(Json("{\"a\":1}")).ShouldBeFalse();
        ListParameterNormalizer.IsListCandidate(Json("1")).ShouldBeFalse();
        ListParameterNormalizer.IsListCandidate(null).ShouldBeFalse();
        ListParameterNormalizer.IsListCandidate(5L).ShouldBeFalse();
    }

    // SEC-128-07 / M-7
    [Fact]
    [Trait("Category", "Security")]
    public void ElementCoercionError_ContainsNoValue()
    {
        var ex = Should.Throw<ClientListException>(() =>
            ListParameterNormalizer.Normalize(new object?[] { "super-secret-value" }, Limits, SqlParameterType.Int32));
        ex.Message.ShouldNotContain("secret");
        ex.InnerException.ShouldBeNull();
        ex.ToString().ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Normalizer_HugeJsonArray_CountIsCheckedBeforeElementsAreCopied()
    {
        // 1,000,001 empty-object elements would each be refused as TYPE if they were read; the length check must come first.
        var json = "[" + string.Join(",", Enumerable.Repeat("{}", 1_000_001)) + "]";
        Code(() => Normalize(json)).ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    [Fact]
    public void Culture_DoesNotInfluenceParsing()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Elements(ListParameterNormalizer.Normalize(new object?[] { "1.5" }, Limits, SqlParameterType.Decimal)).ShouldBe(new object[] { 1.5m });
            Code(() => ListParameterNormalizer.Normalize(new object?[] { "1,5" }, Limits, SqlParameterType.Int32)).ShouldBe("SQL_LIST_PARAM_TYPE");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
