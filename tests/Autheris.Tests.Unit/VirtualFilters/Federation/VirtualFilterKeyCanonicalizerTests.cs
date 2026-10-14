namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Exceptions;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-4 (M2, SEC-18): key values are compared as typed, canonical values chosen by the catalog type of the
/// protected column. Values that cannot be converted never authorize anything.
/// </summary>
public sealed class VirtualFilterKeyCanonicalizerTests
{
    [Theory]
    [InlineData("integer", VirtualFilterKeyType.IntegerNumber)]
    [InlineData("BIGINT", VirtualFilterKeyType.IntegerNumber)]
    [InlineData("int4", VirtualFilterKeyType.IntegerNumber)]
    [InlineData("smallint", VirtualFilterKeyType.IntegerNumber)]
    [InlineData("numeric(10,2)", VirtualFilterKeyType.DecimalNumber)]
    [InlineData("decimal", VirtualFilterKeyType.DecimalNumber)]
    [InlineData("uuid", VirtualFilterKeyType.Uuid)]
    [InlineData("uniqueidentifier", VirtualFilterKeyType.Uuid)]
    [InlineData("varchar", VirtualFilterKeyType.Text)]
    [InlineData("nvarchar(64)", VirtualFilterKeyType.Text)]
    [InlineData("character varying(255)", VirtualFilterKeyType.Text)]
    [InlineData("text", VirtualFilterKeyType.Text)]
    public void ForColumnType_SupportedTypes(string dataType, VirtualFilterKeyType expected)
    {
        VirtualFilterKeyCanonicalizer.ForColumnType(dataType, "crm_api.public.customers.id").ShouldBe(expected);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("timestamp")]
    [InlineData("bool")]
    [InlineData("boolean")]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("varbinary")]
    [InlineData("citext")]
    [InlineData("")]
    [InlineData(null)]
    public void ForColumnType_UnsupportedTypes_ThrowKeyTypeMismatch(string? dataType)
    {
        var ex = Should.Throw<GatewaySecurityException>(() => VirtualFilterKeyCanonicalizer.ForColumnType(dataType, "crm_api.public.customers.id"));

        ex.ErrorCode.ShouldBe("VF_KEY_TYPE_MISMATCH");
    }

    public static TheoryData<object> IntegerFortySeven => new() { 47L, 47, (short)47, (byte)47, 47UL, 47.0d, 47.0f, 47m, 47.00m, "47", "047", "+47" };

    [Theory]
    [MemberData(nameof(IntegerFortySeven))]
    public void Integer_AcceptsIntegralValues(object value)
    {
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.IntegerNumber, value, out var canonical).ShouldBeTrue();
        canonical.ShouldBeOfType<long>().ShouldBe(47L);
    }

    public static TheoryData<object?> NotAnInteger => new()
    {
        null, "4x", " 47", "47.0", "", 47.5d, 47.5m, true, double.NaN, double.PositiveInfinity,
        9007199254740992d, 9007199254740993d, -9007199254740992d, ulong.MaxValue, "{\"id\":1}", "[1]", Guid.Empty, new byte[] { 47 }
    };

    [Theory]
    [MemberData(nameof(NotAnInteger))]
    public void Integer_RejectsOtherValues(object? value)
    {
        // SEC-18: a double at or above 2^53 cannot prove its integer value (9007199254740993.0 parses as 2^53).
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.IntegerNumber, value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Integer_LargestExactDouble_IsAccepted()
    {
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.IntegerNumber, 9007199254740991d, out var canonical).ShouldBeTrue();
        canonical.ShouldBe(9007199254740991L);
    }

    [Fact]
    public void Decimal_TrailingZerosAndRoundTrippingDoubles_AreOneValue()
    {
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.DecimalNumber, 12.50m, out var a).ShouldBeTrue();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.DecimalNumber, "12.5", out var b).ShouldBeTrue();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.DecimalNumber, 12.5d, out var c).ShouldBeTrue();

        a.ShouldBe(12.5m);
        b.ShouldBe(12.5m);
        c.ShouldBe(12.5m);
        a.ToString().ShouldBe(b.ToString());
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.DecimalNumber, "12,5", out _).ShouldBeFalse();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.DecimalNumber, 1e30d, out _).ShouldBeFalse();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.DecimalNumber, true, out _).ShouldBeFalse();
    }

    [Fact]
    public void Text_IsOrdinalAndCaseSensitive_NumbersBecomeInvariantText()
    {
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Text, "abc", out var lower).ShouldBeTrue();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Text, "ABC", out var upper).ShouldBeTrue();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Text, 47L, out var number).ShouldBeTrue();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Text, -1234567, out var negative).ShouldBeTrue();

        lower.ShouldNotBe(upper);
        number.ShouldBe("47");
        negative.ShouldBe("-1234567");
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Text, true, out _).ShouldBeFalse();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Text, 4.5d, out _).ShouldBeFalse();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Text, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Uuid_IsCanonical()
    {
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Uuid, "6F9619FF-8B86-D011-B42D-00C04FC964FF", out var upper).ShouldBeTrue();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Uuid, id, out var guid).ShouldBeTrue();

        upper.ShouldBe(id);
        guid.ShouldBe(id);
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Uuid, "not-a-guid", out _).ShouldBeFalse();
        VirtualFilterKeyCanonicalizer.TryCanonicalize(VirtualFilterKeyType.Uuid, 47L, out _).ShouldBeFalse();
    }

    [Fact]
    public void Key_EqualityIsTypedAndOrdinal()
    {
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.IntegerNumber, VirtualFilterKeyType.Text], ["47", "a"], out var a).ShouldBeTrue();
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.IntegerNumber, VirtualFilterKeyType.Text], [47L, "a"], out var b).ShouldBeTrue();
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.IntegerNumber, VirtualFilterKeyType.Text], [47L, "A"], out var c).ShouldBeTrue();
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.Text], ["47"], out var text).ShouldBeTrue();
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.IntegerNumber], [47L], out var integer).ShouldBeTrue();

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(c);
        text.ShouldNotBe(integer);
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.IntegerNumber, VirtualFilterKeyType.Text], [47L, null], out _).ShouldBeFalse();
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.IntegerNumber], [47L, "extra"], out _).ShouldBeFalse();
    }

    [Fact]
    public void Key_ToStringDoesNotRevealValues()
    {
        VirtualFilterKey.TryCreate([VirtualFilterKeyType.Text], ["secret-key-value"], out var key).ShouldBeTrue();

        key.ToString().ShouldNotContain("secret-key-value");
    }
}
