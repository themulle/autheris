namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Events;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class EventAndWebhookIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EventAndWebhookIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task DepositDocument_PublishesDocumentDepositedEventToEventBus()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-DOCADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var eventBus = _factory.Services.GetRequiredService<IGatewayEventBus>();
        DocumentDepositedEvent? receivedEvent = null;

        using var sub = eventBus.Subscribe<DocumentDepositedEvent>((ev, ct) =>
        {
            receivedEvent = ev;
            return Task.CompletedTask;
        });

        var payload = JsonSerializer.Serialize(new
        {
            title = "Orders Service Architecture",
            content = "# Architecture\nMicroservice processing orders.",
            tags = new[] { "architecture", "orders" }
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync("/api/v1/docs/apps/orders-service/documents/architecture/overview", content);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ((IGatewayEventBusDrain)eventBus).DrainAsync();
        receivedEvent.ShouldNotBeNull();
        receivedEvent.AppId.ShouldBe("orders-service");
        receivedEvent.Category.ShouldBe("architecture");
        receivedEvent.DocId.ShouldBe("overview");
        receivedEvent.Title.ShouldBe("Orders Service Architecture");
        receivedEvent.RelativePath.ShouldBe("orders-service/architecture/overview");
    }

    [Fact]
    public async Task AccessPlanningService_PlanAndStateChange_PublishesDomainEvents()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var planningService = scope.ServiceProvider.GetRequiredService<IAccessPlanningService>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IGatewayEventBus>();

        var receivedEvents = new List<IGatewayEvent>();
        using var sub = eventBus.Subscribe<IGatewayEvent>((ev, ct) =>
        {
            lock (receivedEvents)
            {
                receivedEvents.Add(ev);
            }
            return Task.CompletedTask;
        });

        // Act 1: Plan Access
        var planReq = new AdminPlanAccessRequest(
            DatasetId: "crm.customers",
            Grants:
            [
                new PrincipalAccessGrant("david", new Dictionary<string, string> { ["email"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(30))
            ],
            Reason: "Customer onboarding verification");

        var planResult = await planningService.PlanAccessAsync(planReq, "admin-1");
        planResult.ShouldNotBeNull();

        // Act 2: Set Dataset State
        var stateReq = new AdminSetDatasetStateRequest(
            DatasetId: "crm.customers",
            State: "inactive",
            Reason: "Maintenance mode");

        var stateResult = await planningService.SetDatasetStateAsync(stateReq, "admin-1");
        stateResult.ShouldNotBeNull();

        // Assert
        await ((IGatewayEventBusDrain)eventBus).DrainAsync();
        lock (receivedEvents)
        {
            receivedEvents.ShouldContain(e => e is AccessPlanCreatedEvent && ((AccessPlanCreatedEvent)e).DatasetId == "crm.customers");
            receivedEvents.ShouldContain(e => e is DatasetStateChangedEvent && ((DatasetStateChangedEvent)e).DatasetId == "crm.customers");
        }
    }
}
