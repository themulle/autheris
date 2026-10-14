namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Integration.FederatedVirtualFilterWebApiFixture;

/// <summary>
/// F-GOV-14 CR-01 (v1 path-lookup semantics): the result and the HTTP status of a query on a path-lookup endpoint are
/// the same as for every other tier. (a) A read of a path template that cannot be filled is denied (403) without an
/// upstream call, (b) one request per key with the answers restricted to the requested key and de-duplicated, so
/// COUNT and SUM are right even if the upstream ignores the key, (c) an upstream 404 for one key means "not present".
/// The mock upstream answers a request with an unexpanded <c>{id}</c> with 404, so such a request can never succeed.
/// </summary>
[Collection(FederatedVirtualFilterCollection.Name)]
public sealed class FederatedVirtualFilterPathLookupTests(FederatedVirtualFilterWebApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Query(string table, string where = "") =>
        $"SELECT c.id, c.name FROM crm_api.public.{table} c JOIN erp.main.orders o ON o.customer_id = c.id {where}";

    private static string Aggregate(string table) =>
        $"SELECT COUNT(*) AS n, SUM(c.id) AS total FROM crm_api.public.{table} c JOIN erp.main.orders o ON o.customer_id = c.id";

    private async Task<(string Count, string Sum)> AggregateAsync(string table)
    {
        var (status, body) = await fixture.SqlAsync(Aggregate(table));
        using (body)
        {
            status.ShouldBe(HttpStatusCode.OK, body.RootElement.ToString());
            var row = RowsOf(body.RootElement).ShouldHaveSingleItem();
            return (row["n"]!.ToString()!, row["total"]!.ToString()!);
        }
    }

    [Fact]
    public async Task UpstreamIgnoresTheKey_CountAndSum_EqualTheTierThreeResult()
    {
        fixture.Crm.Mode = CrmApiMode.IgnoreFilters;
        await fixture.UseBindingsAsync(Binding(Tables.None));
        var tierThree = await AggregateAsync(Tables.None);
        fixture.Crm.Reset();
        fixture.Crm.Mode = CrmApiMode.IgnoreFilters;
        await fixture.UseBindingsAsync(Binding(Tables.Path));

        var pathLookup = await AggregateAsync(Tables.Path);

        tierThree.ShouldBe(("3", "308"));
        pathLookup.ShouldBe(tierThree);
        fixture.Crm.Requests.Count.ShouldBe(3);
        fixture.Crm.Requests.ShouldAllBe(r => !r.Path.Contains('{'));
    }

    [Fact]
    public async Task UpstreamIgnoresTheKey_RowsAreExactlyTheAuthorizedOnes_OncePerKey()
    {
        fixture.Crm.Mode = CrmApiMode.IgnoreFilters;
        await fixture.UseBindingsAsync(Binding(Tables.Path));

        var ids = await fixture.ColumnAsync(Query(Tables.Path));

        ids.ShouldBe(["101", "102", "105"]);
    }

    [Fact]
    public async Task AnAuthorizedKeyThatIsGoneUpstream_Answers404_ZeroRowsForThatKey_Status200_AndIsAudited()
    {
        await fixture.UseBindingsAsync(Binding(Tables.Path));
        fixture.Crm.MissingIds[102] = true;
        var started = DateTimeOffset.UtcNow;

        var ids = await fixture.ColumnAsync(Query(Tables.Path));

        ids.ShouldBe(["101", "105"]);
        fixture.Crm.Requests.Count.ShouldBe(3);
        var read = (await fixture.AuditSinceAsync(started))
            .Single(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.DetailsJson.Contains("upstreamRequests"));
        using var details = JsonDocument.Parse(read.DetailsJson);
        details.RootElement.GetProperty("upstreamRequests").GetInt32().ShouldBe(3);
        details.RootElement.GetProperty("upstreamKeysNotFound").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task APointLookupOfAKeyThatIsGoneUpstream_IsZeroRows_Status200_LikeATierOneMiss()
    {
        await fixture.UseBindingsAsync(Binding(Tables.Path));
        fixture.Crm.MissingIds[102] = true;

        var (status, body) = await fixture.SqlAsync(Query(Tables.Path, "WHERE c.id = 102"));

        using (body)
        {
            status.ShouldBe(HttpStatusCode.OK);
            RowsOf(body.RootElement).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task AnyOtherUpstreamFailure_FailsTheQuery_NotTreatedAsAMissingKey()
    {
        await fixture.UseBindingsAsync(Binding(Tables.Path));
        fixture.Crm.FailingIds[102] = true;

        var (status, body) = await fixture.SqlAsync(Query(Tables.Path));

        using (body)
        {
            ((int)status).ShouldBeGreaterThanOrEqualTo(500);
        }
    }

    [Fact]
    public async Task AdaptiveWithMoreKeysThanThePathLookupBudget_IsDenied_NoRequestIsSent()
    {
        // 12 authorized keys, MaxPathLookupRequests is 10: never a full scan of /customers/{id}.
        await fixture.UseBindingsAsync(Binding(Tables.Path, filter: "vf_clients_many"));

        await ExpectDeniedAsync(Query(Tables.Path), "VF_PATH_TEMPLATE_UNFILLABLE");
    }

    [Fact]
    public async Task DuckDbHashJoinStrategy_WantsAFullRead_IsDenied_NoRequestIsSent()
    {
        await fixture.UseBindingsAsync(Binding(Tables.Path, strategy: VirtualFilterExecutionStrategy.DuckDbHashJoin));

        await ExpectDeniedAsync(Query(Tables.Path), "VF_PATH_TEMPLATE_UNFILLABLE");
    }

    [Fact]
    public async Task Mock_AnUnexpandedPlaceholderPath_Is404()
    {
        // Guard of the fixture itself (CR-01 d): such a request must never "succeed" with rows.
        fixture.Crm.Mode = CrmApiMode.IgnoreFilters;
        using var client = new System.Net.Http.HttpClient(fixture.Crm);

        using var response = await client.GetAsync("https://mock.crm.internal/api/v1/customers/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task ExpectDeniedAsync(string sql, string reasonCode)
    {
        var started = DateTimeOffset.UtcNow;

        var (status, body) = await fixture.SqlAsync(sql);

        using (body)
        {
            status.ShouldBe(HttpStatusCode.Forbidden, body.RootElement.ToString());
        }

        fixture.Crm.Requests.ShouldBeEmpty();
        var denied = (await fixture.AuditSinceAsync(started)).Single(e => e.EventType == AuditEventTypes.WebSqlQueryDenied);
        using var details = JsonDocument.Parse(denied.DetailsJson);
        details.RootElement.GetProperty("reasonCode").GetString().ShouldBe(reasonCode);
    }
}
