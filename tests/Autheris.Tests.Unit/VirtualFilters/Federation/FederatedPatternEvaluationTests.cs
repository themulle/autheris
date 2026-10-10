namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-2 (ST-7, ST-8): a stored target pattern that cannot be evaluated denies the WebSQL cross-source query
/// with <c>VF_PATTERN_EVALUATION_FAILED</c> (HTTP 403, never 500/501), before any key is loaded or any upstream call.
/// </summary>
public sealed class FederatedPatternEvaluationTests
{
    private static readonly TableMetadata CrmCustomers = HttpTable("crm_api", "public", "customers", "id", "name");
    private static readonly TableMetadata ErpOrders = SqlTable("erp", "public", "orders", "id", "customer_id");

    [Theory]
    [InlineData("regex:^crm_api\\.(public")]   // invalid regex written directly to the repository
    [InlineData("regex:^(a)\\1$")]             // backreference (NotSupportedException in the linear engine)
    public async Task WebSql_StoredPatternFailsAtMatchTime_IsDeniedWithPatternEvaluationFailed(string pattern)
    {
        var crm = new StubTableConnector("crm_api", [Row(("id", "1"), ("name", "Alpha"))]);
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "1"))]);
        var pipeline = Build(
            [(CrmCustomers, crm), (ErpOrders, erp)],
            Consents(Allow(CrmCustomers.Identifier), Allow(ErpOrders.Identifier)),
            AliceSnapshot("crm_api.*.*", pattern));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(
            "SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id"));

        ex.ErrorCode.ShouldBe("VF_PATTERN_EVALUATION_FAILED");
        ex.ShouldNotBeOfType<GatewayForbiddenException>();   // the specific code, not a generic forbidden
        crm.UpstreamCalls.ShouldBe(0);
        erp.UpstreamCalls.ShouldBe(0);
        await pipeline.KeySetProvider.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default, default);
        await pipeline.Audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlQueryDenied &&
                                       e.DetailsJson != null &&
                                       e.DetailsJson.Contains("\"reasonCode\":\"VF_PATTERN_EVALUATION_FAILED\"")),
            Arg.Any<CancellationToken>());
    }
}
