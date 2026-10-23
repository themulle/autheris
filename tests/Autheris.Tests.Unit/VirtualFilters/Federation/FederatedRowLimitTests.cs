namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Threading.Tasks;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>SQLA-08: the cross-source path honours an explicit LIMIT and reports truncation only when rows were really cut.</summary>
public sealed class FederatedRowLimitTests
{
    private static readonly TableMetadata CrmCustomers = HttpTable("crm_api", "public", "customers", "id", "name", "status");
    private static readonly TableMetadata ErpCustomers = SqlTable("erp", "public", "customers", "id", "credit");

    private static FederatedServiceHarness.Pipeline Build() =>
        FederatedServiceHarness.Build(
            [
                (CrmCustomers, new StubTableConnector("crm_api",
                [
                    Row(("id", "1"), ("name", "Alpha"), ("status", "OPEN")),
                    Row(("id", "2"), ("name", "Beta"), ("status", "CLOSED")),
                    Row(("id", "3"), ("name", "Gamma"), ("status", "OPEN"))
                ])),
                (ErpCustomers, new StubTableConnector("erp",
                [
                    Row(("id", "1"), ("credit", "100")),
                    Row(("id", "2"), ("credit", "200")),
                    Row(("id", "3"), ("credit", "300"))
                ]))
            ],
            Consents(Allow(CrmCustomers.Identifier), Allow(ErpCustomers.Identifier)));

    private const string Join =
        "SELECT c.id, e.credit FROM crm_api.public.customers c JOIN erp.public.customers e ON c.id = e.id ORDER BY c.id";

    [Fact]
    public async Task ExplicitLimit_IsHonoured_AndNotReportedAsTruncated()
    {
        var result = await Build().RunFullAsync(Join + " LIMIT 2");

        result.RowCount.ShouldBe(2);
        result.Truncated.ShouldBeFalse();   // the client LIMIT is satisfied exactly, the gateway cut nothing
    }

    [Fact]
    public async Task ExactSizeResult_IsNotReportedAsTruncated()
    {
        var result = await Build().RunFullAsync(Join + " LIMIT 3");

        result.RowCount.ShouldBe(3);
        result.Truncated.ShouldBeFalse();
    }
}
