namespace Autheris.Tests.Unit.Api;

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Shouldly;
using Xunit;

public sealed class WebSqlValueWriterTests
{
    private sealed class Opaque;

    private static string Render(object? value, string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WebSqlValueWriter.Write(writer, value);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    public void Write_TemporalArms_AreInvariant(string culture)
    {
        Render(new DateTime(2023, 6, 1, 8, 30, 0, DateTimeKind.Unspecified), culture).ShouldBe("\"2023-06-01T08:30:00Z\"");
        Render(new DateTime(2023, 6, 1, 8, 30, 0, DateTimeKind.Utc).AddTicks(1234567), culture).ShouldBe("\"2023-06-01T08:30:00.1234567Z\"");
        Render(new DateTimeOffset(2023, 6, 1, 10, 30, 0, TimeSpan.FromHours(2)), culture).ShouldBe("\"2023-06-01T08:30:00Z\"");
        Render(new DateOnly(2023, 6, 1), culture).ShouldBe("\"2023-06-01\"");
        Render(new TimeOnly(8, 30), culture).ShouldBe("\"08:30:00\"");
        Render(new TimeSpan(1, 2, 3, 4), culture).ShouldBe("\"1.02:03:04\"");
        Render(1.5m, culture).ShouldBe("1.5");
        Render(1.5d, culture).ShouldBe("1.5");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WebSqlValueWriter_TemporalArms_CultureInvariant_TrTrDeDe()
    {
        foreach (var culture in new[] { "tr-TR", "de-DE" })
        {
            Render(new DateTime(2023, 6, 1, 8, 30, 0), culture).ShouldBe("\"2023-06-01T08:30:00Z\"");
            Render(new DateOnly(2023, 6, 1), culture).ShouldBe("\"2023-06-01\"");
            Render(new TimeOnly(8, 30, 15), culture).ShouldBe("\"08:30:15\"");
            Render(new DateTimeOffset(2023, 6, 1, 8, 30, 0, TimeSpan.Zero), culture).ShouldBe("\"2023-06-01T08:30:00Z\"");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WebSqlValueWriter_LocalKind_ConvertsToUtcZ()
    {
        var local = new DateTime(2023, 6, 1, 8, 30, 0, DateTimeKind.Local);
        var expected = local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
        Render(local, "de-DE").ShouldBe("\"" + expected + "\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WebSqlValueWriter_MinMaxValues_AllKinds_NoThrow()
    {
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local })
        {
            Should.NotThrow(() => Render(DateTime.SpecifyKind(DateTime.MinValue, kind), "tr-TR"));
            Should.NotThrow(() => Render(DateTime.SpecifyKind(DateTime.MaxValue, kind), "tr-TR"));
        }

        Should.NotThrow(() => Render(DateTimeOffset.MinValue, "de-DE"));
        Should.NotThrow(() => Render(DateTimeOffset.MaxValue, "de-DE"));
        Should.NotThrow(() => Render(DateOnly.MinValue, "de-DE"));
        Should.NotThrow(() => Render(DateOnly.MaxValue, "de-DE"));
        Should.NotThrow(() => Render(TimeOnly.MaxValue, "de-DE"));
        Should.NotThrow(() => Render(TimeSpan.MinValue, "de-DE"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WebSqlValueWriter_UnknownType_NoTypeNameOrExceptionLeak()
    {
        var rendered = Render(new Opaque(), "de-DE");
        rendered.ShouldNotContain(nameof(Opaque));
        rendered.ShouldNotContain("Autheris");
        rendered.ShouldBe("null");
    }
}
