namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
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

    [Fact]
    [Trait("Category", "Security")]
    public async Task Lock_survives_revoke_failure()
    {
        var revocation = new StubRevocation(new GrantRevocationResult(0, 0, 0, Failed: 1, Truncated: false));
        var service = new IdentityDeprovisioningService(_identityStore, revocation);

        var act = () => service.DeprovisionAndLockSubjectAsync(new TenantId("tenant-1"), "workforce-entra", "user-1", "SCIM_DEPROVISION", "SCIM_PUT");

        await Should.ThrowAsync<GrantRevocationIncompleteException>(act);
        await _identityStore.Received(1).AddLockAsync(Arg.Any<Autheris.Domain.Identity.ScimLock>(), Arg.Any<CancellationToken>());
        await _identityStore.DidNotReceive().RemoveLockAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Deprovisioning_revokes_grants_for_every_subject_form()
    {
        var revocation = new StubRevocation(new GrantRevocationResult(1, 0, 0, 0, false));
        var service = new IdentityDeprovisioningService(_identityStore, revocation);

        await service.DeprovisionAndLockSubjectAsync(new TenantId("tenant-1"), "workforce-entra", "user-1", "SCIM_DEPROVISION", "SCIM_PUT");

        revocation.Subjects.ShouldBe([new Sid("user-1"), new Sid("workforce-entra|user-1")]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Bare_subject_form_skipped_when_user_key_exists_in_another_realm()
    {
        var tenant = new TenantId("tenant-1");
        _identityStore.UserKeyExistsInOtherRealmAsync(tenant, "workforce-entra", "user-1", Arg.Any<CancellationToken>()).Returns(true);
        var revocation = new StubRevocation(new GrantRevocationResult(1, 0, 0, 0, false));
        var service = new IdentityDeprovisioningService(_identityStore, revocation);

        await service.DeprovisionAndLockSubjectAsync(tenant, "workforce-entra", "user-1", "SCIM_DEPROVISION", "SCIM_PUT");

        revocation.Subjects.ShouldBe([new Sid("workforce-entra|user-1")]);
    }

    private sealed class StubRevocation(GrantRevocationResult result) : IGrantRevocationService
    {
        public List<Sid> Subjects { get; } = [];

        public Task<GrantRevocationResult> RevokeAllForSubjectAsync(TenantId tenant, Sid subject, string reason, CancellationToken ct = default)
        {
            Subjects.Add(subject);
            return Task.FromResult(result);
        }
    }
}
