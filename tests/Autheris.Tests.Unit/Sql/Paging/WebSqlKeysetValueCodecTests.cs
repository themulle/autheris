namespace Autheris.Tests.Unit.Sql.Paging;

using System;
using System.Globalization;
using Autheris.Application.Sql.Paging;
using Shouldly;
using Xunit;

public sealed class WebSqlKeysetValueCodecTests
{
    public static TheoryData<object> SupportedValues => new()
    {
        (byte)200,
        (short)-123,
        42,
        long.MinValue,
        123456789.1234567m,
        "a:b cä",
        Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        new DateOnly(2024, 2, 29),
        new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.FromMinutes(-210))
    };

    [Theory]
    [MemberData(nameof(SupportedValues))]
    public void RoundTrip_AllSupportedTypes_ExactClrTypeAndValue(object value)
    {
        WebSqlKeysetValueCodec.TryEncode(value, out var encoded).ShouldBeTrue();
        WebSqlKeysetValueCodec.TryDecode(encoded, out var decoded).ShouldBeTrue();
        decoded.GetType().ShouldBe(value.GetType());
        decoded.ShouldBe(value);
        if (value is DateTimeOffset dto)
        {
            ((DateTimeOffset)decoded).Offset.ShouldBe(dto.Offset);
        }
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void DateTimeKind_Preserved(DateTimeKind kind)
    {
        var value = new DateTime(2024, 1, 2, 3, 4, 5, 6, kind);
        WebSqlKeysetValueCodec.TryEncode(value, out var encoded).ShouldBeTrue();
        WebSqlKeysetValueCodec.TryDecode(encoded, out var decoded).ShouldBeTrue();
        ((DateTime)decoded).Kind.ShouldBe(kind);
        ((DateTime)decoded).Ticks.ShouldBe(value.Ticks);
    }

    [Fact]
    public void Decimal_ScalePreserved()
    {
        WebSqlKeysetValueCodec.TryEncode(1.50m, out var encoded).ShouldBeTrue();
        encoded.ShouldBe("dec:1.50");
        WebSqlKeysetValueCodec.TryDecode(encoded, out var decoded).ShouldBeTrue();
        ((decimal)decoded).ToString(CultureInfo.InvariantCulture).ShouldBe("1.50");
    }

    [Fact]
    public void Unsupported_Double_Bool_Bytes_Null_Rejected()
    {
        WebSqlKeysetValueCodec.TryEncode(1.5d, out _).ShouldBeFalse();
        WebSqlKeysetValueCodec.TryEncode(true, out _).ShouldBeFalse();
        WebSqlKeysetValueCodec.TryEncode(new byte[] { 1 }, out _).ShouldBeFalse();
        WebSqlKeysetValueCodec.TryEncode(null, out _).ShouldBeFalse();
        WebSqlKeysetValueCodec.TryEncode(DBNull.Value, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("i4")]
    [InlineData(":5")]
    [InlineData("i4:")]
    [InlineData("i4:abc")]
    [InlineData("i4:+5")]
    [InlineData("i4:05")]
    [InlineData("u1:256")]
    [InlineData("g:not-a-guid")]
    [InlineData("d:-1")]
    [InlineData("dt:5")]
    [InlineData("dt:5:X")]
    [InlineData("dt:3155378976000000000:U")]
    [InlineData("dto:5:9999")]
    [InlineData("dto:5")]
    public void MalformedTag_Rejected(string encoded)
    {
        WebSqlKeysetValueCodec.TryDecode(encoded, out _).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("zz:5")]
    [InlineData("I4:5")]
    [InlineData("f8:1.5")]
    public void KeysetValue_UnknownTag_Rejected(string encoded)
    {
        WebSqlKeysetValueCodec.TryDecode(encoded, out _).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("i4:5 ")]
    [InlineData("i4:5x")]
    [InlineData("i4:5\n")]
    [InlineData("dec:1.5 ")]
    [InlineData("dec:1,5")]
    [InlineData("g:0f8fad5b-d9cb-469f-a165-70867728950e ")]
    [InlineData("dt:5:U:extra")]
    public void KeysetValue_TrailingGarbage_Rejected(string encoded)
    {
        WebSqlKeysetValueCodec.TryDecode(encoded, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetValue_Over256Chars_Rejected()
    {
        WebSqlKeysetValueCodec.TryEncode(new string('x', 254), out var ok).ShouldBeTrue();
        ok.Length.ShouldBe(256);
        WebSqlKeysetValueCodec.TryDecode(ok, out _).ShouldBeTrue();
        WebSqlKeysetValueCodec.TryEncode(new string('x', 255), out _).ShouldBeFalse();
        WebSqlKeysetValueCodec.TryDecode("s:" + new string('x', 255), out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetValue_CultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            WebSqlKeysetValueCodec.TryEncode(1234.5m, out var encoded).ShouldBeTrue();
            encoded.ShouldBe("dec:1234.5");
            WebSqlKeysetValueCodec.TryEncode(-7, out var neg).ShouldBeTrue();
            neg.ShouldBe("i4:-7");
            WebSqlKeysetValueCodec.TryDecode(encoded, out var decoded).ShouldBeTrue();
            decoded.ShouldBe(1234.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
