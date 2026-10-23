using System.Net.Http.Json;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;
using Shouldly;

namespace Autheris.Tests.Parity.Smoke;

/// <summary>WP1 smoke: the host, seed and the WebSql/GraphQl channels agree on the hand-computed seed values.</summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed class ParityHostSmokeTests(ParityHostFixture host) : ParityTestBase
{
    private static readonly ParityQuery CountAll = new(ParitySeed.SalesTable, [new ParityAggregate(ParityFn.Count, null, "n")]);

    private static readonly ParityQuery SumByRegion = new(
        ParitySeed.SalesTable,
        [new ParityAggregate(ParityFn.Sum, "amount", "total")],
        ["region"]);

    private IParityChannel[] Channels => [new WebSqlChannel(host), new GraphQlChannel(host)];

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public async Task Smoke_CountAll(string channelName)
    {
        var channel = Channels.Single(c => c.Name == channelName);
        await ParityCell.RunAsync(new ParityCellInfo("smoke", channelName, "count", "all"), async () =>
        {
            var r = await channel.ExecuteAsync(CountAll, CancellationToken.None);
            r.Status.ShouldBe(200, r.RawBody);
            r.Rows.Count.ShouldBe(1, r.RawBody);
            Convert.ToInt32(r.Rows[0]["n"]).ShouldBe(ParityExpected.TotalRows);
        });
    }

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public async Task Smoke_SumByRegion(string channelName)
    {
        var channel = Channels.Single(c => c.Name == channelName);
        await ParityCell.RunAsync(new ParityCellInfo("smoke", channelName, "sum_group", "region"), async () =>
        {
            var r = await channel.ExecuteAsync(SumByRegion, CancellationToken.None);
            r.Status.ShouldBe(200, r.RawBody);
            var sums = r.Rows.Where(x => x["region"] is string && x["total"] is not null)
                .ToDictionary(x => (string)x["region"]!, x => Convert.ToDecimal(x["total"]));
            sums.ShouldBe(ParityExpected.SumAmountByRegion, ignoreOrder: true, r.RawBody);
        });
    }

    [ParityFact]
    public async Task Smoke_McpIsPending()
    {
        var r = await new McpChannel().ExecuteAsync(CountAll, CancellationToken.None);
        r.IsPending.ShouldBeTrue();
    }

    [ParityFact]
    public async Task Smoke_UnauthenticatedWebSqlIsRejected()
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Test-User-Sid");
        client.DefaultRequestHeaders.Remove("X-Test-Roles");
        client.DefaultRequestHeaders.Remove("X-Test-Tenant");
        var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT 1" });
        ((int)response.StatusCode).ShouldBe(401);
    }
}
