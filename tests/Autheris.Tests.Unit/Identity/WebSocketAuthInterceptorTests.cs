namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;
using Autheris.GraphQL.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static Autheris.Application.Common.Interfaces.ISocketTokenValidator CreateValidator(string token, ClaimsPrincipal? principal)
    {
        var validator = NSubstitute.Substitute.For<Autheris.Application.Common.Interfaces.ISocketTokenValidator>();
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

    #region DP-13 WebSocket Group Membership Revalidation Security Tests

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class StubGroupSidResolver(Func<ValueTask<GroupResolution>> resolve) : IGroupSidResolver
    {
        public int Calls { get; private set; }

        public ValueTask<GroupResolution> ResolveAsync(
            ClaimsPrincipal normalized,
            IdentityRealm realm,
            TenantId tenant,
            string scheme,
            CancellationToken ct)
        {
            Calls++;
            return resolve();
        }
    }

    private sealed class StubTokenRevocationService(bool revoked = false) : ITokenRevocationService
    {
        public ValueTask<bool> IsRevokedAsync(ClaimsPrincipal principal, CancellationToken ct = default) =>
            ValueTask.FromResult(revoked);

        public Task RevokeAsync(string identifier, DateTimeOffset expiresAt, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static IdentityRealmRegistry CreateRealmRegistry(
        string realmId = "realm-ws",
        RealmGroupSource groupSource = RealmGroupSource.Resolver)
    {
        var registry = new IdentityRealmRegistry();
        registry.RegisterRealm(new IdentityRealm(
            realmId,
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(new TenantId("tenant-a")),
            groupSource: groupSource,
            provisioning: RealmProvisioningMode.Scim,
            issuers: ["https://sts.example.com/"]));
        return registry;
    }

    private static ClaimsPrincipal CreatePipelinePrincipal(params string[] canonicalGroupSids)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ALICE"),
            new("oid", "alice-oid"),
            new("tenant_id", "tenant-a"),
            new("autheris:realm", "realm-ws"),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        };

        foreach (var sid in canonicalGroupSids)
        {
            claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(sid));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Group_removal_closes_session_on_revalidation()
    {
        const string groupA = "S-1-5-21-1-2-3-1001";
        const string groupB = "S-1-5-21-1-2-3-1002";

        var currentGroups = new HashSet<Sid> { new(groupA), new(groupB) };
        var groupResolver = new StubGroupSidResolver(() => ValueTask.FromResult(
            new GroupResolution(currentGroups.ToHashSet(), GroupResolutionStatus.Complete, "resolver", DateTimeOffset.UtcNow.AddMinutes(5))));

        var principal = CreatePipelinePrincipal(groupA, groupB);
        var validator = CreateValidator("token-ws-1", principal);
        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator,
            revocationService: new StubTokenRevocationService(revoked: false),
            realmRegistry: CreateRealmRegistry(),
            groupSidResolver: groupResolver);

        var (session, payload, httpContext) = CreateSocketSession("token-ws-1");
        var lifetime = new RecordingLifetimeFeature();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        var status = await interceptor.OnConnectAsync(session, payload);
        status.Accepted.ShouldBeTrue();

        var watch = httpContext.Items[WebSocketAuthInterceptor.RevocationWatchItemKey]
            .ShouldBeOfType<WebSocketAuthInterceptor.RevocationWatch>();
        try
        {
            // First tick: groups unchanged -> stays open
            (await watch.CheckAsync()).ShouldBeFalse();
            lifetime.Aborted.ShouldBeFalse();

            // Simulate group removal
            currentGroups = [new(groupA)];

            // Second tick: canonical group SIDs changed -> closes immediately without swapping claims
            (await watch.CheckAsync()).ShouldBeTrue();
            lifetime.Aborted.ShouldBeTrue();
            httpContext.User.GetGroupSids().Count.ShouldBe(2);
        }
        finally
        {
            watch.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Unchanged_groups_keep_session()
    {
        const string groupA = "S-1-5-21-1-2-3-1001";
        const string groupB = "S-1-5-21-1-2-3-1002";

        var groupResolver = new StubGroupSidResolver(() => ValueTask.FromResult(
            new GroupResolution(
                new HashSet<Sid> { new(groupB), new(groupA) },
                GroupResolutionStatus.Complete,
                "resolver",
                DateTimeOffset.UtcNow.AddMinutes(5))));

        var principal = CreatePipelinePrincipal(groupA, groupB);
        var validator = CreateValidator("token-ws-2", principal);
        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator,
            revocationService: new StubTokenRevocationService(revoked: false),
            realmRegistry: CreateRealmRegistry(),
            groupSidResolver: groupResolver);

        var (session, payload, httpContext) = CreateSocketSession("token-ws-2");
        var lifetime = new RecordingLifetimeFeature();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        var status = await interceptor.OnConnectAsync(session, payload);
        status.Accepted.ShouldBeTrue();

        var watch = httpContext.Items[WebSocketAuthInterceptor.RevocationWatchItemKey]
            .ShouldBeOfType<WebSocketAuthInterceptor.RevocationWatch>();
        try
        {
            (await watch.CheckAsync()).ShouldBeFalse();
            (await watch.CheckAsync()).ShouldBeFalse();
            lifetime.Aborted.ShouldBeFalse();
            groupResolver.Calls.ShouldBe(2);
        }
        finally
        {
            watch.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Group_resolution_failure_closes_session()
    {
        const string groupA = "S-1-5-21-1-2-3-1001";

        var groupResolver = new StubGroupSidResolver(() => ValueTask.FromResult(
            new GroupResolution(
                new HashSet<Sid>(),
                GroupResolutionStatus.Degraded,
                "resolver",
                DateTimeOffset.UtcNow.AddMinutes(5))));

        var principal = CreatePipelinePrincipal(groupA);
        var validator = CreateValidator("token-ws-3", principal);
        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator,
            realmRegistry: CreateRealmRegistry(),
            groupSidResolver: groupResolver);

        var (session, payload, httpContext) = CreateSocketSession("token-ws-3");
        var lifetime = new RecordingLifetimeFeature();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        var status = await interceptor.OnConnectAsync(session, payload);
        status.Accepted.ShouldBeTrue();

        var watch = httpContext.Items[WebSocketAuthInterceptor.RevocationWatchItemKey]
            .ShouldBeOfType<WebSocketAuthInterceptor.RevocationWatch>();
        try
        {
            // Degraded status on revalidation closes fail-closed immediately (first tick)
            (await watch.CheckAsync()).ShouldBeTrue();
            lifetime.Aborted.ShouldBeTrue();
        }
        finally
        {
            watch.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Group_resolution_exception_closes_session()
    {
        const string groupA = "S-1-5-21-1-2-3-1001";

        var groupResolver = new StubGroupSidResolver(() =>
            ValueTask.FromException<GroupResolution>(new InvalidOperationException("directory unreachable")));

        var principal = CreatePipelinePrincipal(groupA);
        var validator = CreateValidator("token-ws-4", principal);
        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator,
            realmRegistry: CreateRealmRegistry(),
            groupSidResolver: groupResolver);

        var (session, payload, httpContext) = CreateSocketSession("token-ws-4");
        var lifetime = new RecordingLifetimeFeature();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        var status = await interceptor.OnConnectAsync(session, payload);
        status.Accepted.ShouldBeTrue();

        var watch = httpContext.Items[WebSocketAuthInterceptor.RevocationWatchItemKey]
            .ShouldBeOfType<WebSocketAuthInterceptor.RevocationWatch>();
        try
        {
            (await watch.CheckAsync()).ShouldBeTrue();
            lifetime.Aborted.ShouldBeTrue();
        }
        finally
        {
            watch.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Plugin_enriched_groups_do_not_keep_session_alive()
    {
        const string canonicalGroup = "S-1-5-21-1-2-3-1001";
        const string pluginEnrichedGroup = "S-1-5-21-1-2-3-9999";

        var principal = CreatePipelinePrincipal(canonicalGroup);
        // Simulate a control-plane plugin adding an unverified GroupSid claim (non-pipeline issuer)
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.GroupSid, pluginEnrichedGroup, ClaimValueTypes.String, "plugin-enricher"));

        var currentCanonicalGroups = new HashSet<Sid> { new(canonicalGroup) };
        var groupResolver = new StubGroupSidResolver(() => ValueTask.FromResult(
            new GroupResolution(currentCanonicalGroups.ToHashSet(), GroupResolutionStatus.Complete, "resolver", DateTimeOffset.UtcNow.AddMinutes(5))));

        var validator = CreateValidator("token-ws-5", principal);
        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator,
            realmRegistry: CreateRealmRegistry(),
            groupSidResolver: groupResolver);

        var (session, payload, httpContext) = CreateSocketSession("token-ws-5");
        var lifetime = new RecordingLifetimeFeature();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        var status = await interceptor.OnConnectAsync(session, payload);
        status.Accepted.ShouldBeTrue();

        // Also attach plugin ControlPlaneEnrichment to the SecurityPrincipalContext stored on HttpContext
        if (httpContext.Items[SecurityPrincipalContext.ItemKey] is SecurityPrincipalContext ctx)
        {
            httpContext.Items[SecurityPrincipalContext.ItemKey] = ctx with
            {
                ControlPlaneEnrichment = new ControlPlaneEnrichment(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PluginAdmin" },
                    new HashSet<Sid> { new(pluginEnrichedGroup) })
            };
        }

        var watch = httpContext.Items[WebSocketAuthInterceptor.RevocationWatchItemKey]
            .ShouldBeOfType<WebSocketAuthInterceptor.RevocationWatch>();
        try
        {
            (await watch.CheckAsync()).ShouldBeFalse();
            lifetime.Aborted.ShouldBeFalse();

            // Canonical group is removed; only plugin-enriched SID remains
            currentCanonicalGroups = [];

            (await watch.CheckAsync()).ShouldBeTrue();
            lifetime.Aborted.ShouldBeTrue();
        }
        finally
        {
            watch.Dispose();
        }
    }

    #endregion
}
