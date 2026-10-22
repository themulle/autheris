namespace Autheris.Tests.Unit.Governance.Recertification;

using System.Collections.Generic;
using System.Security.Claims;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Recertification;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>GOV-02: the decision endpoints never fall back to a constant reviewer and map guard failures to 403/404/409.</summary>
[Trait("Category", "Security")]
public sealed class RecertificationEndpointDecisionTests
{
    private static HttpContext Context(params Claim[] claims) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
    };

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;

    [Fact]
    public async Task Certify_WithoutSid_Returns403()
    {
        var service = Substitute.For<IRecertificationService>();

        var result = await RecertificationEndpoints.HandleCertifyAsync("tenant-a", "camp", "item", null, Context(), service, CancellationToken.None);

        Status(result).ShouldBe(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().CertifyItemAsync(default, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Revoke_WithoutSid_Returns403()
    {
        var service = Substitute.For<IRecertificationService>();

        var result = await RecertificationEndpoints.HandleRevokeAsync("tenant-a", "camp", "item", null, Context(), service, CancellationToken.None);

        Status(result).ShouldBe(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().RevokeItemAsync(default, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Certify_ServiceRejectsReviewer_Returns403()
    {
        var service = Substitute.For<IRecertificationService>();
        service.CertifyItemAsync(Arg.Any<TenantId>(), "camp", "item", "S-1-5-21-MALLORY", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new SecurityException("not designated"));

        var result = await RecertificationEndpoints.HandleCertifyAsync(
            "tenant-a", "camp", "item", null, Context(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-MALLORY")), service, CancellationToken.None);

        Status(result).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Certify_AlreadyDecided_Returns409()
    {
        var service = Substitute.For<IRecertificationService>();
        service.CertifyItemAsync(Arg.Any<TenantId>(), "camp", "item", "S-1-5-21-BOB", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new RecertificationConflictException("RECERT_ITEM_ALREADY_DECIDED", "decided"));

        var result = await RecertificationEndpoints.HandleCertifyAsync(
            "tenant-a", "camp", "item", null, Context(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-BOB")), service, CancellationToken.None);

        Status(result).ShouldBe(StatusCodes.Status409Conflict);
    }
}
