using System.Text.RegularExpressions;
using Autheris.Application.Data.Services;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.DataApi;

/// <summary>#326 (GAP-035, SEC-RG-12): the generated UTC-offset arms of a zone for SQLite.</summary>
public sealed class SqliteZoneOffsetTableTests
{
    [Fact]
    public void Berlin_WinterAndSummer()
    {
        var table = SqliteZoneOffsetTable.Get("Europe/Berlin");

        table.BaseOffset.ShouldBe("+01:00");
        table.Arms.ShouldContain(a => a.FromUtc == "2025-03-30 01:00:00" && a.ToUtc == "2025-10-26 01:00:00" && a.Offset == "+02:00");
        // One summer arm per year of the range.
        table.Arms.Count(a => a.Offset == "+02:00" && a.FromUtc.StartsWith("2025-", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void ZoneWithoutDst_SingleArm()
    {
        SqliteZoneOffsetTable.Get("Asia/Kolkata").Arms.ShouldBeEmpty();
        SqliteZoneOffsetTable.OffsetModifier("Asia/Kolkata", "datetime(\"ts\")").ShouldBe("'+05:30'");
    }

    [Fact]
    public void HalfHourZone()
    {
        SqliteZoneOffsetTable.Get("Asia/Kolkata").BaseOffset.ShouldBe("+05:30");
    }

    [Fact]
    public void NegativeOffsetZone_FormatsTheSign()
    {
        SqliteZoneOffsetTable.Get("America/New_York").BaseOffset.ShouldBe("-05:00");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_RG_12_NonAllowlistedZone_FailsClosed()
    {
        Should.Throw<ArgumentException>(() => SqliteZoneOffsetTable.OffsetModifier("Mars/Olympus' OR 1=1 --", "datetime(\"ts\")"));
        Should.Throw<ArgumentException>(() => SqliteZoneOffsetTable.Get("Not/AZone"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_RG_12_LiteralsInvariantFormat_FixedShape()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            var expression = SqliteZoneOffsetTable.OffsetModifier("Europe/Berlin", "datetime(\"ts\")");

            // Only fixed-shape literals: a timestamp or an offset between single quotes, nothing else besides the column expression.
            var literals = Regex.Matches(expression, "'([^']*)'").Select(m => m.Groups[1].Value).ToList();
            literals.ShouldNotBeEmpty();
            literals.ShouldAllBe(l => Regex.IsMatch(l, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}|[+-]\d{2}:\d{2})$"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_RG_12_TableIsBounded_AndCachedPerZone()
    {
        var first = SqliteZoneOffsetTable.Get("Europe/Berlin");

        first.Arms.Count.ShouldBeLessThanOrEqualTo(2 * (SqliteZoneOffsetTable.LastYear - SqliteZoneOffsetTable.FirstYear + 1) + 4);
        SqliteZoneOffsetTable.Get("Europe/Berlin").ShouldBeSameAs(first);
    }
}
