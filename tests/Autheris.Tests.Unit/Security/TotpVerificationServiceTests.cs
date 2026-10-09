namespace Autheris.Tests.Unit.Security;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public class TotpVerificationServiceTests
{
    private readonly InMemoryClusterStateProvider _clusterState;
    private readonly TotpVerificationService _totpService;

    public TotpVerificationServiceTests()
    {
        _clusterState = new InMemoryClusterStateProvider();
        _totpService = new TotpVerificationService(_clusterState);
    }

    [Fact]
    public void GenerateEnrollment_GeneratesValidBase32AndUri()
    {
        // Act
        var enrollment = _totpService.GenerateEnrollment("user-alice-123", "alice@example.com", "Autheris");

        // Assert
        enrollment.ShouldNotBeNull();
        enrollment.SecretBase32.ShouldNotBeNullOrWhiteSpace();
        enrollment.SecretBase32.Length.ShouldBe(32); // 160-bit key in Base32 = 32 chars
        enrollment.SecretBase32.ShouldMatch("^[A-Z2-7]+$");

        enrollment.QrCodeUri.ShouldStartWith("otpauth://totp/Autheris:alice");
        enrollment.QrCodeUri.ShouldContain("secret=" + enrollment.SecretBase32);
        enrollment.QrCodeUri.ShouldContain("issuer=Autheris");

        enrollment.FormattedKey.ShouldNotBeNullOrWhiteSpace();
        enrollment.FormattedKey.ShouldContain(" ");

        enrollment.RecoveryCodes.ShouldNotBeNull();
        enrollment.RecoveryCodes.Count.ShouldBeGreaterThanOrEqualTo(8);
    }

    [Fact]
    public void VerifyTotp_Rfc6238StandardTestVectors_MatchesSpecification()
    {
        // RFC 6238 Appendix B test vector:
        // ASCII Secret: "12345678901234567890" -> Base32: "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
        var rfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

        // T = 59s -> step 1 -> 6-digit TOTP = 287082
        var timeStep1 = DateTimeOffset.FromUnixTimeSeconds(59);
        var code1 = _totpService.GenerateTotpCode(rfcSecret, timeStep1);
        code1.ShouldBe("287082");

        // T = 1111111109s -> step 37037036 -> 6-digit TOTP = 081804
        var timeStep2 = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var code2 = _totpService.GenerateTotpCode(rfcSecret, timeStep2);
        code2.ShouldBe("081804");

        // T = 1234567890s -> step 41152263 -> 6-digit TOTP = 005924
        var timeStep3 = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var code3 = _totpService.GenerateTotpCode(rfcSecret, timeStep3);
        code3.ShouldBe("005924");

        // T = 2000000000s -> step 66666666 -> 6-digit TOTP = 279037
        var timeStep4 = DateTimeOffset.FromUnixTimeSeconds(2000000000);
        var code4 = _totpService.GenerateTotpCode(rfcSecret, timeStep4);
        code4.ShouldBe("279037");
    }

    [Fact]
    public void VerifyTotp_ValidCode_CurrentTimestamp_ReturnsTrue()
    {
        var enrollment = _totpService.GenerateEnrollment("user-bob", "bob@example.com");
        var currentCode = _totpService.GenerateTotpCode(enrollment.SecretBase32);

        var isValid = _totpService.VerifyTotp(enrollment.SecretBase32, currentCode);

        isValid.ShouldBeTrue();
    }

    [Fact]
    public void VerifyTotp_ToleranceWindow_AcceptsPreviousAndNextStep()
    {
        var enrollment = _totpService.GenerateEnrollment("user-charlie", "charlie@example.com");
        var now = DateTimeOffset.UtcNow;

        var pastCode = _totpService.GenerateTotpCode(enrollment.SecretBase32, now.AddSeconds(-30));
        var futureCode = _totpService.GenerateTotpCode(enrollment.SecretBase32, now.AddSeconds(30));
        var tooOldCode = _totpService.GenerateTotpCode(enrollment.SecretBase32, now.AddSeconds(-70));
        var tooFarFutureCode = _totpService.GenerateTotpCode(enrollment.SecretBase32, now.AddSeconds(70));

        // Within 1 step tolerance (+/- 30s)
        _totpService.VerifyTotp(enrollment.SecretBase32, pastCode, toleranceSteps: 1).ShouldBeTrue();
        _totpService.VerifyTotp(enrollment.SecretBase32, futureCode, toleranceSteps: 1).ShouldBeTrue();

        // Beyond tolerance
        _totpService.VerifyTotp(enrollment.SecretBase32, tooOldCode, toleranceSteps: 1).ShouldBeFalse();
        _totpService.VerifyTotp(enrollment.SecretBase32, tooFarFutureCode, toleranceSteps: 1).ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAndConsumeTotpAsync_ReplayAttempt_RejectsSecondUse()
    {
        var enrollment = _totpService.GenerateEnrollment("user-david", "david@example.com");
        var currentCode = _totpService.GenerateTotpCode(enrollment.SecretBase32);

        // First use: Valid
        var firstAttempt = await _totpService.VerifyAndConsumeTotpAsync("user-david", enrollment.SecretBase32, currentCode);
        firstAttempt.ShouldBeTrue();

        // Second use: Replay attack rejected!
        var secondAttempt = await _totpService.VerifyAndConsumeTotpAsync("user-david", enrollment.SecretBase32, currentCode);
        secondAttempt.ShouldBeFalse();
    }

    [Fact]
    public async Task HitLApproval_WithTotp_ApprovesOnValidAndRejectsOnInvalidOrReplay()
    {
        var secretStore = new InMemoryTotpSecretStore();
        var approverSid = "steward-eve";
        var enrollment = _totpService.GenerateEnrollment(approverSid, "eve@example.com");
        await secretStore.SetSecretAsync(approverSid, enrollment.SecretBase32);

        var options = Options.Create(new GatewayOptions
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

        var hitlService = new HitLStepUpApprovalService(
            options,
            NullLogger<HitLStepUpApprovalService>.Instance,
            scopeFactory: null,
            clusterState: _clusterState,
            totpVerificationService: _totpService,
            totpSecretStore: secretStore);

        // Ticket 1: requester is alice
        var requestTask1 = hitlService.RequestStepUpApprovalAsync(
            "query_table",
            "tenant-1",
            "user-alice",
            TableIdentifier.Parse("sales.orders"));

        var pending1 = hitlService.GetPendingTickets("tenant-1");
        pending1.Count.ShouldBe(1);
        var ticket1 = pending1[0];

        var approverContext = new HitLApproverContext(approverSid, new[] { approverSid }, "tenant-1");

        // Attempt 1: Invalid TOTP code
        var invalidResult = await hitlService.ApproveStepUpRequestAsync(ticket1.ApprovalId, approverContext, "000000");
        invalidResult.IsApproved.ShouldBeFalse();

        // Attempt 2: Valid TOTP code
        var validCode = _totpService.GenerateTotpCode(enrollment.SecretBase32);
        var validResult = await hitlService.ApproveStepUpRequestAsync(ticket1.ApprovalId, approverContext, validCode);
        validResult.IsApproved.ShouldBeTrue();
        (await requestTask1).IsApproved.ShouldBeTrue();

        // Ticket 2: Requester is bob
        var requestTask2 = hitlService.RequestStepUpApprovalAsync(
            "query_table",
            "tenant-1",
            "user-bob",
            TableIdentifier.Parse("sales.customers"));

        var pending2 = hitlService.GetPendingTickets("tenant-1");
        pending2.Count.ShouldBe(1);
        var ticket2 = pending2[0];

        // Attempt 3: Replay same TOTP code on Ticket 2
        var replayResult = await hitlService.ApproveStepUpRequestAsync(ticket2.ApprovalId, approverContext, validCode);
        replayResult.IsApproved.ShouldBeFalse();
        (await hitlService.GetTicketAsync(ticket2.ApprovalId))!.Status.ShouldBe(HitLApprovalStatus.Pending);
    }
}
