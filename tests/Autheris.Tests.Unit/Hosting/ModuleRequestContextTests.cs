namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Api.Hosting;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Security;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

/// <summary>The module request context is a 1:1 forwarder: every member returns what the host helper returns.</summary>
public sealed class ModuleRequestContextTests
{
    private readonly IModuleRequestContext _sut = new ModuleRequestContext();

    private static HttpContext ContextWith(params Claim[] claims)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return ctx;
    }

    private static SecurityPrincipalContext Principal(string tenant) => new()
    {
        UserSid = new Sid("S-1-5-21-CTX"),
        GroupSids = new HashSet<Sid>(),
        ResolvedTenant = new TenantId(tenant),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer",
    };

    [Fact]
    [Trait("Category", "Security")]
    public void Request_tenant_matches_endpoint_security()
    {
        var withPrincipal = new DefaultHttpContext();
        withPrincipal.Items[SecurityPrincipalContext.ItemKey] = Principal("tenant-a");
        _sut.GetRequestTenant(withPrincipal).ShouldBe(EndpointSecurity.GetRequestTenant(withPrincipal));

        var withItem = new DefaultHttpContext();
        withItem.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-b");
        _sut.GetRequestTenant(withItem).ShouldBe(EndpointSecurity.GetRequestTenant(withItem));
        _sut.GetRequestTenant(withItem).Value.ShouldBe("tenant-b");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Malformed_tenant_claim_fails_closed()
    {
        var ctx = ContextWith(new Claim("tenant_id", "not a valid tenant!!"));

        Should.Throw<Exception>(() => EndpointSecurity.GetRequestTenant(ctx));
        Should.Throw<Exception>(() => _sut.GetRequestTenant(ctx));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Malformed_tenant_claim_propagates_security_exception()
    {
        var ctx = ContextWith(new Claim("tenant_id", "not a valid tenant!!"));

        Exception expected = Should.Throw<Exception>(() => EndpointSecurity.GetRequestTenant(ctx));
        Exception actual = Should.Throw<Exception>(() => _sut.GetRequestTenant(ctx));

        actual.GetType().ShouldBe(expected.GetType());
        actual.Message.ShouldBe(expected.Message);
        (actual is SecurityException or GatewaySecurityException).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_context_matches_endpoint_security()
    {
        var ctx = new DefaultHttpContext();
        var principal = Principal("tenant-a");
        ctx.Items[SecurityPrincipalContext.ItemKey] = principal;

        _sut.GetSecurityContext(ctx).ShouldBeSameAs(EndpointSecurity.GetSecurityContext(ctx));
        _sut.GetSecurityContext(ctx).ShouldBeSameAs(principal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Caller_identity_matches_endpoint_security()
    {
        var anonymous = new DefaultHttpContext();
        var named = ContextWith(new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("sub", "user-1"));

        foreach (var ctx in new[] { anonymous, named })
        {
            _sut.GetCallerIdentity(ctx).ShouldBe(EndpointSecurity.GetCallerIdentity(ctx));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Admin_checks_match_endpoint_security()
    {
        var principals = new ClaimsPrincipal?[]
        {
            null,
            new ClaimsPrincipal(new ClaimsIdentity()),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "ClusterAdmin")], "test")),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "GovernanceAdmin")], "test")),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "ClusterAdmin")], "test", "name", "roles")),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Reader")], "test")),
        };

        foreach (var principal in principals)
        {
            _sut.IsCanonicalClusterAdmin(principal).ShouldBe(EndpointSecurity.IsCanonicalClusterAdmin(principal));
            _sut.IsGlobalGovernanceAdmin(principal).ShouldBe(EndpointSecurity.IsGlobalGovernanceAdmin(principal));
        }
    }

    [Fact]
    public async Task Body_over_limit_returns_413_problem()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 64)));

        var (body, error) = await _sut.TryReadBodyAsync(ctx.Request, 16, "Too large.", default);

        body.ShouldBeNull();
        error.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);

        var declared = new DefaultHttpContext();
        declared.Request.ContentLength = 1024;
        _sut.TryApplyBodyLimit(declared.Request, 16, "Too large.").ShouldBeOfType<ProblemHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);

        var wrapped = await _sut.WithBodyLimitAsync(
            () => throw new BadHttpRequestException("limit", StatusCodes.Status413PayloadTooLarge),
            "Too large.");
        wrapped.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public void Problem_results_match_api_problems()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/modules/x/y";

        Same(_sut.NotFound("missing", ctx), ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "missing", ctx));
        Same(_sut.BadRequest(ApiErrorCodes.BadRequest, "bad", ctx), ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "bad", ctx));
        Same(_sut.Forbidden(ctx), ApiProblems.Forbidden(httpContext: ctx));
        Same(_sut.ServiceUnavailable("down", ctx), ApiProblems.ServiceUnavailable(ApiErrorCodes.ServiceUnavailable, "down", ctx));
        Same(_sut.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "conflict", ctx), ApiProblems.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "conflict", ctx));
        Same(_sut.NotFound("missing"), ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "missing"));
    }

    private static void Same(IResult actual, IResult expected)
    {
        var a = actual.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        var e = expected.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        a.Status.ShouldBe(e.Status);
        a.Title.ShouldBe(e.Title);
        a.Detail.ShouldBe(e.Detail);
        a.Type.ShouldBe(e.Type);
        a.Instance.ShouldBe(e.Instance);
        a.Extensions.OrderBy(kv => kv.Key).ToList().ShouldBe(e.Extensions.OrderBy(kv => kv.Key).ToList());
    }
}
