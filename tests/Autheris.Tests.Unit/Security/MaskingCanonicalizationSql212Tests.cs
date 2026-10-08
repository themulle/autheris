namespace Autheris.Tests.Unit.Security;

using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using Autheris.Application.Services;
using Shouldly;
using Xunit;

/// <summary>
/// SQL2-12: the GraphQL tree path (JSON values from the database) and the REST paths (ADO.NET values) canonicalize a
/// value identically before masking, so HMAC pseudonyms match across APIs.
/// </summary>
public sealed class MaskingCanonicalizationSql212Tests
{
    [Theory]
    [InlineData("2026-10-07T12:00:00", "2026-10-07T12:00:00.0000000Z")]
    [InlineData("2026-10-07 12:00:00", "2026-10-07T12:00:00.0000000Z")]
    [InlineData("2026-10-07T14:00:00+02:00", "2026-10-07T12:00:00.0000000Z")]
    public void JsonTimestamps_MatchDateTimeValues(string json, string expected)
    {
        MaskingInputCanonicalizer.FromJson(JsonValue.Create(json)!).ShouldBe(expected);
        MaskingInputCanonicalizer.ToCanonicalString(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc)).ShouldBe(expected);
    }

    [Fact]
    public void Booleans_AndNumbers_MatchAcrossPaths()
    {
        MaskingInputCanonicalizer.FromJson(JsonNode.Parse("true")!).ShouldBe(MaskingInputCanonicalizer.ToCanonicalString(true));
        MaskingInputCanonicalizer.FromJson(JsonNode.Parse("1.50")!).ShouldBe(MaskingInputCanonicalizer.ToCanonicalString(1.50m));
        MaskingInputCanonicalizer.FromJson(JsonNode.Parse("42")!).ShouldBe(MaskingInputCanonicalizer.ToCanonicalString(42L));
    }

    [Fact]
    public void Numbers_AreCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            MaskingInputCanonicalizer.ToCanonicalString(1.5m).ShouldBe("1.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
