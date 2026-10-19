namespace Autheris.Tests.Unit.Telemetry;

using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Shouldly;
using Xunit;

public sealed class SqlTextScrubberTests
{
    [Fact]
    public void TryScrub_ReplacesLiteralsWithPlaceholders()
    {
        var sql = "SELECT * FROM Users WHERE Email = 'alice@example.com' AND Age > 25 AND Balance = 100.50";
        var success = SqlTextScrubber.TryScrub(sql, DatabaseDialect.PostgreSql, out var scrubbed, out var hashHex);

        success.ShouldBeTrue();
        scrubbed.ShouldNotBeNull();
        hashHex.ShouldNotBeNull();
        scrubbed.ShouldNotContain("alice@example.com");
        scrubbed.ShouldNotContain("25");
        scrubbed.ShouldNotContain("100.50");
        scrubbed.ShouldContain("Email = ?");
        scrubbed.ShouldContain("Age > ?");
        scrubbed.ShouldContain("Balance = ?");
        hashHex.Length.ShouldBe(16);
    }

    [Fact]
    public void TryScrub_PreservesIdentifiersAndParameterMarkers()
    {
        var sql = "SELECT \"first_name\", [last_name] FROM `customers` WHERE id = @p0 AND tenant = :t1 AND code = $1";
        var success = SqlTextScrubber.TryScrub(sql, DatabaseDialect.PostgreSql, out var scrubbed, out var hashHex);

        success.ShouldBeTrue();
        scrubbed.ShouldNotBeNull();
        hashHex.ShouldNotBeNull();
        scrubbed.ShouldContain("\"first_name\"");
        scrubbed.ShouldContain("[last_name]");
        scrubbed.ShouldContain("`customers`");
        scrubbed.ShouldContain("@p0");
        scrubbed.ShouldContain(":t1");
        scrubbed.ShouldContain("$1");
        hashHex.Length.ShouldBe(16);
    }

    [Fact]
    public void TryScrub_StripsComments()
    {
        var sql = "SELECT /* inline comment */ 1 -- line comment\nFROM tbl";
        var success = SqlTextScrubber.TryScrub(sql, DatabaseDialect.PostgreSql, out var scrubbed, out var hashHex);

        success.ShouldBeTrue();
        scrubbed.ShouldNotBeNull();
        hashHex.ShouldNotBeNull();
        scrubbed.ShouldNotContain("inline comment");
        scrubbed.ShouldNotContain("line comment");
        hashHex.Length.ShouldBe(16);
    }

    [Fact]
    public void TryScrub_FailsClosed_OnUnterminatedString()
    {
        var sql = "SELECT * FROM Users WHERE Name = 'unterminated";
        var success = SqlTextScrubber.TryScrub(sql, DatabaseDialect.PostgreSql, out var scrubbed, out var hashHex);

        success.ShouldBeFalse();
        scrubbed.ShouldBeNull();
        hashHex.ShouldBeNull();
    }

    [Fact]
    public void TryScrub_FailsClosed_OnOversizedInput()
    {
        var sql = new string('A', 70000);
        var success = SqlTextScrubber.TryScrub(sql, DatabaseDialect.PostgreSql, out var scrubbed, out var hashHex);

        success.ShouldBeFalse();
        scrubbed.ShouldBeNull();
        hashHex.ShouldBeNull();
    }

    [Fact]
    public void TryScrub_ComputesStableHash_OverScrubbedText()
    {
        var sql1 = "SELECT * FROM Users WHERE Email = 'alice@example.com'";
        var sql2 = "SELECT * FROM Users WHERE Email = 'bob@example.com'";

        var s1 = SqlTextScrubber.TryScrub(sql1, DatabaseDialect.PostgreSql, out var scrubbed1, out var hash1);
        var s2 = SqlTextScrubber.TryScrub(sql2, DatabaseDialect.PostgreSql, out var scrubbed2, out var hash2);

        s1.ShouldBeTrue();
        s2.ShouldBeTrue();
        scrubbed1.ShouldBe(scrubbed2);
        hash1.ShouldBe(hash2);
    }
}
