namespace Autheris.Tests.Unit.ConsentRequests;

using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Api.Endpoints;
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
    public async Task StaleExpectedVersion_Returns409()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-103");
        var client = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);

        var stale = await harness.DecideAsync(client, req.Id, "approve", "idem-stale-1", ifMatch: "\"r99\"", approverSid: "S-1-5-21-APPROVER-1");
        stale.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
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
        endpoints.Count.ShouldBe(23);

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
        AssertPolicy("/api/v1/consent/inbox", "GET", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy("/api/v1/governance/containers/{kind}/{id:guid}/grants", "GET", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy("/api/v1/governance/deputies", "GET", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy("/api/v1/governance/deputies/sources/{source}", "PUT", GatewayPolicies.DeputySync);
        AssertPolicy("/api/v1/governance/deputies/sources/{source}/relations/{externalId}", "PUT", GatewayPolicies.DeputySync);
        AssertPolicy("/api/v1/governance/deputies/sources/{source}/relations/{externalId}", "DELETE", GatewayPolicies.DeputySync);
        AssertPolicy("/api/v1/consent/grants", "GET", GatewayPolicies.ConsentRead);
        AssertPolicy("/api/v1/consent/grants/{id:guid}", "GET", GatewayPolicies.ConsentRead);
        AssertPolicy("/api/v1/consent/grants/{id:guid}/revoke", "POST", GatewayPolicies.GrantRevoke);
        AssertPolicy("/api/v1/consent/catalog-grants/{id:guid}/revoke", "POST", GatewayPolicies.GrantRevoke);
        AssertPolicy("/api/v1/consent/grants/{id:guid}/renew", "POST", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy("/api/v1/consent/self-grants", "POST", GatewayPolicies.ItAdmin);
        AssertPolicy("/api/v1/consent/profile-assignments/{id:guid}/revoke", "POST", GatewayPolicies.GrantRevoke);
        AssertPolicy("/api/v1/consent/grant-records/proof", "GET", GatewayPolicies.GrantVerify);
        AssertPolicy("/api/v1/consent/grant-records/{seq:long}", "GET", GatewayPolicies.GrantVerify);
        AssertPolicy("/api/v1/consent/grant-records/verify", "POST", GatewayPolicies.GrantVerify);
        AssertPolicy("/api/v1/consent/grant-records/keys", "GET", GatewayPolicies.GrantVerify);
    }

    [Fact]
    public void No_selfGrant_field_in_any_consent_request_dto()
    {
        var dtoTypes = new[]
        {
            typeof(CreateConsentRequestBody), typeof(CreateConsentRequestTarget), typeof(ConsentDecisionBody),
            typeof(ConsentDecisionApiResponse), typeof(ConsentRequestDto)
        };

        foreach (var type in dtoTypes)
        {
            type.GetProperties().ShouldNotContain(
                p => p.Name.Contains("SelfGrant", System.StringComparison.OrdinalIgnoreCase),
                $"{type.Name} must not accept or expose a self-grant flag");
        }
    }

    [Fact]
    public async Task ExpectedVersion_is_required_for_a_decision()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-ev-1");
        var client = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);

        var missing = await harness.DecideAsync(client, req.Id, "approve", "idem-ev-1", ifMatch: null, approverSid: "S-1-5-21-APPROVER-1");
        missing.StatusCode.ShouldBe(StatusCodes.Status428PreconditionRequired);

        var invalid = await harness.DecideAsync(client, req.Id, "approve", "idem-ev-2", ifMatch: null, approverSid: "S-1-5-21-APPROVER-1", expectedVersion: 0);
        invalid.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        var given = await harness.DecideAsync(client, req.Id, "approve", "idem-ev-3", ifMatch: null, approverSid: "S-1-5-21-APPROVER-1", expectedVersion: 1);
        given.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Invisible_target_same_response_as_missing()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var user = ConsentApiTestHarness.BuildUserPrincipal("tenant-a", "S-1-5-21-USER-7", "Developer");

        var missing = await harness.CreateRequestAsync(user, new CreateConsentRequestBody("pg", "public", "does_not_exist", Justification: "Quarterly revenue report", DurationDays: 7));
        var malformed = await harness.CreateRequestAsync(user, new CreateConsentRequestBody("pg", "public", "bad.name", Justification: "Quarterly revenue report", DurationDays: 7));
        var hidden = await harness.CreateRequestAsync(user, new CreateConsentRequestBody("pg", "public", "hidden_payroll", Justification: "Quarterly revenue report", DurationDays: 7));

        missing.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        malformed.StatusCode.ShouldBe(missing.StatusCode);
        Normalize(malformed.Body).ShouldBe(Normalize(missing.Body));
        hidden.StatusCode.ShouldBe(missing.StatusCode);
        Normalize(hidden.Body.Replace("hidden_payroll", "does_not_exist")).ShouldBe(Normalize(missing.Body));
        harness.Store.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Foreign_tenant_request_same_as_missing()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var foreign = harness.SeedExternalRequest("tenant-b", harness.RegisteredSystemId, "ext-foreign-1");
        var decider = ConsentApiTestHarness.BuildUserPrincipal("tenant-a", "S-1-5-21-GOV-1", "GovernanceAdmin");

        var foreignResp = await harness.DecideAsync(decider, foreign.Id, "approve", "idem-foreign-1", ifMatch: null, expectedVersion: 1);
        var missingResp = await harness.DecideAsync(decider, Guid.NewGuid(), "approve", "idem-foreign-1", ifMatch: null, expectedVersion: 1);

        missingResp.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        foreignResp.StatusCode.ShouldBe(missingResp.StatusCode);
        Normalize(foreignResp.Body.Replace(foreign.Id.ToString("D"), "<id>")).ShouldBe(Normalize(missingResp.Body));
    }

    [Fact]
    public async Task Reason_codes_leak_no_third_party_data()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var request = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-ref-leak-1");
        // The requester holds the governance role and tries to decide their own request: separation of duties denies.
        var requester = ConsentApiTestHarness.BuildUserPrincipal("tenant-a", request.RequesterSid.Value, "GovernanceAdmin");

        var denied = await harness.DecideAsync(requester, request.Id, "approve", "idem-leak-1", ifMatch: null, expectedVersion: 1);

        denied.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        denied.Body.ShouldContain("Consent decision is not permitted.");
        // Neither the requester, the grantee, the target nor any group or grant of a third party is part of the response.
        denied.Body.ShouldNotContain(request.RequesterSid.Value);
        denied.Body.ShouldNotContain(request.RequestedGranteeRef);
        denied.Body.ShouldNotContain("orders");
        denied.Body.ShouldNotContain("S-1-5-21", Case.Insensitive);
        denied.Body.ShouldNotContain("group", Case.Insensitive);
    }

    private static string Normalize(string body) =>
        Regex.Replace(Regex.Replace(body, "\"(correlationId|traceId)\":\"[^\"]*\"", "\"$1\":\"<id>\""), "[0-9a-fA-F]{32}|[0-9a-fA-F-]{36}", "<id>");
}
