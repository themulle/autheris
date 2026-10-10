namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-18 (SEC-1): access decisions in the WebSQL cross-source path are keyed by the full, source-qualified
/// table name. Two tables of different sources with the same schema and table name keep their own decisions, and a
/// table without a decision is denied instead of being read unrestricted.
/// </summary>
public sealed class FederatedDecisionKeyTests
{
    private static readonly TableMetadata CrmCustomers = HttpTable("crm_api", "public", "customers", "id", "name", "status");
    private static readonly TableMetadata ErpCustomers = SqlTable("erp", "public", "customers", "id", "credit");

    private const string HttpFirst =
        "SELECT c.id, c.status, e.credit FROM crm_api.public.customers c JOIN erp.public.customers e ON c.id = e.id";

    private const string SqlFirst =
        "SELECT c.id, c.status, e.credit FROM erp.public.customers e JOIN crm_api.public.customers c ON c.id = e.id";

    private static FederatedServiceHarness.Pipeline Build(IConsentRepository consents, VirtualFilterSnapshot? snapshot = null) =>
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
            consents,
            snapshot);

    private static ConsentRowFilter StatusOpen() => new() { ColumnName = "status", Operator = "EQ", ValueType = "string", ValueJson = "\"OPEN\"" };

    private static async Task<List<string>> RunAsync(FederatedServiceHarness.Pipeline pipeline, string sql)
    {
        var rows = await pipeline.RunAsync(sql);
        return rows
            .Select(r => $"{r["id"]}|{r["status"]}|{r["credit"]}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(HttpFirst)]
    [InlineData(SqlFirst)]
    public async Task SameSchemaAndTableName_InTwoSources_EachTableKeepsItsOwnRowFilter(string sql)
    {
        // ST-1 / ST-2: the unrestricted decision of erp.public.customers must not replace the row filter of the HTTP table.
        var pipeline = Build(Consents(Allow(CrmCustomers.Identifier, StatusOpen()), Allow(ErpCustomers.Identifier)));

        var rows = await RunAsync(pipeline, sql);

        rows.ShouldBe(["1|OPEN|100", "3|OPEN|300"]);
    }

    [Theory]
    [InlineData(HttpFirst)]
    [InlineData(SqlFirst)]
    public async Task SameSchemaAndTableName_VirtualFilterOnTheHttpTable_IsNeverDroppedByTheOtherDecision(string sql)
    {
        // ST-1 / ST-2: the HTTP table keeps its own decision with the applied Virtual Filter; until the gateway-side
        // enforcement exists that decision is denied (403), the rows are never returned unfiltered.
        var pipeline = Build(Consents(Allow(CrmCustomers.Identifier), Allow(ErpCustomers.Identifier)), FederatedServiceHarness.AliceSnapshot("crm_api.*.*", "crm_api.public.customers"));

        await Should.ThrowAsync<GatewayForbiddenException>(() => RunAsync(pipeline, sql));
    }
}
