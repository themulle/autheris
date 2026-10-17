namespace TrinoSqlEngine.Tests.TimeZones;

using Xunit;

public sealed class TimeZoneRegistryTests
{
    private static readonly string ResourceText = TimeZoneRegistry.LoadResourceText()!;

    private static IEnumerable<(string Iana, string Windows)> Entries() =>
        ResourceText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l[0] != '#')
            .Select(l => l.Split(';'))
            .Select(p => (p[0], p[1]));

    [Fact]
    public void T2_TheResourceIsEmbeddedAndLoadsThroughTheManifest()
    {
        Assert.NotNull(ResourceText);
        Assert.StartsWith("#", ResourceText, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ResourceText);
        Assert.True(TimeZoneRegistry.Count >= TimeZoneRegistry.MinimumEntries);
    }

    [Fact]
    public void T2_EveryEntryMatchesThePatternsAndHasNoDuplicate()
    {
        var all = Entries().ToList();
        Assert.Equal(all.Count, all.Select(e => e.Iana).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, TimeZoneRegistry.Count);
        foreach (var (iana, windows) in all)
        {
            Assert.Matches(@"\A[A-Za-z0-9_+\-/]{1,64}\z", iana);
            Assert.Matches(@"\A[A-Za-z0-9 .,()+\-]{1,64}\z", windows);
            Assert.True(TimeZoneRegistry.Contains(iana));
        }
    }

    [Theory]
    [InlineData("UTC", "UTC")]
    [InlineData("Etc/UTC", "UTC")]
    [InlineData("Europe/Berlin", "W. Europe Standard Time")]
    [InlineData("America/New_York", "Eastern Standard Time")]
    [InlineData("Asia/Kolkata", "India Standard Time")]
    [InlineData("Australia/Lord_Howe", "Lord Howe Standard Time")]
    [InlineData("America/St_Johns", "Newfoundland Standard Time")]
    [InlineData("Pacific/Kiritimati", "Line Islands Standard Time")]
    public void T2_AnchorsArePresentWithTheExpectedWindowsIds(string iana, string windows)
    {
        Assert.True(TimeZoneRegistry.Contains(iana));
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.SqlServer, iana, out var value));
        Assert.Equal(windows, value);
    }

    [Theory]
    [InlineData("europe/berlin")]
    [InlineData("EUROPE/BERLIN")]
    [InlineData("Europe/Berlin ")]
    [InlineData(" Europe/Berlin")]
    [InlineData("Europe/Berlin\0")]
    [InlineData("Europe/Berlin\n")]
    [InlineData("EST")]
    [InlineData("")]
    public void T2_LookupIsOrdinalAndNeverTrimsOrNormalizes(string value)
    {
        Assert.False(TimeZoneRegistry.Contains(value));
        Assert.False(TimeZoneRegistry.TryResolve(TargetSqlDialect.PostgreSql, value, out var mapped));
        Assert.Equal(string.Empty, mapped);
    }

    [Fact]
    public void T2_NullIsNotAMember()
    {
        Assert.False(TimeZoneRegistry.Contains(null!));
        Assert.False(TimeZoneRegistry.TryResolve(TargetSqlDialect.SqlServer, null!, out _));
    }

    [Fact]
    public void T3_TryResolveMapsPerDialectAndReturnsRegistryOwnedStrings()
    {
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.SqlServer, "Europe/Berlin", out var sqlServer));
        Assert.Equal("W. Europe Standard Time", sqlServer);
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.PostgreSql, "Europe/Berlin", out var postgres));
        Assert.Equal("Europe/Berlin", postgres);
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.DuckDb, "Europe/Berlin", out var duck));
        Assert.Equal("Europe/Berlin", duck);

        foreach (var dialect in new[] { TargetSqlDialect.Sqlite, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks, TargetSqlDialect.Ansi, TargetSqlDialect.Snowflake })
        {
            Assert.False(TimeZoneRegistry.TryResolve(dialect, "Europe/Berlin", out var none));
            Assert.Equal(string.Empty, none);
        }

        // The value is a registry string: a second lookup returns the same instance, a caller's equal string is never returned.
        var callerCopy = new string("Europe/Berlin".AsSpan());
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.PostgreSql, callerCopy, out var first));
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.PostgreSql, "Europe/Berlin", out var second));
        Assert.Same(first, second);
        Assert.NotSame(callerCopy, first);
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.SqlServer, callerCopy, out var w1));
        Assert.True(TimeZoneRegistry.TryResolve(TargetSqlDialect.SqlServer, "Europe/Berlin", out var w2));
        Assert.Same(w1, w2);
    }

    [Theory]
    [InlineData("Europe/Berlin'; SELECT 1--")]
    [InlineData("Europe/Berlin/*x*/")]
    [InlineData("{{x}}")]
    [InlineData("%")]
    [InlineData("\\")]
    [InlineData("   ")]
    [InlineData("Еurope/Berlin")]          // Cyrillic IE
    [InlineData("Europe/Berlin​")]         // zero width space
    public void T4_HostileInputsAreNotMembers(string value)
    {
        Assert.False(TimeZoneRegistry.Contains(value));
        Assert.False(TimeZoneRegistry.TryResolve(TargetSqlDialect.SqlServer, value, out _));
    }

    [Fact]
    public void T4_AnOverlongIdIsNotAMember()
    {
        Assert.False(TimeZoneRegistry.Contains(new string('a', 65)));
        Assert.False(TimeZoneRegistry.Contains("Europe/" + new string('a', 58)));
    }

    [Fact]
    public void T5_SelfTestPassesOnTheEmbeddedResource()
    {
        var result = TimeZoneRegistry.SelfTest();
        Assert.True(result.Passed);
        Assert.Null(result.FailedCheck);
    }

    [Fact]
    public void T5_ChangedHashFailsWithItsOwnCheckId()
    {
        var result = TimeZoneRegistry.SelfTest(ResourceText, new string('0', 64));
        Assert.False(result.Passed);
        Assert.Equal("hash", result.FailedCheck);
    }

    [Fact]
    public void T5_MissingAnchorFailsWithItsOwnCheckId()
    {
        string tampered = ResourceText.Replace("Europe/Berlin;", "Europe/Berlinx;", StringComparison.Ordinal);
        var result = TimeZoneRegistry.SelfTest(tampered);
        Assert.False(result.Passed);
        Assert.Equal("anchor", result.FailedCheck);
    }

    [Fact]
    public void T5_SwappedAnchorMappingFails()
    {
        string tampered = ResourceText.Replace("Europe/Berlin;W. Europe Standard Time", "Europe/Berlin;Eastern Standard Time", StringComparison.Ordinal);
        Assert.Equal("anchor", TimeZoneRegistry.SelfTest(tampered).FailedCheck);
    }

    [Theory]
    [InlineData("Europe/Evil'x;W. Europe Standard Time\n", "pattern")]
    [InlineData("Europe/Evil;W. Europe 'Standard' Time\n", "pattern")]
    [InlineData("Europe/Evil;W. Europe Standard Time\u0000\n", "pattern")]
    [InlineData("Europe/Berlin;W. Europe Standard Time\n", "duplicate")]
    [InlineData("Europe/NoSeparator\n", "format")]
    [InlineData("a;b;c\n", "format")]
    public void T5_IllegalLinesFailWithTheirOwnCheckId(string extraLine, string expected)
    {
        var result = TimeZoneRegistry.SelfTest(ResourceText + extraLine);
        Assert.False(result.Passed);
        Assert.Equal(expected, result.FailedCheck);
    }

    [Fact]
    public void T5_TooFewEntriesFail()
    {
        string small = string.Join('\n', ResourceText.Split('\n').Take(12)) + "\n";
        Assert.Equal("entry-count", TimeZoneRegistry.SelfTest(small).FailedCheck);
    }

    [Fact]
    public void T5_TheFailureResultCarriesNoData()
    {
        var result = TimeZoneRegistry.SelfTest(ResourceText + "Europe/Evil'x;W\n");
        Assert.DoesNotContain("Evil", result.ToString(), StringComparison.Ordinal);
    }
}
