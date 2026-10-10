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
/// F-GOV-14 WI-15 (E2E-5, E2E-8, D-12): every failure mode denies with HTTP 403, a stable reason code in a
/// <c>WEBSQL_QUERY_DENIED</c> audit record and without any upstream request; the strategy matrix holds end to end.
/// </summary>
[Collection(FederatedVirtualFilterCollection.Name)]
public sealed class FederatedVirtualFilterFailClosedTests(FederatedVirtualFilterWebApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Authorized = ["101", "102", "105"];

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Query(string table, string where = "") =>
        $"SELECT c.id, c.name FROM crm_api.public.{table} c JOIN erp.main.orders o ON o.customer_id = c.id {where}";

    /// <summary>Runs the query, expects HTTP 403 and returns the <c>reasonCode</c> of the denial audit record.</summary>
    private async Task<(string ReasonCode, string AuditDetails)> ExpectDeniedAsync(string sql)
    {
        var started = DateTimeOffset.UtcNow;

        var (status, body) = await fixture.SqlAsync(sql);

        using (body)
        {
            status.ShouldBe(HttpStatusCode.Forbidden, body.RootElement.ToString());
        }

        fixture.Crm.Requests.ShouldBeEmpty();
        var denied = (await fixture.AuditSinceAsync(started)).Single(e => e.EventType == AuditEventTypes.WebSqlQueryDenied);
        denied.Decision.ShouldBe("DENY");
        using var details = JsonDocument.Parse(denied.DetailsJson);
        return (details.RootElement.GetProperty("reasonCode").GetString()!, denied.DetailsJson);
    }

    [Fact]
    public async Task E2E5_KeySourceUnreachable_Forbidden_ZeroUpstreamRequests_DeniedAudit()
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList, filter: "vf_clients_down"));

        var (code, details) = await ExpectDeniedAsync(Query(Tables.QueryParameterList));

        code.ShouldBe("VIRTUAL_FILTER_SOURCE_UNAVAILABLE");
        details.ShouldNotContain("missing-dir");     // the inner exception never reaches the audit store
        details.ShouldNotContain("unable to open");
    }

    [Fact]
    public async Task E2E8_ShortCircuitOnlyScan_ForbiddenScanNotPermitted()
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList, strategy: VirtualFilterExecutionStrategy.ShortCircuitOnly));

        var (code, _) = await ExpectDeniedAsync(Query(Tables.QueryParameterList));

        code.ShouldBe("VF_SCAN_NOT_PERMITTED");
    }

    [Fact]
    public async Task E2E8_ShortCircuitOnlyPointLookup_PushesTheKey()
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList, strategy: VirtualFilterExecutionStrategy.ShortCircuitOnly));

        (await fixture.ColumnAsync(Query(Tables.QueryParameterList, "WHERE c.id = 102"))).ShouldBe(["102"]);
        fixture.Crm.Requests.ShouldHaveSingleItem().Query["ids"].ShouldBe("102");
    }

    [Fact]
    public async Task E2E8_PushdownOnlyOverBudget_ForbiddenLimitExceeded()
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList, strategy: VirtualFilterExecutionStrategy.PushdownOnly, maxKeys: 2));

        var (code, _) = await ExpectDeniedAsync(Query(Tables.QueryParameterList));   // three authorized keys

        code.ShouldBe("VF_PUSHDOWN_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task E2E8_PushdownOnlyWithoutCapability_ForbiddenPushdownUnavailable()
    {
        await fixture.UseBindingsAsync(Binding(Tables.None, strategy: VirtualFilterExecutionStrategy.PushdownOnly));

        var (code, _) = await ExpectDeniedAsync(Query(Tables.None));

        code.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
    }

    [Fact]
    public async Task E2E8_InvalidStoredRegex_Forbidden()
    {
        // The profile is written to the repository directly: the write-time validation would have refused the pattern.
        await fixture.UseStoredBindingsAsync(Binding(Tables.QueryParameterList, pattern: "regex:^(crm_api"));

        var (code, _) = await ExpectDeniedAsync(Query(Tables.QueryParameterList));

        code.ShouldBe("VF_PATTERN_EVALUATION_FAILED");
    }

    [Fact]
    public async Task E2E8_DuckDbHashJoinStrategy_NeverSendsPushdown()
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList, strategy: VirtualFilterExecutionStrategy.DuckDbHashJoin));

        (await fixture.ColumnAsync(Query(Tables.QueryParameterList))).ShouldBe(Authorized);
        fixture.Crm.Requests.ShouldHaveSingleItem().Query.ShouldNotContainKey("ids");
    }

    [Fact]
    public async Task E2E8_UncoveredWebApiTable_IsDenied_NotServedUnfiltered()
    {
        // The profile covers customers_qpl only; crm_api.public is in its scope with Uncovered = Deny.
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList));

        var (status, body) = await fixture.SqlAsync(Query(Tables.None));

        using (body)
        {
            status.ShouldBe(HttpStatusCode.Forbidden);
        }

        fixture.Crm.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task D12_ODataOnProtectedHttpTable_StillForbidden()
    {
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList));

        using var response = await fixture.Client().GetAsync($"/odata/v4/crm_api/public/{Tables.QueryParameterList}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task D12_RestOnProtectedHttpTable_NeverReturnsRows()
    {
        // The REST dataset channel is not part of the federation (D-12). On a web-api table it does not succeed at all:
        // it builds LIMIT .. OFFSET .. SQL that the cross-source parser rejects (pre-existing, independent of Virtual
        // Filters), so the call ends in an error status. What matters here: no rows, whatever the status.
        await fixture.UseBindingsAsync(Binding(Tables.QueryParameterList));

        using var response = await fixture.Client().GetAsync($"/api/v1/data/crm_api/public/{Tables.QueryParameterList}");

        response.IsSuccessStatusCode.ShouldBeFalse();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("Customer ");
    }
}
