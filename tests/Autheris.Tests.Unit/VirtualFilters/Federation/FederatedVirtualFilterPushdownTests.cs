namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-8 (AC-8.5, ST-25): through the real pipeline the authorized keys reach the connector as the batch
/// argument, a connector that ignores them still yields only authorized rows, and a pushdown argument can never
/// override an argument the gateway already set.
/// </summary>
public sealed class FederatedVirtualFilterPushdownTests
{
    private static readonly TableMetadata Erp = SqlTable("erp", "public", "orders", "id", "customer_id");

    private const string Join = "SELECT c.id, c.name FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id";

    private static TableMetadata Crm(string param = "ids")
    {
        var table = HttpTable("crm_api", "public", "customers", "id", "name");
        return WithEndpoint(table, new HttpEndpointDescriptor
            {
                BaseUrl = "https://api.example.internal",
                PathTemplate = "/customers",
                BatchType = HttpBatchType.QueryParameterList,
                BatchParamName = param,
                PrimaryKeyField = "id",
                CompleteResponse = true
            });
    }

    private static (StubTableConnector Crm, Pipeline Pipeline) Setup(string param = "ids")
    {
        var crmTable = Crm(param);
        var crm = new StubTableConnector("crm_api",
            [Row(("id", "1"), ("name", "A")), Row(("id", "2"), ("name", "B")), Row(("id", "3"), ("name", "C")), Row(("id", "4"), ("name", "D"))]);
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "1")), Row(("id", "11"), ("customer_id", "2")), Row(("id", "12"), ("customer_id", "3"))]);
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"], ["3"]);
        var pipeline = Build(
            [(crmTable, crm), (Erp, erp)],
            Consents(Allow(crmTable.Identifier), Allow(Erp.Identifier)),
            AliceSnapshot("crm_api.*.*", "crm_api.public.customers"),
            keys);
        return (crm, pipeline);
    }

    [Fact]
    public async Task Scan_ConnectorReceivesTheAuthorizedKeysAsTheBatchArgument_AndAnUpstreamThatIgnoresThemStillYieldsOnlyAuthorizedRows()
    {
        var (crm, pipeline) = Setup();

        var rows = await pipeline.RunAsync(Join);

        rows.Select(r => (string)r["id"]!).OrderBy(i => i, StringComparer.Ordinal).ShouldBe(["1", "3"]);   // the stub returned 1..4
        crm.UpstreamCalls.ShouldBe(1);
        ((IEnumerable<object>)crm.LastSession!.Arguments["ids"]!).Cast<string>().ShouldBe(["1", "3"]);
    }

    [Fact]
    public async Task PointLookupHit_PushesOnlyTheAuthorizedQueriedKey()
    {
        // ST-25: 2 is queried but not authorized and is never sent upstream.
        var (crm, pipeline) = Setup();

        var rows = await pipeline.RunAsync(Join + " WHERE c.id IN ('1', '2')");

        rows.Select(r => (string)r["id"]!).ShouldBe(["1"]);
        ((IEnumerable<object>)crm.LastSession!.Arguments["ids"]!).Cast<string>().ShouldBe(["1"]);
    }

    [Fact]
    public async Task PushdownArgumentThatCollidesWithAGatewayArgument_IsDenied_ConnectorNeverCalled()
    {
        // AC-8.5: 'limit' is set by the gateway; a binding cannot overwrite it through the batch parameter name.
        var (crm, pipeline) = Setup(param: "limit");

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join));

        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_ARGUMENT_CONFLICT");
        crm.UpstreamCalls.ShouldBe(0);
    }
}
