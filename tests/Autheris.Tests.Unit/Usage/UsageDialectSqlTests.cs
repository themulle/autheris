namespace Autheris.Tests.Unit.Usage;

using System;
using Autheris.Infrastructure.Persistence.Dialects;
using Shouldly;
using Xunit;

public sealed class UsageDialectSqlTests
{
    [Fact]
    public void UpsertAccumulate_PostgreSql_UsesOnConflictSum()
    {
        var pg = new PostgreSqlGovernanceDialect("Host=localhost");
        string sql = pg.UpsertAccumulate(
            GovernanceTable.UsageDaily,
            ["day", "series_hash"],
            ["tenant_id"],
            ["requests", "queries"]);

        sql.ShouldStartWith("INSERT INTO \"usage_daily\" (\"day\", \"series_hash\", \"tenant_id\", \"requests\", \"queries\")");
        sql.ShouldContain("VALUES (@day, @series_hash, @tenant_id, @requests, @queries)");
        sql.ShouldContain("ON CONFLICT (\"day\", \"series_hash\") DO UPDATE SET \"requests\" = \"usage_daily\".\"requests\" + EXCLUDED.\"requests\", \"queries\" = \"usage_daily\".\"queries\" + EXCLUDED.\"queries\"");
    }

    [Fact]
    public void UpsertAccumulate_SqlServer_UsesMergeWithHoldlockSum()
    {
        var ss = new SqlServerGovernanceDialect("Server=localhost");
        string sql = ss.UpsertAccumulate(
            GovernanceTable.UsageDaily,
            ["day", "series_hash"],
            ["tenant_id"],
            ["requests"]);

        sql.ShouldStartWith("MERGE INTO [USAGE_DAILY] WITH (HOLDLOCK) AS target USING (VALUES (@day, @series_hash, @tenant_id, @requests)) AS source ([day], [series_hash], [tenant_id], [requests])");
        sql.ShouldContain("ON (target.[day] = source.[day] AND target.[series_hash] = source.[series_hash])");
        sql.ShouldContain("WHEN MATCHED THEN UPDATE SET target.[requests] = target.[requests] + source.[requests]");
        sql.ShouldContain("WHEN NOT MATCHED THEN INSERT ([day], [series_hash], [tenant_id], [requests]) VALUES (source.[day], source.[series_hash], source.[tenant_id], source.[requests]);");
    }

    [Fact]
    public void UpsertAccumulate_EmptyCounterColumns_EmitsInsertIfAbsent()
    {
        var pg = new PostgreSqlGovernanceDialect("Host=localhost");
        var ss = new SqlServerGovernanceDialect("Server=localhost");

        string pgSql = pg.UpsertAccumulate(GovernanceTable.UsageGroupSet, ["tenant_id", "group_set_id", "group_sid"], ["first_seen"], []);
        string ssSql = ss.UpsertAccumulate(GovernanceTable.UsageGroupSet, ["tenant_id", "group_set_id", "group_sid"], ["first_seen"], []);

        pgSql.ShouldEndWith("ON CONFLICT (\"tenant_id\", \"group_set_id\", \"group_sid\") DO NOTHING");
        ssSql.ShouldNotContain("WHEN MATCHED");
        ssSql.ShouldContain("WHEN NOT MATCHED THEN INSERT");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("day; DROP TABLE USAGE_DAILY--")]
    [InlineData("bad col")]
    [InlineData("bad\"col")]
    [InlineData("")]
    public void UpsertAccumulate_RejectsInvalidIdentifiers(string bad)
    {
        var pg = new PostgreSqlGovernanceDialect("Host=localhost");
        var ss = new SqlServerGovernanceDialect("Server=localhost");

        Should.Throw<ArgumentException>(() => pg.UpsertAccumulate(GovernanceTable.UsageDaily, [bad], [], ["requests"]));
        Should.Throw<ArgumentException>(() => pg.UpsertAccumulate(GovernanceTable.UsageDaily, ["day"], [bad], ["requests"]));
        Should.Throw<ArgumentException>(() => pg.UpsertAccumulate(GovernanceTable.UsageDaily, ["day"], [], [bad]));
        Should.Throw<ArgumentException>(() => ss.UpsertAccumulate(GovernanceTable.UsageDaily, [bad], [], ["requests"]));
    }
}
