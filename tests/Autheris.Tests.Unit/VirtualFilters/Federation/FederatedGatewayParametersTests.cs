namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>Issue #280 (SEC-280-7): the federated path rejects gateway-planned parameters and keeps the client-name guard.</summary>
public sealed class FederatedGatewayParametersTests
{
    private static readonly TableMetadata ErpOrders = SqlTable("erp", "public", "orders", "id", "customer_id");

    private const string Sql = "SELECT id FROM erp.public.orders WHERE id = @__gql_tf_0";

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue280_Federated_GatewayParameters_FailClosed()
    {
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "999"))]);
        var pipeline = Build([(ErpOrders, erp)], null);
        var request = new GovernedSqlQueryRequest(Sql, Parameters: null, Transport: "WebSql", PlannedTableRead: true)
        {
            GatewayParameters = new Dictionary<string, object?> { ["__gql_tf_0"] = "SENTINEL-280" },
        };

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(request, User(), Tenant));

        ex.Message.ShouldNotContain("SENTINEL-280");
        ex.Message.ShouldNotContain("__gql_tf_0");
        erp.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue280_Federated_ClientReservedName_StillRejected()
    {
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "999"))]);
        var pipeline = Build([(ErpOrders, erp)], null);
        var request = new GovernedSqlQueryRequest(
            Sql,
            Parameters: new Dictionary<string, object?> { ["__gql_tf_0"] = 1 },
            Transport: "WebSql",
            PlannedTableRead: true);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            pipeline.Service.ExecuteQueryBufferedAsync(request, User(), Tenant));

        ex.Message.ShouldContain("reserved gateway parameter name");
        erp.UpstreamCalls.ShouldBe(0);
    }
}
