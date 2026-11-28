namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Quota;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

/// <summary><c>GET /api/v1/me/limits</c> answers the calling token only and rejects an unauthenticated request.</summary>
[Trait("Category", "Security")]
public sealed class CallerLimitsEndpointTests
{
    private sealed class FakeLimits : ICallerQueryLimitsService
    {
        public ClaimsPrincipal? Seen { get; private set; }

        public Task<CallerQueryLimits> ResolveAsync(ClaimsPrincipal principal, string? apiKey, string? clientIp, CancellationToken ct = default)
        {
            Seen = principal;
            var one = new LimitValue(1, "scope");
            return Task.FromResult(new CallerQueryLimits(
                "Standard",
                new ConcurrencyLimits(one, one, one),
                one,
                one,
                null,
                new GraphQlQueryLimits(one, one, one, one)));
        }
    }

    [Fact]
    public async Task AuthenticatedCaller_GetsOwnLimits()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-1"), new Claim("sid", "S-1-5-21-1"), DefaultTenantCaller.Claim], "TestAuth"));
        var context = new DefaultHttpContext { User = principal };
        var service = new FakeLimits();

        var result = await CallerLimitsEndpoints.HandleGetLimitsAsync(context, service, CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status200OK);
        service.Seen.ShouldBeSameAs(principal);
    }

    [Fact]
    public async Task UnauthenticatedRequest_IsRejected_WithoutResolvingLimits()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var service = new FakeLimits();

        var result = await CallerLimitsEndpoints.HandleGetLimitsAsync(context, service, CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        service.Seen.ShouldBeNull();
    }
}
