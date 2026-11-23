namespace Autheris.Tests.Unit.ConsentRequests;

using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConsentApiEndpointSecurityTests
{
    [Fact]
    public async Task External_decision_from_unregistered_system_returns_404()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "req-1");

        // Case 1: completely unknown client_id
        var unknownClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", "unregistered-client", ExternalApiPermissions.ConsentDecide);
        var resp1 = await harness.DecideAsync(unknownClient, req.Id, "approve", "k-unreg-1", "\"r1\"");
        resp1.StatusCode.ShouldBe(StatusCodes.Status404NotFound);

        // Case 2: disabled external system
        var disabledSysId = harness.RegisterExternalSystem("tenant-a", "disabled-client", enabled: false);
        var reqForDisabled = harness.SeedExternalRequest("tenant-a", disabledSysId, "req-2");
        var disabledClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", "disabled-client", ExternalApiPermissions.ConsentDecide);
        var resp2 = await harness.DecideAsync(disabledClient, reqForDisabled.Id, "approve", "k-disabled-1", "\"r1\"");
        resp2.StatusCode.ShouldBe(StatusCodes.Status404NotFound);

        // Case 3: system registered in another tenant trying to decide tenant-a request
        harness.RegisterExternalSystem("tenant-b", "client-tenant-b", enabled: true);
        var otherTenantClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-b", "client-tenant-b", ExternalApiPermissions.ConsentDecide);
        var resp3 = await harness.DecideAsync(otherTenantClient, req.Id, "approve", "k-cross-1", "\"r1\"");
        resp3.StatusCode.ShouldBe(StatusCodes.Status404NotFound);

        harness.Denials.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Replay_with_same_idempotency_key_creates_no_second_grant()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-100");
        var client = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);

        var first = await harness.DecideAsync(client, req.Id, "approve", "idem-replay-1", "\"r1\"", approverSid: "S-1-5-21-APPROVER-1");
        first.StatusCode.ShouldBe(StatusCodes.Status200OK);
        first.Headers.ContainsKey("Idempotent-Replayed").ShouldBeFalse();
        harness.Store.Consents.Count.ShouldBe(1);

        // Second request replays exact same key + body with same original If-Match "r1" even though version incremented to r2
        var second = await harness.DecideAsync(client, req.Id, "approve", "idem-replay-1", "\"r1\"", approverSid: "S-1-5-21-APPROVER-1");
        second.StatusCode.ShouldBe(StatusCodes.Status200OK);
        second.Headers.TryGetValue("Idempotent-Replayed", out var vals).ShouldBeTrue();
        vals.ToString().ShouldBe("true");
        harness.Store.Consents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SameKeyDifferentBody_Returns422()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-101");
        var client = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);

        var first = await harness.DecideAsync(client, req.Id, "approve", "idem-mismatch-1", "\"r1\"", approverSid: "S-1-5-21-APPROVER-1");
        first.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var second = await harness.DecideAsync(client, req.Id, "reject", "idem-mismatch-1", "\"r1\"", reason: "Changed mind", approverSid: "S-1-5-21-APPROVER-1");
        second.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        second.Body.ShouldContain("IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task MissingIfMatch_Returns428()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-102");
        var client = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);

        var missingIfMatch = await harness.DecideAsync(client, req.Id, "approve", "idem-no-ifmatch", ifMatch: null, approverSid: "S-1-5-21-APPROVER-1");
        missingIfMatch.StatusCode.ShouldBe(StatusCodes.Status428PreconditionRequired);
        missingIfMatch.Body.ShouldContain("PRECONDITION_REQUIRED");

        var missingIdempotencyKey = await harness.DecideAsync(client, req.Id, "approve", idempotencyKey: null, ifMatch: "\"r1\"", approverSid: "S-1-5-21-APPROVER-1");
        missingIdempotencyKey.StatusCode.ShouldBe(StatusCodes.Status428PreconditionRequired);
        missingIdempotencyKey.Body.ShouldContain("IDEMPOTENCY_KEY_REQUIRED");
    }

    [Fact]
    public async Task StaleIfMatch_Returns412()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-103");
        var client = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);

        var stale = await harness.DecideAsync(client, req.Id, "approve", "idem-stale-1", ifMatch: "\"r99\"", approverSid: "S-1-5-21-APPROVER-1");
        stale.StatusCode.ShouldBe(StatusCodes.Status412PreconditionFailed);
    }

    [Fact]
    public async Task DelegatedUserToken_WithDecideScope_Returns403()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-104");

        // Delegated human user carrying scp=Autheris.Consent.Decide (and even DataSteward role) must receive 403
        var delegatedUser = ConsentApiTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-USER-99", ExternalApiPermissions.ConsentDecide, roles: "DataSteward");
        var resp = await harness.DecideAsync(delegatedUser, req.Id, "approve", "idem-delegated-1", "\"r1\"", approverSid: "S-1-5-21-USER-99");
        resp.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        // Also ensure delegated token without Approver role is denied
        var delegatedPlain = ConsentApiTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-USER-98", ExternalApiPermissions.ConsentDecide);
        var resp2 = await harness.DecideAsync(delegatedPlain, req.Id, "approve", "idem-delegated-2", "\"r1\"", approverSid: "S-1-5-21-USER-98");
        resp2.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task RateLimit_Decisions_Returns429()
    {
        await using var harness = ConsentApiTestHarness.Create(decisionsPerMinute: 2);
        var req1 = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-rl-1");
        var req2 = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-rl-2");
        var req3 = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-rl-3");
        var client = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);

        var r1 = await harness.DecideAsync(client, req1.Id, "approve", "rl-1", "\"r1\"", approverSid: "S-1-5-21-APPR-1");
        r1.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var r2 = await harness.DecideAsync(client, req2.Id, "approve", "rl-2", "\"r1\"", approverSid: "S-1-5-21-APPR-1");
        r2.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var r3 = await harness.DecideAsync(client, req3.Id, "approve", "rl-3", "\"r1\"", approverSid: "S-1-5-21-APPR-1");
        r3.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        r3.Headers.ContainsKey("Retry-After").ShouldBeTrue();
    }

    [Fact]
    public async Task All_ConsentApi_Routes_Require_Expected_Authorization_Policies_And_No_AllowAnonymous()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var endpoints = harness.GetMappedRouteEndpoints();
        endpoints.Count.ShouldBe(16);

        void AssertPolicy(string pattern, string method, string expectedPolicy)
        {
            var ep = endpoints.Single(e =>
                e.RoutePattern.RawText == pattern
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            ep.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldContain(a => a.Policy == expectedPolicy);
            ep.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
        }

        AssertPolicy("/api/v1/consent/requests", "GET", GatewayPolicies.ConsentRead);
        AssertPolicy("/api/v1/consent/requests/{id:guid}", "GET", GatewayPolicies.ConsentRead);
        AssertPolicy("/api/v1/consent/requests", "POST", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy("/api/v1/consent/requests/{id:guid}/decision", "POST", GatewayPolicies.ConsentDecide);
        AssertPolicy("/api/v1/consent/requests/{id:guid}/withdraw", "POST", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy("/api/v1/consent/events", "GET", GatewayPolicies.ConsentRead);
        AssertPolicy("/api/v1/consent/grants", "GET", GatewayPolicies.ConsentRead);
        AssertPolicy("/api/v1/consent/grants/{id:guid}", "GET", GatewayPolicies.ConsentRead);
        AssertPolicy("/api/v1/consent/grants/{id:guid}/revoke", "POST", GatewayPolicies.GrantRevoke);
        AssertPolicy("/api/v1/consent/grants/{id:guid}/renew", "POST", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy("/api/v1/consent/self-grants", "POST", GatewayPolicies.ItAdmin);
        AssertPolicy("/api/v1/consent/profile-assignments/{id:guid}/revoke", "POST", GatewayPolicies.GrantRevoke);
        AssertPolicy("/api/v1/consent/grant-records/proof", "GET", GatewayPolicies.GrantVerify);
        AssertPolicy("/api/v1/consent/grant-records/{seq:long}", "GET", GatewayPolicies.GrantVerify);
        AssertPolicy("/api/v1/consent/grant-records/verify", "POST", GatewayPolicies.GrantVerify);
        AssertPolicy("/api/v1/consent/grant-records/keys", "GET", GatewayPolicies.GrantVerify);
    }
}
