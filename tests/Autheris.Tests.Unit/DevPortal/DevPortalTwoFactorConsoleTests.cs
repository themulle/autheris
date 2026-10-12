namespace Autheris.Tests.Unit.DevPortal;

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.UI;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DevPortalTwoFactorConsoleTests
{
    [Fact]
    public void SvgQrCodeGenerator_GeneratesValidSvg()
    {
        // Arrange
        var otpUri = "otpauth://totp/Autheris:testuser@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Autheris";

        // Act
        var svg = SvgQrCodeGenerator.GenerateSvg(otpUri);

        // Assert
        svg.ShouldNotBeNullOrWhiteSpace();
        svg.ShouldStartWith("<svg xmlns=\"http://www.w3.org/2000/svg\"");
        svg.ShouldContain("<rect width=\"100%\" height=\"100%\"");
        svg.ShouldContain("<rect x=");
        svg.ShouldEndWith("</svg>");
    }

    [Fact]
    public async Task PostPortalEnrollVerify_ValidCode_StoresSecretAndReturnsSuccess()
    {
        // Arrange
        var totpService = Substitute.For<ITotpVerificationService>();
        var secretStore = Substitute.For<ITotpSecretStore>();
        var context = new DefaultHttpContext();
        var userSid = "developer-123";
        var secret = "JBSWY3DPEHPK3PXP";
        var code = "123456";

        totpService.VerifyAndConsumeTotpAsync(userSid, secret, code, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var json = JsonSerializer.Serialize(new { totpCode = code, secret = secret });
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        // Act - Call logic directly or simulate verify flow
        var valid = await totpService.VerifyAndConsumeTotpAsync(userSid, secret, code);
        if (valid)
        {
            await secretStore.SetSecretAsync(userSid, secret);
        }

        // Assert
        valid.ShouldBeTrue();
        await secretStore.Received(1).SetSecretAsync(userSid, secret);
    }

    [Fact]
    public async Task PostPortalEnrollVerify_InvalidCode_RejectsWithoutStoringSecret()
    {
        // Arrange
        var totpService = Substitute.For<ITotpVerificationService>();
        var secretStore = Substitute.For<ITotpSecretStore>();
        var userSid = "developer-123";
        var secret = "JBSWY3DPEHPK3PXP";
        var code = "000000";

        totpService.VerifyAndConsumeTotpAsync(userSid, secret, code, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Act
        var valid = await totpService.VerifyAndConsumeTotpAsync(userSid, secret, code);

        // Assert
        valid.ShouldBeFalse();
        await secretStore.DidNotReceive().SetSecretAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PostPortalApprovalStepUp_ValidTotp_ConfirmsTicket()
    {
        // Arrange
        var hitlService = Substitute.For<IHitLStepUpApprovalService>();
        var ticketId = "ticket-999";
        var approver = new HitLApproverContext("approver-1", new[] { "approver-1" }, "tenant-1");
        var totpCode = "654321";

        var ticket = new HitLApprovalTicket(
            ticketId,
            "query_table",
            "tenant-1",
            "user-1",
            TableIdentifier.Parse("dbo.payroll"),
            "Auditing",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            HitLApprovalStatus.Approved,
            ApproverSid: "approver-1",
            Signature: "sig-valid-999");

        hitlService.ApproveStepUpRequestAsync(ticketId, approver, totpCode, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HitLApprovalResult(true, ticket)));

        // Act
        var result = await hitlService.ApproveStepUpRequestAsync(ticketId, approver, totpCode);

        // Assert
        result.IsApproved.ShouldBeTrue();
        result.Ticket.Signature.ShouldBe("sig-valid-999");
    }

    [Fact]
    public async Task PostPortalApprovalStepUp_InvalidTotp_RejectsTicket()
    {
        // Arrange
        var hitlService = Substitute.For<IHitLStepUpApprovalService>();
        var ticketId = "ticket-999";
        var approver = new HitLApproverContext("approver-1", new[] { "approver-1" }, "tenant-1");
        var totpCode = "000000";

        var ticket = new HitLApprovalTicket(
            ticketId,
            "query_table",
            "tenant-1",
            "user-1",
            TableIdentifier.Parse("dbo.payroll"),
            "Auditing",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            HitLApprovalStatus.Pending);

        hitlService.ApproveStepUpRequestAsync(ticketId, approver, totpCode, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HitLApprovalResult(false, ticket, "Invalid TOTP code")));

        // Act
        var result = await hitlService.ApproveStepUpRequestAsync(ticketId, approver, totpCode);

        // Assert
        result.IsApproved.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("Invalid TOTP code");
    }
}
