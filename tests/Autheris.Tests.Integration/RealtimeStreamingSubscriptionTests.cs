namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

public class RealtimeStreamingSubscriptionTests : IClassFixture<RealtimeStreamingSubscriptionTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RealtimeStreamingSubscriptionTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("RealtimeStreamingSubscriptionTests"),
            };
            // The CDC ingestion route belongs to the events module.
            EventsWorkspaceTestSettings.Apply((k, v) => settings[k] = v);
            config.AddInMemoryCollection(settings);
        }
    }

    [Fact]
    public async Task CdcEventIngestEndpoint_ValidDebeziumJson_ReturnsAccepted()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-TEST-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "ClusterAdmin");

        var payload = """
        {
            "op": "u",
            "source": { "schema": "crm", "table": "customers", "name": "salesdb" },
            "before": { "id": 100, "name": "Old Customer" },
            "after": { "id": 100, "name": "New Customer", "email": "customer@acme.com", "tenant_id": "tenant-sales" }
        }
        """;

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/modules/events/v1/cdc/events", content);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.ShouldContain("Ingested");
    }

    [Fact]
    public async Task CdcEventIngestEndpoint_EmptyPayload_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-TEST-ADMIN");

        var content = new StringContent("", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/modules/events/v1/cdc/events", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CdcEventIngestEndpoint_Unauthenticated_ReturnsUnauthorized()
    {
        // Finding A1 Verification:
        // /modules/events/v1/cdc/events MUST require authorization (401 Unauthorized when unauthenticated)
        var client = _factory.CreateClient(); // No auth header

        var payload = """
        {
            "op": "c",
            "source": { "schema": "crm", "table": "customers", "name": "salesdb" },
            "after": { "id": 200, "name": "Unauthorized Ingest", "tenant_id": "tenant-sales" }
        }
        """;

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/modules/events/v1/cdc/events", content);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CdcEventIngestEndpoint_InvalidJson_ReturnsBadRequest_WithoutLeakingInternalDetails()
    {
        // Finding A1 Verification:
        // /modules/events/v1/cdc/events MUST NOT leak exception details (ex.Message) to caller
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-TEST-ADMIN");

        var content = new StringContent("{ broken json payload ???", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/modules/events/v1/cdc/events", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.ShouldNotContain("details");
        responseBody.ShouldNotContain("Exception");
        responseBody.ShouldContain("Invalid CDC event format");
    }

    [Fact]
    public async Task EventStream_PublishAndSubscribe_DeliversEventsLocally()
    {
        using var scope = _factory.Services.CreateScope();
        var channel = scope.ServiceProvider.GetRequiredService<ICdcEventChannel>();

        var table = new TableIdentifier("salesdb", "crm", "customers");
        var cdcEvent = new CdcEvent(
            EventId: "test-stream-1",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-sales",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            Timestamp: DateTimeOffset.UtcNow
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start reading in background task
        var receivedEvents = new List<CdcEvent>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var evt in channel.SubscribeAsync("cdc_all", cts.Token))
            {
                // The channel replays recent history of the shared host (for example the Debezium event of the ingest test): only this test's event counts.
                if (evt.EventId != "test-stream-1")
                {
                    continue;
                }

                receivedEvents.Add(evt);
                break;
            }
        });

        // Publish event
        await channel.PublishAsync(cdcEvent, CancellationToken.None);

        await Task.WhenAny(readTask, Task.Delay(4000));

        receivedEvents.Count.ShouldBeGreaterThanOrEqualTo(1);
        receivedEvents[0].EventId.ShouldBe("test-stream-1");
        receivedEvents[0].Table.TableName.ShouldBe("customers");
    }
}
