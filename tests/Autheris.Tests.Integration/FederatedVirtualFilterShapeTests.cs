namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static Autheris.Tests.Integration.FederatedVirtualFilterWebApiFixture;

/// <summary>
/// F-GOV-14 WI-14 (E2E-4, E2E-6, E2E-9): composite keys with mixed key types, every query shape over a protected table,
/// and a consent row filter on a web-api table (no Virtual Filter) that stays enforced in a cross-source query. The
/// upstream ignores all filter arguments where the shape matters, so only the gateway enforcement can produce the rows.
/// </summary>
[Collection(FederatedVirtualFilterCollection.Name)]
public sealed class FederatedVirtualFilterShapeTests(FederatedVirtualFilterWebApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Authorized = ["101", "102", "105"];
    private const string Qpl = "crm_api.public." + Tables.QueryParameterList;

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task E2E4_CompositeKey_DuplicateAndMixedTypeTuples_ExactRows_NoDuplicates_NoExtraColumns_CorrectCount()
    {
        // Key source: (101,T1) twice, (102,T1), (105,T2); 103/104 inactive; one NULL key. Upstream rows mix 101 and "101"
        // and carry wrong tenants for 102 (T2) and 105 (T1).
        await fixture.UseBindingsAsync(Binding(Tables.Mixed, filter: "vf_clients_tenant"));

        var (status, body) = await fixture.SqlAsync($"SELECT * FROM crm_api.public.{Tables.Mixed}");

        using (body)
        {
            status.ShouldBe(HttpStatusCode.OK, body.RootElement.ToString());
            ColumnNames(body.RootElement).ShouldBe(["id", "name", "tenant_code"]);
            var rows = RowsOf(body.RootElement);
            rows.Select(r => r["name"]!.ToString()).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["A", "B", "D", "G"]);
            rows.Select(r => r["tenant_code"]!.ToString() + ":" + r["id"]).OrderBy(n => n, StringComparer.Ordinal)
                .ShouldBe(["T1:101", "T1:101", "T1:102", "T2:105"]);
        }

        var (_, count) = await fixture.SqlAsync($"SELECT COUNT(*) AS n FROM crm_api.public.{Tables.Mixed}");
        using (count)
        {
            RowsOf(count.RootElement).Single()["n"].ShouldBe("4");
        }
    }

    public static IEnumerable<object[]> Shapes() =>
    [
        ["Union", $"SELECT id FROM {Qpl} UNION ALL SELECT id FROM erp.main.orders"],
        ["Cte", $"WITH c AS (SELECT id FROM {Qpl}) SELECT c.id FROM c JOIN erp.main.orders o ON o.customer_id = c.id"],
        ["Subquery", $"SELECT x.id FROM (SELECT id FROM {Qpl}) x JOIN erp.main.orders o ON o.customer_id = x.id"],
        ["SelfJoin", $"SELECT a.id FROM {Qpl} a JOIN {Qpl} b ON a.id = b.id JOIN erp.main.orders o ON o.customer_id = a.id"],
        ["JoinWithSqlTable", $"SELECT c.id FROM {Qpl} c JOIN erp.main.orders o ON o.customer_id = c.id"],
        ["InSubquery", $"SELECT o.id FROM erp.main.orders o WHERE o.customer_id IN (SELECT id FROM {Qpl})"],
        ["Exists", $"SELECT o.id FROM erp.main.orders o WHERE EXISTS (SELECT 1 FROM {Qpl} c WHERE c.id = o.customer_id)"],
        ["Aggregate", $"SELECT COUNT(*) AS id FROM {Qpl} c JOIN erp.main.orders o ON o.customer_id = c.id"]
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task E2E6_QueryShape_OnlyAuthorizedRows(string shape, string sql)
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList));
        fixture.Crm.Mode = CrmApiMode.IgnoreFilters;

        var ids = await fixture.ColumnAsync(sql);

        switch (shape)
        {
            case "Union":
                ids.Where(i => i.Length == 3).OrderBy(i => i, StringComparer.Ordinal).ShouldBe(Authorized);   // orders ids are 1..5
                ids.Count.ShouldBe(3 + 5);
                break;
            case "InSubquery":
            case "Exists":
                ids.ShouldBe(["1", "2", "4"]);   // orders of the authorized customers 101, 102, 105
                break;
            case "Aggregate":
                ids.ShouldBe(["3"]);
                break;
            case "SelfJoin":
            case "Cte":
            case "Subquery":
            case "JoinWithSqlTable":
                ids.ShouldBe(Authorized);
                break;
        }
    }

    [Fact]
    public async Task E2E9_ConsentRowFilterOnHttpTable_StillEnforcedInCrossSourceQuery()
    {
        // No profile and no Virtual Filter at all: the consent row filter tenant_code = 'T1' must still hold although
        // the upstream returns every customer (AC-7.4: a federation flag once switched all row filters off).
        fixture.Crm.Mode = CrmApiMode.IgnoreFilters;

        var ids = await fixture.ColumnAsync(
            $"SELECT c.id FROM crm_api.public.{Tables.Filtered} c JOIN erp.main.orders o ON o.customer_id = c.id");

        ids.ShouldBe(["101", "103", "105"]);   // the T1 customers (odd ids) that have an order
    }
}
