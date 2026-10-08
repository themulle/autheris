namespace Autheris.Tests.Unit.Sql;

using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>Row limits per transport (Gateway:RowLimits:*), falling back to the WebSQL limits.</summary>
public sealed class TransportRowLimitTests
{
    private static readonly WebSqlOptions WebSql = new() { DefaultMaxRows = 1000, MaxAllowedRows = 10000 };

    [Fact]
    public void For_WithoutChannelValues_UsesWebSqlLimits()
    {
        SqlRowLimit.For(WebSql, new ChannelRowLimitOptions()).ShouldBe(new SqlRowLimit(1000, 10000));
        SqlRowLimit.For(WebSql).ShouldBe(new SqlRowLimit(1000, 10000));
    }

    [Fact]
    public void For_ChannelValues_OverrideEachLimitSeparately()
    {
        SqlRowLimit.For(WebSql, new ChannelRowLimitOptions { MaxAllowedRows = 500000 }).ShouldBe(new SqlRowLimit(1000, 500000));
        SqlRowLimit.For(WebSql, new ChannelRowLimitOptions { DefaultMaxRows = 50 }).ShouldBe(new SqlRowLimit(50, 10000));
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData(60000L, 10000)]
    [InlineData(5L, 5)]
    public void Effective_BoundsExplicitLimitAndDefaultsWithout(long? explicitLimit, long expected)
    {
        new SqlRowLimit(1000, 10000).Effective(explicitLimit).ShouldBe(expected);
    }

    [Fact]
    public void Effective_DefaultAboveMaximum_IsBoundedAndZeroMaximumIsUnbounded()
    {
        new SqlRowLimit(50000, 10000).Effective(null).ShouldBe(10000);
        new SqlRowLimit(1000, 0).Effective(250000).ShouldBe(250000);
        new SqlRowLimit(0, 0).Effective(null).ShouldBe(1000);
    }

    [Theory]
    [InlineData("SELECT id FROM lwetem_prod.md.crane LIMIT 60000", false, "10000")]
    [InlineData("SELECT id FROM lwetem_prod.md.crane LIMIT 60000", true, "50000")]
    [InlineData("SELECT id FROM lwetem_prod.md.crane", true, "20")]
    public async Task GovernedExecution_UsesTheRowLimitOfTheRequest(string sql, bool withChannelLimit, string expectedLimit)
    {
        var (service, _, command) = WebSqlTwoPartNameDataSourceTests.CreateServiceWithCommand(WebSqlTwoPartNameDataSourceTests.CraneId);
        var request = new GovernedSqlQueryRequest(sql, RowLimit: withChannelLimit ? new SqlRowLimit(20, 50000) : null);

        await service.ExecuteQueryBufferedAsync(request, WebSqlTwoPartNameDataSourceTests.CreateUser(), new TenantId(WebSqlTwoPartNameDataSourceTests.Tenant));

        command.CommandText.ShouldContain("LIMIT " + expectedLimit);
    }
}
