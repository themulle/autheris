namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Diagnostics;
using System.Linq;
using Autheris.Domain.Common;
using FsCheck;
using FsCheck.Xunit;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 1: binding patterns "source.schema.object[.column]". A segment is a name with wildcards
/// (* and ?) or a regular expression in parentheses that must match the whole segment; only dots outside parentheses
/// separate segments.
/// </summary>
public sealed class ObjectPatternTests
{
    private static readonly TableIdentifier Air1 = new("lwetem_prod", "fms", "air1");

    [Fact]
    public void FourSegments_MatchObjectAndColumn()
    {
        var pattern = ObjectPattern.Parse("lwetem_prod.*.*.client_id");

        pattern.HasColumnSegment.ShouldBeTrue();
        pattern.MatchesObject(Air1).ShouldBeTrue();
        pattern.MatchesColumn("client_id").ShouldBeTrue();
    }

    [Fact]
    public void Segments_AreAnchored()
    {
        var pattern = ObjectPattern.Parse("lwetem_prod.*.*.client_id");

        pattern.MatchesObject(new TableIdentifier("lwetem_prodX", "fms", "air1")).ShouldBeFalse();
        pattern.MatchesColumn("client_idx").ShouldBeFalse();
        pattern.MatchesColumn("xclient_id").ShouldBeFalse();
    }

    [Theory]
    [InlineData("fms", true)]
    [InlineData("tem", true)]
    [InlineData("dm", false)]
    [InlineData("fmsx", false)]
    public void RegexSegment_MustMatchTheWholeSegment(string schema, bool expected)
    {
        ObjectPattern.Parse("lwetem_prod.(fms|tem).*.client_id")
            .MatchesObject(new TableIdentifier("lwetem_prod", schema, "air1")).ShouldBe(expected);
    }

    [Fact]
    public void DotInsideParentheses_DoesNotSeparate()
    {
        var pattern = ObjectPattern.Parse("src.(a.b|c).*");

        pattern.HasColumnSegment.ShouldBeFalse();
        pattern.MatchesObject(new TableIdentifier("src", "aXb", "t")).ShouldBeTrue();
        pattern.MatchesObject(new TableIdentifier("src", "c", "t")).ShouldBeTrue();
        pattern.MatchesColumn("anything").ShouldBeTrue();
    }

    [Fact]
    public void Backslash_IsAnOrdinaryCharacterInNames()
    {
        ObjectPattern.Parse(@"lwetem_prod.LWEW2K\LWEMUM2.*.*")
            .MatchesObject(new TableIdentifier("lwetem_prod", @"LWEW2K\LWEMUM2", "t")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("fm?", "fms", true)]
    [InlineData("fm?", "fm", false)]
    [InlineData("fm?", "fmsx", false)]
    [InlineData("client_*", "client_", true)]
    [InlineData("client_*", "client_conf", true)]
    [InlineData("*_conf", "client_conf", true)]
    [InlineData("a+b", "a+b", true)]
    [InlineData("a+b", "aab", false)]
    public void Wildcards_StarAndQuestionMark_OtherCharactersAreLiteral(string segment, string name, bool expected)
    {
        ObjectPattern.Parse($"src.{segment}.t").MatchesObject(new TableIdentifier("src", name, "t")).ShouldBe(expected);
    }

    [Fact]
    public void Matching_IgnoresCase()
    {
        ObjectPattern.Parse("LWETEM_PROD.FMS.*.CLIENT_ID").MatchesObject(Air1).ShouldBeTrue();
        ObjectPattern.Parse("lwetem_prod.(FMS|TEM).*.Client_*").MatchesColumn("client_id").ShouldBeTrue();
        ObjectPattern.Parse("lwetem_prod.(FMS|TEM).*").MatchesObject(Air1).ShouldBeTrue();
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a.b.c.d.e")]
    [InlineData("a..b.c")]
    [InlineData(".a.b.c")]
    [InlineData("a.(fms.c.d")]
    [InlineData("a.fms).c.d")]
    [InlineData("a.().c.d")]
    [InlineData("a.x(y).c")]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidPatterns_AreRejected(string pattern)
    {
        Should.Throw<ArgumentException>(() => ObjectPattern.Parse(pattern));
        ObjectPattern.TryParse(pattern, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(@"src.((a)\1).t")]
    [InlineData("src.((?=a)a).t")]
    [InlineData("src.((?<!a)b).t")]
    public void RegexConstructsWithoutLinearMatching_AreRejected(string pattern)
    {
        Should.Throw<ArgumentException>(() => ObjectPattern.Parse(pattern));
    }

    [Fact]
    public void CatastrophicBacktrackingPattern_RunsInLinearTime()
    {
        var pattern = ObjectPattern.Parse("src.((a+)+).t");
        var name = new string('a', 50_000) + "b";

        var watch = Stopwatch.StartNew();
        pattern.MatchesObject(new TableIdentifier("src", name, "t")).ShouldBeFalse();
        watch.ElapsedMilliseconds.ShouldBeLessThan(2000);
    }

    [Fact]
    public void LengthLimits_AreEnforced()
    {
        Should.Throw<ArgumentException>(() => ObjectPattern.Parse("src." + new string('a', 257) + ".t"));
        // four segments of 256 characters each: every segment is allowed, the pattern (1027 characters) is not
        Should.Throw<ArgumentException>(() => ObjectPattern.Parse(string.Join(".", new string('a', 256), new string('b', 256), new string('c', 256), new string('d', 256))));
        Should.NotThrow(() => ObjectPattern.Parse(string.Join(".", new string('a', 256), new string('b', 256), new string('c', 256), new string('d', 200))));
    }

    [Fact]
    public void FourFieldForm_MatchesLikeTheStringForm()
    {
        var fields = ObjectPattern.FromParts("lwetem_prod", "(fms|tem)", "*", "client_id");
        var text = ObjectPattern.Parse("lwetem_prod.(fms|tem).*.client_id");

        foreach (var schema in new[] { "fms", "tem", "dm" })
        {
            var id = new TableIdentifier("lwetem_prod", schema, "air1");
            fields.MatchesObject(id).ShouldBe(text.MatchesObject(id));
        }

        fields.MatchesColumn("client_id").ShouldBeTrue();
        ObjectPattern.FromParts("lwetem_prod", "fms", "*", null).HasColumnSegment.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => ObjectPattern.FromParts("lwetem_prod", "", "*", null));
    }

    [Fact]
    public void Text_IsTheNormalizedPattern()
    {
        ObjectPattern.Parse("lwetem_prod.(fms|tem).*.client_id").Text.ShouldBe("lwetem_prod.(fms|tem).*.client_id");
        ObjectPattern.FromParts("lwetem_prod", "fms", "*", "client_id").Text.ShouldBe("lwetem_prod.fms.*.client_id");
    }

    [Property(MaxTest = 200)]
    public bool SegmentWithoutWildcards_MatchesExactlyTheSameName(NonEmptyString first, NonEmptyString second)
    {
        var a = Sanitize(first.Get);
        var b = Sanitize(second.Get);
        var pattern = ObjectPattern.Parse($"src.{a}.t");
        return pattern.MatchesObject(new TableIdentifier("src", b, "t")) == string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(40).ToArray();
        return chars.Length == 0 ? "x" : new string(chars);
    }
}
