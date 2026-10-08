namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.GraphQL.Subscriptions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

#pragma warning disable CS0618

public sealed class WebSocketAuthInterceptorTests
{
    [Fact]
    public void ExtractToken_WithBearerPrefix_ExtractsCleanToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer my-secret-jwt-token"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("my-secret-jwt-token");
    }

    [Fact]
    public void ExtractToken_WithApiKeyHeader_ExtractsToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["x-api-key"] = "enterprise-api-key-99"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("enterprise-api-key-99");
    }

    [Fact]
    public void ExtractToken_EmptyOrMissing_ReturnsNull()
    {
        var dict = new Dictionary<string, object?>
        {
            ["unrelated"] = "value"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBeNull();
    }

    [Fact]
    public void ExtractToken_WithMixedCaseAuthorization_ExtractsCleanToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["authorization"] = "Bearer token-abc"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("token-abc");
    }

    #region R-GQL-8 WebSocket Identity & SID Security Tests

    private static (HotChocolate.AspNetCore.Subscriptions.ISocketSession Session, HotChocolate.AspNetCore.Subscriptions.Protocols.IOperationMessagePayload Payload, DefaultHttpContext HttpContext)
        CreateSocketSession(string token)
    {
        var httpContext = new DefaultHttpContext();
        var session = NSubstitute.Substitute.For<HotChocolate.AspNetCore.Subscriptions.ISocketSession>();
        session.Connection.HttpContext.Returns(httpContext);

        using var doc = System.Text.Json.JsonDocument.Parse($"{{\"authorization\":\"Bearer {token}\"}}");
        var payload = NSubstitute.Substitute.For<HotChocolate.AspNetCore.Subscriptions.Protocols.IOperationMessagePayload>();
        payload.Payload.Returns(doc.RootElement.Clone());

        return (session, payload, httpContext);
    }

    private static Autheris.Application.Interfaces.ISocketTokenValidator CreateValidator(string token, ClaimsPrincipal? principal)
    {
        var validator = NSubstitute.Substitute.For<Autheris.Application.Interfaces.ISocketTokenValidator>();
        validator.ValidateTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((principal != null, principal)));
        return validator;
    }

    [Fact]
    public async Task OnConnectAsync_TokenWithoutSid_RejectsWithMissingSidMessage()
    {
        // Arrange: Token principal has tenant and name, but NO SID claim
        var claims = new List<Claim>
        {
            new("tenant_id", "tenant-a"),
            new(ClaimTypes.Name, "alice-without-sid")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var validator = CreateValidator("token-no-sid", principal);
        var interceptor = new WebSocketAuthInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketAuthInterceptor>.Instance, validator);

        var (session, payload, _) = CreateSocketSession("token-no-sid");

        // Act
        var result = await interceptor.OnConnectAsync(session, payload);

        // Assert: Fail-closed rejection
        result.Accepted.ShouldBeFalse();
        result.Message.ShouldBe("Token missing subject identifier (SID)");
    }

    [Fact]
    public async Task OnConnectAsync_HttpAuthenticated_SameTenant_DifferentSid_RejectsWithCrossSubjectMismatch()
    {
        // Arrange: HTTP connection was authenticated as Alice
        var httpClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("tenant_id", "tenant-a")
        };
        var httpUser = new ClaimsPrincipal(new ClaimsIdentity(httpClaims, "Cookie"));

        // WebSocket token is for Bob in the SAME tenant
        var tokenClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-BOB"),
            new("tenant_id", "tenant-a")
        };
        var tokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(tokenClaims, "Bearer"));
        var validator = CreateValidator("token-bob", tokenPrincipal);
        var interceptor = new WebSocketAuthInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketAuthInterceptor>.Instance, validator);

        var (session, payload, httpContext) = CreateSocketSession("token-bob");
        httpContext.User = httpUser;

        // Act
        var result = await interceptor.OnConnectAsync(session, payload);

        // Assert: Even within the same tenant, cross-subject impersonation must be rejected
        result.Accepted.ShouldBeFalse();
        result.Message.ShouldBe("Cross-subject identity mismatch");
    }

    [Fact]
    public async Task OnConnectAsync_HttpAuthenticated_SameTenant_SameSid_Succeeds()
    {
        // Arrange: HTTP connection authenticated as Alice
        var httpClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("tenant_id", "tenant-a")
        };
        var httpUser = new ClaimsPrincipal(new ClaimsIdentity(httpClaims, "Cookie"));

        // WebSocket token is also for Alice in tenant-a
        var tokenClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("tenant_id", "tenant-a")
        };
        var tokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(tokenClaims, "Bearer"));
        var validator = CreateValidator("token-alice", tokenPrincipal);
        var interceptor = new WebSocketAuthInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketAuthInterceptor>.Instance, validator);

        var (session, payload, httpContext) = CreateSocketSession("token-alice");
        httpContext.User = httpUser;

        // Act
        var result = await interceptor.OnConnectAsync(session, payload);

        // Assert
        result.Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task OnConnectAsync_HttpAuthenticated_DifferentTenant_RejectsWithCrossTenantMismatch()
    {
        // Arrange: HTTP authenticated as Alice in tenant-a
        var httpClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("tenant_id", "tenant-a")
        };
        var httpUser = new ClaimsPrincipal(new ClaimsIdentity(httpClaims, "Cookie"));

        // WebSocket token is for Alice but in tenant-b
        var tokenClaims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("tenant_id", "tenant-b")
        };
        var tokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(tokenClaims, "Bearer"));
        var validator = CreateValidator("token-alice-b", tokenPrincipal);
        var interceptor = new WebSocketAuthInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketAuthInterceptor>.Instance, validator);

        var (session, payload, httpContext) = CreateSocketSession("token-alice-b");
        httpContext.User = httpUser;

        // Act
        var result = await interceptor.OnConnectAsync(session, payload);

        // Assert
        result.Accepted.ShouldBeFalse();
        result.Message.ShouldBe("Cross-tenant token mismatch");
    }

    #endregion
}
