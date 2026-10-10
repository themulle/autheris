namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Threading.Tasks;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-6 (ST-22): without a registered federation planner, a Virtual Filter on an HTTP table denies every WebSQL
/// cross-source query shape (fail closed) before any key is loaded or any upstream call is made.
/// </summary>
public sealed class FederatedVirtualFilterInterimTests
{
    private static readonly TableMetadata CrmCustomers = HttpTable("crm_api", "public", "customers", "id", "name");
    private static readonly TableMetadata ErpOrders = SqlTable("erp", "public", "orders", "id", "customer_id");

    [Theory]
    [InlineData("SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 999")]
    [InlineData("SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id")]
    public async Task VirtualFilterOnHttpTable_WithoutPlanner_IsDeniedEnforcementUnavailable(string sql)
    {
        var crm = new StubTableConnector("crm_api", [Row(("id", "1"), ("name", "Alpha")), Row(("id", "999"), ("name", "Omega"))]);
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "999"))]);
        var pipeline = Build(
            [(CrmCustomers, crm), (ErpOrders, erp)],
            Consents(Allow(CrmCustomers.Identifier), Allow(ErpOrders.Identifier)),
            AliceSnapshot("crm_api.*.*", "crm_api.public.customers"),
            withPlanner: false);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(sql));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_UNAVAILABLE");
        crm.UpstreamCalls.ShouldBe(0);
        erp.UpstreamCalls.ShouldBe(0);
        pipeline.KeySetProvider.Calls.ShouldBeEmpty();
    }
}
