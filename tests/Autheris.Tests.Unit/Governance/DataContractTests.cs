namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class DataContractTests
{
    private static DataContract CreateContract(TableIdentifier table, bool isPassing = true)
    {
        return new DataContract(
            Id: $"contract-{table.TableName}",
            Table: table,
            Version: "1.0.0",
            Status: isPassing ? DataContractStatus.Active : DataContractStatus.Broken,
            SchemaColumns: new[] { "id", "customer_id", "amount", "created_at" },
            Assertions: new[]
            {
                new DataContractAssertion(
                    Id: "freshness-1",
                    Type: AssertionType.Freshness,
                    Description: "Data freshness < 24h",
                    Column: "created_at",
                    Threshold: "24h",
                    IsPassing: isPassing
                )
            },
            Sla: new DataContractSla(FreshnessHours: 24),
            OwnerTeam: "BillingTeam",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        );
    }

    [Fact]
    public void DataContract_IsHealthy_ReflectsAssertionStatus()
    {
        var healthy = CreateContract(new TableIdentifier("sales", "crm", "orders"), isPassing: true);
        healthy.IsHealthy.ShouldBeTrue();

        var broken = CreateContract(new TableIdentifier("sales", "crm", "orders"), isPassing: false);
        broken.IsHealthy.ShouldBeFalse();
    }

    [Fact]
    public async Task InMemoryDataContractEngine_RegisterAndEvaluate_UpdatesHealth()
    {
        var engine = new InMemoryDataContractEngine();
        var table = new TableIdentifier("sales", "crm", "orders");
        var contract = CreateContract(table, isPassing: true);

        await engine.RegisterContractAsync(contract);

        var retrieved = await engine.GetContractAsync(table);
        retrieved.ShouldNotBeNull();
        retrieved.Id.ShouldBe("contract-orders");
        (await engine.IsTableHealthyAsync(table)).ShouldBeTrue();

        // Simulate failing assertion (e.g. freshness SLA breached)
        var updated = await engine.UpdateAssertionResultAsync(
            contractId: contract.Id,
            assertionId: "freshness-1",
            isPassing: false,
            failureReason: "Max freshness exceeded: last row is 48 hours old.");

        updated.ShouldBeTrue();
        (await engine.IsTableHealthyAsync(table)).ShouldBeFalse();

        var updatedContract = await engine.GetContractAsync(table);
        updatedContract.ShouldNotBeNull();
        updatedContract.Status.ShouldBe(DataContractStatus.Broken);
        updatedContract.Assertions[0].FailureReason!.ShouldContain("48 hours");
    }

    [Fact]
    public async Task DataHubCatalogClient_IngestMetadataChangeEvent_Succeeds()
    {
        var engine = new InMemoryDataContractEngine();
        var client = new DataHubCatalogClient(engine, NullLogger<DataHubCatalogClient>.Instance);

        const string mclEvent = """
        {
          "entityType": "assertion",
          "entityUrn": "urn:li:assertion:sales_orders_freshness",
          "aspect": {
            "type": "SUCCESS"
          }
        }
        """;

        var result = await client.IngestMetadataChangeEventAsync(mclEvent);
        result.ShouldBeTrue();
    }
}
