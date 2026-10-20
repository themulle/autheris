namespace Autheris.Tests.Unit.Identity;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class DeprovisioningLockTests
{
    private readonly IIdentityStoreRepository _identityStore = Substitute.For<IIdentityStoreRepository>();
    private bool _nextCalled;

    private RequestDelegate CreateNext() => _ =>
    {
        _nextCalled = true;
        return Task.CompletedTask;
    };

    private static HttpContext CreateContextWithUser(string userKey, string realmId = "workforce-entra")
    {
        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim("oid", userKey),
            new Claim("sub", userKey),
            new Claim("autheris:realm", realmId),
            new Claim("tenant_id", "tenant-1")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        context.User = new ClaimsPrincipal(identity);
        return context;
    }

    [Fact]
    public async Task SubjectLockoutMiddleware_LockedSubject_Returns401()
    {
        var middleware = new SubjectLockoutMiddleware(CreateNext(), NullLogger<SubjectLockoutMiddleware>.Instance);
        var context = CreateContextWithUser("user-locked");

        _identityStore.IsSubjectLockedAsync(
            new TenantId("tenant-1"), "workforce-entra", "user-locked", Arg.Any<CancellationToken>())
            .Returns(true);

        await middleware.InvokeAsync(context, _identityStore);

        _nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task SubjectLockoutMiddleware_UnlockedSubject_PassesThrough()
    {
        var middleware = new SubjectLockoutMiddleware(CreateNext(), NullLogger<SubjectLockoutMiddleware>.Instance);
        var context = CreateContextWithUser("user-active");

        _identityStore.IsSubjectLockedAsync(
            new TenantId("tenant-1"), "workforce-entra", "user-active", Arg.Any<CancellationToken>())
            .Returns(false);

        await middleware.InvokeAsync(context, _identityStore);

        _nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task SubjectLockoutMiddleware_StoreFailure_FailsClosedWith503()
    {
        var middleware = new SubjectLockoutMiddleware(CreateNext(), NullLogger<SubjectLockoutMiddleware>.Instance);
        var context = CreateContextWithUser("user-active");

        _identityStore.IsSubjectLockedAsync(
            new TenantId("tenant-1"), "workforce-entra", "user-active", Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("Database connection timeout during lockout check"));

        await middleware.InvokeAsync(context, _identityStore);

        _nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }
}
