using Autheris.Domain.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class SidFormatTests
{
    [Theory]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-512")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-12-1-1234567890-2345678901-3456789012-4294967295")]
    [InlineData("S-1-1-0")]
    public void IsCanonical_accepts_domain_and_cloud_sids(string sid) => SidFormat.IsCanonical(sid).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5")]
    [InlineData("S-1-5-")]
    [InlineData("S-1-5-21--1")]
    [InlineData("s-1-5-21-1")]
    [InlineData("S-2-5-21-1")]
    [InlineData("S-1-123-21-1")]
    [InlineData("S-1-5-21-12345678901")]
    [InlineData("S-1-5-21-1 ")]
    [InlineData("S-1-5-21-１２３")]
    [InlineData("S-1-5-21-FORWARD-GRP-finance")]
    [InlineData("S-1-5-21-FORWARD-USR-alice")]
    public void IsCanonical_rejects_non_ascii_overlong_and_pseudo_values(string? sid) => SidFormat.IsCanonical(sid).ShouldBeFalse();

    [Fact]
    public void IsCanonical_bounds_length_and_subauthority_count()
    {
        SidFormat.IsCanonical("S-1-5" + string.Concat(Enumerable.Repeat("-1", 15))).ShouldBeTrue();
        SidFormat.IsCanonical("S-1-5" + string.Concat(Enumerable.Repeat("-1", 16))).ShouldBeFalse();
        SidFormat.IsCanonical("S-1-5-21-" + new string('1', 200)).ShouldBeFalse();
    }

    [Fact]
    public void TryFromEntraObjectId_matches_known_vector()
    {
        SidFormat.TryFromEntraObjectId(Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00"), out var sid).ShouldBeTrue();
        sid.Value.ShouldBe("S-1-12-1-287454020-2005423462-3434850969-16772829");
        SidFormat.TryFromEntraObjectId(Guid.Empty, out _).ShouldBeFalse();
    }

    private static byte[] Binary(byte revision, byte count, byte authority, params uint[] sub)
    {
        var bytes = new byte[8 + 4 * sub.Length];
        bytes[0] = revision;
        bytes[1] = count;
        bytes[7] = authority;
        for (var i = 0; i < sub.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + 4 * i), sub[i]);
        }

        return bytes;
    }

    [Fact]
    public void TryFromBinary_parses_tokenGroups_entry()
    {
        var raw = Convert.FromHexString("01050000000000051500000001000000020000000300000051040000");
        SidFormat.TryFromBinary(raw, out var sid).ShouldBeTrue();
        sid.Value.ShouldBe("S-1-5-21-1-2-3-1105");
    }

    [Fact]
    public void TryFromBinary_rejects_truncated_overlong_and_bad_revision()
    {
        var good = Binary(1, 2, 5, 21, 1105);
        SidFormat.TryFromBinary(good, out _).ShouldBeTrue();
        SidFormat.TryFromBinary(good.AsSpan(0, good.Length - 1), out _).ShouldBeFalse();
        SidFormat.TryFromBinary([], out _).ShouldBeFalse();
        SidFormat.TryFromBinary(Binary(2, 2, 5, 21, 1105), out _).ShouldBeFalse();
        SidFormat.TryFromBinary(Binary(1, 0, 5), out _).ShouldBeFalse();
        SidFormat.TryFromBinary(Binary(1, 2, 200, 21, 1105), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryFromBinary_rejects_subauthority_count_above_15()
    {
        SidFormat.TryFromBinary(Binary(1, 15, 5, new uint[15]), out _).ShouldBeTrue();
        SidFormat.TryFromBinary(Binary(1, 16, 5, new uint[16]), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryFromBinary_rejects_trailing_bytes()
    {
        var withTrailing = Binary(1, 2, 5, 21, 1105).Concat(new byte[] { 0 }).ToArray();
        SidFormat.TryFromBinary(withTrailing, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryFromBinary_never_throws_on_random_input()
    {
        var random = new Random(4711);
        var buffer = new byte[80];
        for (var i = 0; i < 20_000; i++)
        {
            random.NextBytes(buffer);
            var length = random.Next(0, buffer.Length + 1);
            if (random.Next(3) == 0 && length > 1)
            {
                buffer[0] = 1;
                buffer[1] = (byte)random.Next(0, 17);
            }

            Should.NotThrow(() => SidFormat.TryFromBinary(buffer.AsSpan(0, length), out _));
        }
    }

    [Theory]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-21-1-2-3-500", true)]
    [InlineData("S-1-5-21-1-2-3-512", true)]
    [InlineData("S-1-5-21-1-2-3-519", true)]
    [InlineData("S-1-5-21-1-2-3-1105", false)]
    [InlineData("S-1-12-1-1-2-3-500", false)]
    [InlineData("S-1-1-0", false)]
    public void IsPrivileged_flags_builtin_and_rid_500_512_519(string sid, bool expected) =>
        SidFormat.IsPrivileged(new Sid(sid)).ShouldBe(expected);

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    [InlineData("S-1-5-21-1-2-3-516")]
    [InlineData("S-1-5-21-1-2-3-518")]
    public void IsPrivileged_flags_service_and_domain_controller_sids(string sid) =>
        SidFormat.IsPrivileged(new Sid(sid)).ShouldBeTrue();
}
