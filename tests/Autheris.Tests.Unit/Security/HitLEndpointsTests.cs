namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public class HitLEndpointsTests
{
    private readonly InMemoryClusterStateProvider _clusterState;
    private readonly TotpVerificationService _totpService;
    private readonly InMemoryTotpSecretStore _secretStore;
    private readonly IOptions<GatewayOptions> _options;

    public HitLEndpointsTests()
    {
        _clusterState = new InMemoryClusterStateProvider();
        _totpService = new TotpVerificationService(_clusterState);
        _secretStore = new InMemoryTotpSecretStore();
        _options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                Enabled = true,
                ApprovalTimeoutSeconds = 5,
                RequireDifferentApprover = true,
                RequireTotp2Fa = true,
                AutoCreateItsmTicket = false
            }
        });
    }

    private HitLStepUpApprovalService CreateHitLService()
    {
        return new HitLStepUpApprovalService(
            _options,
            NullLogger<HitLStepUpApprovalService>.Instance,
            scopeFactory: null,
            clusterState: _clusterState,
            totpVerificationService: _totpService,
            totpSecretStore: _secretStore);
    }

    private static HttpContext CreateHttpContext(string userSid, string role = "GovernanceAdmin", string tenant = "tenant-1")
    {
        var context = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userSid),
            new("sub", userSid),
            new("oid", userSid),
            new(ClaimTypes.Role, role),
            new("roles", role),
            new("tenant_id", tenant),
            new(ClaimTypes.Email, $"{userSid}@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        context.User = new ClaimsPrincipal(identity);
        return context;
    }

    [Fact]
    public async Task ApproveTicketById_WithValidTotp_Succeeds()
    {
        var hitlService = CreateHitLService();
        var approverSid = "steward-1";
        var enrollment = _totpService.GenerateEnrollment(approverSid, "steward1@example.com");
        await _secretStore.SetSecretAsync(approverSid, enrollment.SecretBase32);

        var requestTask = hitlService.RequestStepUpApprovalAsync(
            "query_table",
            "tenant-1",
            "user-alice",
            TableIdentifier.Parse("sales.orders"));

        var pending = hitlService.GetPendingTickets("tenant-1");
        pending.Count.ShouldBe(1);
        var ticket = pending[0];

        var context = CreateHttpContext(approverSid, role: "GovernanceAdmin", tenant: "tenant-1");
        var validCode = _totpService.GenerateTotpCode(enrollment.SecretBase32);

        // Act: approve via endpoint
        var approver = HitLEndpoints.BuildApproverContext(context);
        approver.ShouldNotBeNull();

        var result = await hitlService.ApproveStepUpRequestAsync(ticket.ApprovalId, approver, validCode);

        // Assert
        result.IsApproved.ShouldBeTrue();
        (await requestTask).IsApproved.ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveTicketById_WithInvalidTotp_ReturnsFailedApproval()
    {
        var hitlService = CreateHitLService();
        var approverSid = "steward-2";
        var enrollment = _totpService.GenerateEnrollment(approverSid, "steward2@example.com");
        await _secretStore.SetSecretAsync(approverSid, enrollment.SecretBase32);

        var requestTask = hitlService.RequestStepUpApprovalAsync(
            "query_table",
            "tenant-1",
            "user-alice",
            TableIdentifier.Parse("sales.orders"));

        var pending = hitlService.GetPendingTickets("tenant-1");
        var ticket = pending[0];

        var context = CreateHttpContext(approverSid, role: "GovernanceAdmin", tenant: "tenant-1");
        var approver = HitLEndpoints.BuildApproverContext(context);
        approver.ShouldNotBeNull();

        // Act: approve with wrong code
        var result = await hitlService.ApproveStepUpRequestAsync(ticket.ApprovalId, approver, "000000");

        // Assert
        result.IsApproved.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("TOTP 2FA verification failed");

        // Ticket still pending
        (await hitlService.GetTicketAsync(ticket.ApprovalId))!.Status.ShouldBe(HitLApprovalStatus.Pending);
    }

    [Fact]
    public async Task TwoFactorEnrollmentAndVerification_Flow_Succeeds()
    {
        var userSid = "user-enroll-test";
        var context = CreateHttpContext(userSid);

        // 1. Enroll
        var enrollment = _totpService.GenerateEnrollment(userSid, $"{userSid}@example.com", "Autheris");
        enrollment.SecretBase32.ShouldNotBeNullOrWhiteSpace();
        enrollment.QrCodeUri.ShouldContain("otpauth://totp/Autheris:");

        // Simulate saving pending secret in cluster
        await _clusterState.SetAsync($"totp:pending:{userSid}", enrollment.SecretBase32, TimeSpan.FromMinutes(15));

        // 2. Verify enrollment with valid code
        var code = _totpService.GenerateTotpCode(enrollment.SecretBase32);
        var isValid = await _totpService.VerifyAndConsumeTotpAsync(userSid, enrollment.SecretBase32, code);
        isValid.ShouldBeTrue();

        await _secretStore.SetSecretAsync(userSid, enrollment.SecretBase32);

        // 3. User is now enrolled
        (await _secretStore.HasSecretAsync(userSid)).ShouldBeTrue();
        (await _secretStore.GetSecretAsync(userSid)).ShouldBe(enrollment.SecretBase32);
    }
}
