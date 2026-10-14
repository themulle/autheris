namespace Autheris.Tests.Integration;

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Integration.FederatedVirtualFilterWebApiFixture;

/// <summary>
/// F-GOV-14 WI-13 (E2E-3): Tier 2 end to end for every endpoint capability, and the guarantee that the result never
/// depends on what the upstream does with the keys: an upstream that ignores all filter arguments still yields only
/// the authorized customers (101, 102, 105).
/// </summary>
[Collection(FederatedVirtualFilterCollection.Name)]
public sealed class FederatedVirtualFilterPushdownTests(FederatedVirtualFilterWebApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Authorized = ["101", "102", "105"];

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Query(string table) =>
        $"SELECT c.id, c.name FROM crm_api.public.{table} c JOIN erp.main.orders o ON o.customer_id = c.id";

    [Theory]
    [InlineData(Tables.QueryParameterList)]
    [InlineData(Tables.JsonBody)]
    [InlineData(Tables.Path)]
    public async Task E2E3_ThreeAuthorizedKeys_RequestInDeclaredFormat(string table)
    {
        await fixture.UseBindingsAsync(Binding(table));

        var ids = await fixture.ColumnAsync(Query(table));

        ids.ShouldBe(Authorized);
        var requests = fixture.Crm.Requests;
        switch (table)
        {
            case Tables.QueryParameterList:
                var single = requests.ShouldHaveSingleItem();
                single.Method.ShouldBe("GET");
                single.Query["ids"].ShouldBe("101,102,105");
                break;
            case Tables.JsonBody:
                var post = requests.ShouldHaveSingleItem();
                post.Method.ShouldBe("POST");
                JsonSerializer.Deserialize<string[]>(post.Body!).ShouldBe(["101", "102", "105"]);
                break;
            default:
                requests.Count.ShouldBe(3);
                requests.ShouldAllBe(r => r.Method == "GET");
                requests.Select(r => r.Path).OrderBy(p => p).ShouldBe(["/api/v1/customers/101", "/api/v1/customers/102", "/api/v1/customers/105"]);
                break;
        }
    }

    [Theory]
    [InlineData(Tables.QueryParameterList)]
    [InlineData(Tables.JsonBody)]
    [InlineData(Tables.Path)]
    [InlineData(Tables.None)]
    public async Task E2E3_UpstreamIgnoresParameters_OnlyAuthorizedRows(string table)
    {
        await fixture.UseBindingsAsync(Binding(table));
        fixture.Crm.Mode = CrmApiMode.IgnoreFilters;

        var ids = await fixture.ColumnAsync(Query(table));

        // CR-01: the rows never depend on what the upstream does with the keys, a path lookup included (one row per key).
        ids.ShouldBe(Authorized);
        fixture.Crm.Requests.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(PushdownParameterFormat.RepeatedParam)]
    [InlineData(PushdownParameterFormat.ODataIn)]
    public async Task E2E3_RepeatedParamOrODataBinding_AdaptiveFallsBackToFullFetch_OnlyAuthorizedRows(PushdownParameterFormat format)
    {
        // The write path rejects these formats; the planner must still fail safe on a profile that is already stored.
        await fixture.UseStoredBindingsAsync(Binding(Tables.ODataFormat, format: format));

        var ids = await fixture.ColumnAsync(Query(Tables.ODataFormat));

        ids.ShouldBe(Authorized);
        var request = fixture.Crm.Requests.ShouldHaveSingleItem();
        request.Query.ShouldNotContainKey("ids");   // Tier 3: nothing was pushed
    }

    [Fact]
    public async Task E2E3_PagedEndpoint_NoPushdown_OnlyAuthorizedRows()
    {
        await fixture.UseBindingsAsync(Binding(Tables.Paged));

        var ids = await fixture.ColumnAsync(Query(Tables.Paged));

        ids.ShouldBe(Authorized);
        fixture.Crm.Requests.ShouldAllBe(r => !r.Query.ContainsKey("ids"));
        fixture.Crm.Requests.Count.ShouldBe(3);   // 10 customers, page size 4: pages 1..3
    }

    [Fact]
    public async Task E2E3_PointLookupHit_PushesOnlyTheQueriedAuthorizedKey_InTheDeclaredFormat()
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList));

        var ids = await fixture.ColumnAsync(Query(Tables.QueryParameterList) + " WHERE c.id IN (102, 103)");

        ids.ShouldBe(["102"]);
        fixture.Crm.Requests.ShouldHaveSingleItem().Query["ids"].ShouldBe("102");
    }
}
