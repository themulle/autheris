namespace Autheris.Tests.Unit.Databricks;

using System.Globalization;
using Autheris.Infrastructure.Databricks;
using Shouldly;
using Xunit;

public sealed class DatabricksTypeMapTests
{
    public static TheoryData<string, int?, string, Type, object> MappedValues() => new()
    {
        { "BOOLEAN", null, "true", typeof(bool), true },
        { "BOOLEAN", null, "false", typeof(bool), false },
        { "BYTE", null, "-128", typeof(sbyte), (sbyte)-128 },
        { "BYTE", null, "127", typeof(sbyte), (sbyte)127 },
        { "SHORT", null, "-32768", typeof(short), (short)-32768 },
        { "SHORT", null, "32767", typeof(short), (short)32767 },
        { "INT", null, "-2147483648", typeof(int), int.MinValue },
        { "INT", null, "42", typeof(int), 42 },
        { "LONG", null, "9000000000", typeof(long), 9_000_000_000L },
        { "FLOAT", null, "1.5", typeof(float), 1.5f },
        { "DOUBLE", null, "2.25", typeof(double), 2.25d },
        { "DECIMAL", 18, "12345.67", typeof(decimal), 12345.67m },
        { "DECIMAL", 28, "99.95", typeof(decimal), 99.95m },
        { "DECIMAL", null, "10.50", typeof(decimal), 10.50m },
        { "DECIMAL", 29, "123456789012345678901234567890.12", typeof(string), "123456789012345678901234567890.12" },
        { "DECIMAL", 38, "99999999999999999999999999999999.123456", typeof(string), "99999999999999999999999999999999.123456" },
        { "DATE", null, "2026-10-11", typeof(DateTime), new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Unspecified) },
        { "TIMESTAMP", null, "2026-10-11T12:30:45.123Z", typeof(DateTimeOffset), new DateTimeOffset(2026, 10, 11, 12, 30, 45, 123, TimeSpan.Zero) },
        { "TIMESTAMP_NTZ", null, "2026-10-11T12:30:45.123", typeof(DateTime), new DateTime(2026, 10, 11, 12, 30, 45, 123, DateTimeKind.Unspecified) },
        { "STRING", null, "hello", typeof(string), "hello" },
        { "CHAR", null, "A", typeof(string), "A" },
        { "VARCHAR", null, "abc", typeof(string), "abc" },
        { "BINARY", null, "AQID", typeof(string), "AQID" },
        { "INTERVAL", null, "1 02:03:04", typeof(string), "1 02:03:04" },
        { "ARRAY", null, "[1,2]", typeof(string), "[1,2]" },
        { "MAP", null, "{\"a\":1}", typeof(string), "{\"a\":1}" },
        { "STRUCT", null, "{\"x\":1}", typeof(string), "{\"x\":1}" },
        { "VARIANT", null, "{\"v\":true}", typeof(string), "{\"v\":true}" },
        { "UNKNOWN_FUTURE_TYPE", null, "raw", typeof(string), "raw" },
        { "int", null, "7", typeof(int), 7 }
    };

    [Theory]
    [MemberData(nameof(MappedValues))]
    public void Maps(string typeName, int? precision, string raw, Type expectedClr, object expectedValue)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var clr = DatabricksTypeMap.ResolveClrType(typeName, precision);
            clr.ShouldBe(expectedClr);

            var parsed = DatabricksTypeMap.ParseCell(raw, typeName, precision);
            parsed.ShouldNotBeNull();
            parsed.GetType().ShouldBe(expectedClr);
            parsed.ShouldBe(expectedValue);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(29)]
    [InlineData(38)]
    public void Decimal38_StaysString(int precision)
    {
        const string large = "123456789012345678901234567890.12345678";
        DatabricksTypeMap.ResolveClrType("DECIMAL", precision).ShouldBe(typeof(string));
        var parsed = DatabricksTypeMap.ParseCell(large, "DECIMAL", precision);
        parsed.ShouldBeOfType<string>().ShouldBe(large);
    }

    [Theory]
    [InlineData("INT", null, "not-an-int")]
    [InlineData("INT", null, "99999999999999")]
    [InlineData("BYTE", null, "200")]
    [InlineData("SHORT", null, "40000")]
    [InlineData("LONG", null, "12.34")]
    [InlineData("BOOLEAN", null, "1")]
    [InlineData("FLOAT", null, "abc")]
    [InlineData("DOUBLE", null, "1,234")]
    [InlineData("DECIMAL", 18, "1,234.56")]
    [InlineData("DECIMAL", 18, "NaN")]
    [InlineData("DATE", null, "11.10.2026")]
    [InlineData("TIMESTAMP", null, "not-a-timestamp")]
    [InlineData("TIMESTAMP_NTZ", null, "not-a-timestamp")]
    public void UnparsableInt_ThrowsProtocol(string typeName, int? precision, string badRaw)
    {
        var ex = Should.Throw<DatabricksProviderException>(() =>
            DatabricksTypeMap.ParseCell(badRaw, typeName, precision));

        ex.Code.ShouldBe(DatabricksErrorCodes.ProtocolViolation);
        ex.Message.ShouldNotContain(badRaw);
    }

    [Theory]
    [InlineData("BOOLEAN", null)]
    [InlineData("BYTE", null)]
    [InlineData("SHORT", null)]
    [InlineData("INT", null)]
    [InlineData("LONG", null)]
    [InlineData("FLOAT", null)]
    [InlineData("DOUBLE", null)]
    [InlineData("DECIMAL", 18)]
    [InlineData("DECIMAL", 38)]
    [InlineData("DATE", null)]
    [InlineData("TIMESTAMP", null)]
    [InlineData("TIMESTAMP_NTZ", null)]
    [InlineData("STRING", null)]
    [InlineData("CHAR", null)]
    [InlineData("VARCHAR", null)]
    [InlineData("BINARY", null)]
    [InlineData("INTERVAL", null)]
    [InlineData("ARRAY", null)]
    [InlineData("MAP", null)]
    [InlineData("STRUCT", null)]
    [InlineData("VARIANT", null)]
    public void Null_IsDbNull_ForEveryType(string typeName, int? precision)
    {
        DatabricksTypeMap.ParseCell(null, typeName, precision).ShouldBe(DBNull.Value);
    }

    [Theory]
    [InlineData("2026-10-11T14:30:45.123+02:00", 2026, 10, 11, 12, 30, 45, 123)]
    [InlineData("2026-10-11T12:30:45.123Z", 2026, 10, 11, 12, 30, 45, 123)]
    [InlineData("2026-10-11 12:30:45.123000", 2026, 10, 11, 12, 30, 45, 123)]
    public void Timestamp_IsUtcOffset(string raw, int year, int month, int day, int hour, int minute, int second, int ms)
    {
        var parsed = DatabricksTypeMap.ParseCell(raw, "TIMESTAMP", null);
        var dto = parsed.ShouldBeOfType<DateTimeOffset>();
        dto.Offset.ShouldBe(TimeSpan.Zero);
        dto.ShouldBe(new DateTimeOffset(year, month, day, hour, minute, second, ms, TimeSpan.Zero));
    }

    [Fact]
    public void TimestampNtz_HasUnspecifiedKind()
    {
        var parsed = DatabricksTypeMap.ParseCell("2026-10-11 12:30:45.123", "TIMESTAMP_NTZ", null);
        var dt = parsed.ShouldBeOfType<DateTime>();
        dt.Kind.ShouldBe(DateTimeKind.Unspecified);
        dt.ShouldBe(new DateTime(2026, 10, 11, 12, 30, 45, 123, DateTimeKind.Unspecified));
    }

    [Fact]
    public void FloatSpecialValues_ParseCorrectly()
    {
        float.IsNaN((float)DatabricksTypeMap.ParseCell("NaN", "FLOAT", null)).ShouldBeTrue();
        float.IsPositiveInfinity((float)DatabricksTypeMap.ParseCell("Infinity", "FLOAT", null)).ShouldBeTrue();
        float.IsNegativeInfinity((float)DatabricksTypeMap.ParseCell("-Infinity", "FLOAT", null)).ShouldBeTrue();

        double.IsNaN((double)DatabricksTypeMap.ParseCell("NaN", "DOUBLE", null)).ShouldBeTrue();
        double.IsPositiveInfinity((double)DatabricksTypeMap.ParseCell("Infinity", "DOUBLE", null)).ShouldBeTrue();
        double.IsNegativeInfinity((double)DatabricksTypeMap.ParseCell("-Infinity", "DOUBLE", null)).ShouldBeTrue();
    }
}
