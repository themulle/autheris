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
/// F-GOV-14 WI-12 (E2E-1, E2E-2, E2E-7): Tier 0 and Tier 1 end to end through the real WebSQL endpoint. The key source is
/// a SQLite file; the authorized customers are 101, 102 and 105 (103 and 104 are inactive, one key is NULL).
/// </summary>
[Collection(FederatedVirtualFilterCollection.Name)]
public sealed class FederatedVirtualFilterTierTests(FederatedVirtualFilterWebApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Query(string table, string where = "") =>
        $"SELECT c.id, c.name FROM crm_api.public.{table} c JOIN erp.main.orders o ON o.customer_id = c.id {where}";

    [Fact]
    public async Task E2E1_PointLookupNotAuthorized_ZeroRows_ZeroUpstreamRequests_CompleteAudit()
    {
        await fixture.UseBindingsAsync(Binding(Tables.Path));
        var started = DateTimeOffset.UtcNow;

        var (status, body) = await fixture.SqlAsync(Query(Tables.Path, "WHERE c.id = 103"));

        using (body)
        {
            status.ShouldBe(HttpStatusCode.OK);
            RowsOf(body.RootElement).ShouldBeEmpty();
            ColumnNames(body.RootElement).ShouldBe(["id", "name"]);
        }

        fixture.Crm.Requests.ShouldBeEmpty();
        var audit = await fixture.AuditSinceAsync(started);
        var start = audit.Single(e => e.EventType == AuditEventTypes.WebSqlCrossSourceQuery);
        using var details = JsonDocument.Parse(start.DetailsJson);
        var vf = details.RootElement.GetProperty("virtualFilters").EnumerateArray().Single();
        vf.GetProperty("tier").GetString().ShouldBe("PointLookup");
        vf.GetProperty("shortCircuited").GetBoolean().ShouldBeTrue();
        audit.ShouldContain(e => e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead && e.DetailsJson.Contains("virtualFilterKeySource"));
        audit.Select(e => e.TraceId).Distinct().Count().ShouldBe(1);
        audit.ShouldAllBe(e => !e.DetailsJson.Contains("103") || e.EventType == AuditEventTypes.WebSqlCrossSourceQuery);
    }

    [Fact]
    public async Task E2E2_PointLookupAuthorized_OneRow_OneRequestCarryingOnlyThatKey()
    {
        await fixture.UseBindingsAsync(Binding(Tables.Path));

        var ids = await fixture.ColumnAsync(Query(Tables.Path, "WHERE c.id = 102"));

        ids.ShouldBe(["102"]);
        var request = fixture.Crm.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("GET");
        request.Path.ShouldBe("/api/v1/customers/102");
        request.Query.ShouldBeEmpty();
    }

    [Fact]
    public async Task E2E2_InListWithAnUnauthorizedKey_SendsOnlyTheAuthorizedKey()
    {
        // ST-25: 103 is queried but not authorized and never reaches the upstream.
        await fixture.UseBindingsAsync(Binding(Tables.Path));

        var ids = await fixture.ColumnAsync(Query(Tables.Path, "WHERE c.id IN (102, 103)"));

        ids.ShouldBe(["102"]);
        fixture.Crm.Requests.Select(r => r.Path).ShouldBe(["/api/v1/customers/102"]);
    }

    [Theory]
    [InlineData(Tables.QueryParameterList)]
    [InlineData(Tables.None)]   // no pushdown capability: only Tier 0 keeps the upstream untouched (mutation M8)
    [InlineData(Tables.Path)]
    public async Task E2E7_EmptyKeySet_ZeroRows_ZeroUpstreamRequests(string table)
    {
        // The filter selects is_active = 2: no authorization row matches, so the key set is empty (Tier 0).
        await fixture.UseBindingsAsync(Binding(table, filter: "vf_clients_none"));

        var (status, body) = await fixture.SqlAsync(Query(table));

        using (body)
        {
            status.ShouldBe(HttpStatusCode.OK);
            RowsOf(body.RootElement).ShouldBeEmpty();
        }

        fixture.Crm.Requests.ShouldBeEmpty();
    }
}
