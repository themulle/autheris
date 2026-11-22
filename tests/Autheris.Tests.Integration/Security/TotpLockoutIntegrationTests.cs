namespace Autheris.Tests.Integration.Security;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TotpLockoutIntegrationTests : IClassFixture<TotpLockoutIntegrationTests.HostFixture>
{
    private const string ApproverSid = "S-1-5-21-TOTP-APPROVER";
    private const string RequesterSid = "S-1-5-21-TOTP-REQUESTER";
    private const string Tenant = "tenant_totp_lockout";

    private readonly WebApplicationFactory<Program> _factory;

    public TotpLockoutIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("TotpLockoutIntegration"));
            builder.UseSetting("Gateway:HitLStepUp:Enabled", "true");
            builder.UseSetting("Gateway:HitLStepUp:RequireTotp2Fa", "true");
            builder.UseSetting("Gateway:HitLStepUp:RequireDifferentApprover", "true");
            builder.UseSetting("Gateway:HitLStepUp:ApprovalTimeoutSeconds", "30");
            builder.UseSetting("Gateway:Totp:MaxFailedAttempts", "3");
            builder.UseSetting("Gateway:Totp:LockoutMinutes", "15");
            base.ConfigureWebHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private HttpClient CreateApproverClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", ApproverSid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        return client;
    }

    [Fact]
    public async Task Replay_cannot_unlock()
    {
        using var client = CreateApproverClient();

        // 1. Begin 2FA enrollment and read the server-generated Base32 seed.
        var enrollResp = await client.PostAsync("/api/v1/governance/2fa/enroll", content: null);
        enrollResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var enrollJson = JsonDocument.Parse(await enrollResp.Content.ReadAsStringAsync());
        var secretBase32 = enrollJson.RootElement.GetProperty("secretBase32").GetString();
        secretBase32.ShouldNotBeNullOrWhiteSpace();

        // Generate two adjacent codes (current step and next step) so the test stays immune to a 30s boundary tick.
        var totpService = _factory.Services.GetRequiredService<ITotpVerificationService>();
        var t0 = DateTimeOffset.UtcNow;
        var consumedCode = totpService.GenerateTotpCode(secretBase32, t0);
        var nextStepCode = totpService.GenerateTotpCode(secretBase32, t0.AddSeconds(30));

        // 2. Complete enrollment by verifying and consuming consumedCode.
        var verifyResp = await client.PostAsJsonAsync(
            "/api/v1/governance/2fa/verify-enrollment",
            new TotpVerifyRequest(consumedCode));
        verifyResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Open a HitL step-up approval ticket for a distinct requester.
        var hitl = _factory.Services.GetRequiredService<IHitLStepUpApprovalService>();
        using var cts = new CancellationTokenSource();
        var pendingTask = hitl.RequestStepUpApprovalAsync(
            "query_table",
            Tenant,
            RequesterSid,
            new TableIdentifier("sales", "dbo", "orders"),
            ct: cts.Token);

        try
        {
            await WaitHelper.WaitUntil(
                () => hitl.GetPendingTickets(Tenant).Count > 0,
                WaitHelper.HangGuard,
                "the HitL step-up ticket was never registered");

            var ticket = hitl.GetPendingTickets(Tenant)[0];
            var approvePath = $"/api/governance/hitl/tickets/{ticket.ApprovalId}/approve";

            // 4. Replay the already-consumed code 3 times to reach MaxFailedAttempts and lock the approver out.
            for (var i = 0; i < 3; i++)
            {
                var replayResp = await client.PostAsJsonAsync(approvePath, new { totpCode = consumedCode });
                replayResp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                using var body = JsonDocument.Parse(await replayResp.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("isApproved").GetBoolean().ShouldBeFalse();
            }

            // 5. After lockout, neither a fresh valid code nor another replay can unlock or approve the ticket.
            var lockedFreshResp = await client.PostAsJsonAsync(approvePath, new { totpCode = nextStepCode });
            lockedFreshResp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using (var body = JsonDocument.Parse(await lockedFreshResp.Content.ReadAsStringAsync()))
            {
                body.RootElement.GetProperty("isApproved").GetBoolean().ShouldBeFalse();
            }

            var lockedReplayResp = await client.PostAsJsonAsync(approvePath, new { totpCode = consumedCode });
            lockedReplayResp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using (var body = JsonDocument.Parse(await lockedReplayResp.Content.ReadAsStringAsync()))
            {
                body.RootElement.GetProperty("isApproved").GetBoolean().ShouldBeFalse();
            }

            var storedTicket = await hitl.GetTicketAsync(ticket.ApprovalId);
            storedTicket.ShouldNotBeNull();
            storedTicket.Status.ShouldBe(HitLApprovalStatus.Pending);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await pendingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
