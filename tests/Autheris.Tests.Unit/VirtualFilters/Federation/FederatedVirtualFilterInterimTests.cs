namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Threading.Tasks;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-5 interim state: the legacy short-circuit evaluator and key provider are removed. Until the federation
/// planner (WI-6) supplies a gateway enforcement, a Virtual Filter on an HTTP table denies every WebSQL cross-source
/// query shape (fail closed); a point lookup no longer returns a silent empty result.
/// </summary>
public sealed class FederatedVirtualFilterInterimTests
{
    private static readonly TableMetadata CrmCustomers = HttpTable("crm_api", "public", "customers", "id", "name");
    private static readonly TableMetadata ErpOrders = SqlTable("erp", "public", "orders", "id", "customer_id");

    [Theory]
    [InlineData("SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 999")]
    [InlineData("SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id")]
    public async Task VirtualFilterOnHttpTable_WithoutEnforcement_IsForbidden(string sql)
    {
        var crm = new StubTableConnector("crm_api", [Row(("id", "1"), ("name", "Alpha")), Row(("id", "999"), ("name", "Omega"))]);
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "999"))]);
        var pipeline = Build(
            [(CrmCustomers, crm), (ErpOrders, erp)],
            Consents(Allow(CrmCustomers.Identifier), Allow(ErpOrders.Identifier)),
            AliceSnapshot("crm_api.*.*", "crm_api.public.customers"));

        await Should.ThrowAsync<GatewayForbiddenException>(() => pipeline.RunAsync(sql));
    }
}
