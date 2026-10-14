namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// FR-02: routes marked AuditLevel.Delegated audit their successful path in the domain layer, but denials, failures and
/// exceptions must still be audited by the central middleware.
/// </summary>
public sealed class DelegatedAuditRouteIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DelegatedAuditRouteIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    private HttpClient CreateClient(string? sid, string roles = "User")
    {
        var client = _factory.CreateClient();
        if (sid != null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        }
        return client;
    }

    private async Task<AuditLogEntry[]> ReadAuditAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        return (await repo.GetAuditLogEntriesAsync(1000)).ToArray();
    }

    [Fact]
    public async Task UnauthenticatedJobSubmit_Returns401_AndRecordsAuthFailedAudit()
    {
        var client = CreateClient(null);

        var response = await client.PostAsJsonAsync("/api/v1/jobs/query", new { query = "SELECT 1" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var entries = await ReadAuditAsync();
        entries.ShouldContain(e => e.EventType == AuditEventTypes.AuthFailed && e.Decision == "DENY");
    }

    [Fact]
    public async Task ForeignUsersJobResult_Returns404_AndRecordsDenyAudit_WithoutOwnerLeak()
    {
        var owner = CreateClient("S-1-5-21-OWNER-ALICE");
        var submit = await owner.PostAsJsonAsync("/api/v1/jobs/query", new { query = "SELECT 1" });
        submit.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var doc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var jobId = doc.RootElement.GetProperty("jobId").GetString()!;

        var intruder = CreateClient("S-1-5-21-INTRUDER-MALLORY");
        var response = await intruder.GetAsync($"/api/v1/jobs/{jobId}/result");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var entries = await ReadAuditAsync();
        var deny = entries.Where(e => e.EventType == AuditEventTypes.AsyncJobAccessDenied).ToArray();
        deny.ShouldNotBeEmpty();
        deny.ShouldAllBe(e => e.Decision == "DENY" && e.TargetTable == $"job:{jobId}" && !e.DetailsJson.Contains("OWNER-ALICE"));
        entries.ShouldContain(e => e.EventType == AuditEventTypes.AsyncJobSubmitted && e.Decision == "ALLOW");
    }

    [Fact]
    public async Task TokenRevocation_ByNonAdmin_Returns403_AndRecordsDenyAudit()
    {
        var client = CreateClient("S-1-5-21-PLAIN-USER");

        var response = await client.PostAsJsonAsync("/api/admin/tokens/revoke", new { subjectOrJti = "someone" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var entries = await ReadAuditAsync();
        entries.ShouldContain(e => e.EventType == AuditEventTypes.AuthzEndpointDenied && e.Decision == "DENY"
                                   && e.DetailsJson.Contains("/api/admin/tokens/revoke"));
    }
}
