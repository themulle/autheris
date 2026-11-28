namespace Autheris.Tests.Unit.ConsentRequests;

using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1161: observer external systems at the endpoints. An authorized observer (enabled observer system, service principal with
/// <c>Autheris.Consent.Read</c> and <c>Autheris.Consent.Observe</c>) lists and reads every request of its tenant and reads the event feed;
/// without the observe role it reads nothing; it never decides or revokes; a workflow system keeps its routed-only view.
/// </summary>
[Trait("Category", "Security")]
public sealed class ObserverEndpointSecurityTests
{
    private const string ObserverClient = "cockpit-observer";

    private static async Task<(ConsentApiTestHarness Harness, ConsentRequest Routed, System.Guid InternalId)> SeedAsync()
    {
        var harness = ConsentApiTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", ObserverClient, enabled: true, ExternalWorkflowSystemRole.Observer);

        // One request routed to the registered workflow system and one internal request submitted by a person.
        var routed = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-observer-1");
        var user = ConsentApiTestHarness.BuildUserPrincipal("tenant-a", "S-1-5-21-USER-1", "Developer");
        var created = await harness.CreateRequestAsync(
            user, new CreateConsentRequestBody("pg", "public", "orders", Justification: "Quarterly revenue report", DurationDays: 7));
        created.StatusCode.ShouldBe(StatusCodes.Status201Created);
        return (harness, routed, created.ReadJson().GetProperty("id").GetGuid());
    }

    private static System.Security.Claims.ClaimsPrincipal Observer(params string[] roles) =>
        ConsentApiTestHarness.BuildAppPrincipal("tenant-a", ObserverClient, roles);

    [Fact]
    public async Task Authorized_observer_lists_and_reads_every_request_of_its_tenant()
    {
        var (harness, routed, internalId) = await SeedAsync();
        await using var _ = harness;
        var observer = Observer(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve);

        var list = await harness.ListRequestsAsync(observer);
        list.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var ids = list.ReadJson().GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.ShouldContain(routed.Id);
        ids.ShouldContain(internalId);

        (await harness.GetRequestByIdAsync(observer, routed.Id)).StatusCode.ShouldBe(StatusCodes.Status200OK);
        (await harness.GetRequestByIdAsync(observer, internalId)).StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Observer_without_the_observe_role_reads_nothing()
    {
        var (harness, routed, internalId) = await SeedAsync();
        await using var _ = harness;
        harness.Store.AddEvent("tenant-a", 1, ConsentEventTypes.RequestCreated, internalId.ToString("D"), "{}", ConsentApiTestHarness.FixedNow);
        var observer = Observer(ExternalApiPermissions.ConsentRead);

        var list = await harness.ListRequestsAsync(observer);
        list.StatusCode.ShouldBe(StatusCodes.Status200OK);
        list.ReadJson().GetProperty("items").GetArrayLength().ShouldBe(0);

        (await harness.GetRequestByIdAsync(observer, routed.Id)).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await harness.GetRequestByIdAsync(observer, internalId)).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await harness.GetEventsAsync(observer)).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData("Developer")]
    [InlineData("GovernanceAdmin")]
    public async Task Person_never_reads_the_tenant_event_feed(string role)
    {
        // Issue #1284: the feed is for registered external systems only; a person saw every event of the tenant.
        var (harness, _, internalId) = await SeedAsync();
        await using var __ = harness;
        harness.Store.AddEvent("tenant-a", 1, ConsentEventTypes.RequestCreated, internalId.ToString("D"), "{}", ConsentApiTestHarness.FixedNow);

        var other = ConsentApiTestHarness.BuildUserPrincipal("tenant-a", "S-1-5-21-USER-2", role);
        var feed = await harness.GetEventsAsync(other);

        feed.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        feed.Body.ShouldNotContain(internalId.ToString("D"));
    }

    [Fact]
    public async Task Delegated_person_token_never_reads_the_tenant_event_feed()
    {
        var (harness, _, internalId) = await SeedAsync();
        await using var __ = harness;
        harness.Store.AddEvent("tenant-a", 1, ConsentEventTypes.RequestCreated, internalId.ToString("D"), "{}", ConsentApiTestHarness.FixedNow);

        var feed = await harness.GetEventsAsync(ConsentApiTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-USER-2", "access_as_user"));

        feed.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        feed.Body.ShouldNotContain(internalId.ToString("D"));
    }

    [Fact]
    public async Task Delegated_person_token_of_a_registered_client_never_reads_the_feed()
    {
        // The app registration of an observer can also serve person sign-in: its delegated tokens carry the same azp.
        var (harness, _, internalId) = await SeedAsync();
        await using var __ = harness;
        harness.Store.AddEvent("tenant-a", 1, ConsentEventTypes.RequestCreated, internalId.ToString("D"), "{}", ConsentApiTestHarness.FixedNow);
        var person = ConsentApiTestHarness.BuildDelegatedUserPrincipal(
            "tenant-a", "S-1-5-21-USER-2", "access_as_user", $"{ExternalApiPermissions.ConsentRead} {ExternalApiPermissions.ConsentObserve}");
        ((System.Security.Claims.ClaimsIdentity)person.Identity!).AddClaim(new System.Security.Claims.Claim("azp", ObserverClient));

        var feed = await harness.GetEventsAsync(person);

        feed.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        feed.Body.ShouldNotContain(internalId.ToString("D"));
    }

    [Fact]
    public async Task Authorized_observer_reads_the_event_feed()
    {
        var (harness, _, internalId) = await SeedAsync();
        await using var __ = harness;
        harness.Store.AddEvent("tenant-a", 1, ConsentEventTypes.RequestCreated, internalId.ToString("D"), "{}", ConsentApiTestHarness.FixedNow);

        var feed = await harness.GetEventsAsync(Observer(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve));
        feed.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Observer_never_decides_even_with_the_decide_role()
    {
        var (harness, routed, _) = await SeedAsync();
        await using var __ = harness;
        var observer = Observer(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve, ExternalApiPermissions.ConsentDecide);

        var decision = await harness.DecideAsync(observer, routed.Id, "approve", "idem-observer-1", "\"r1\"", approverSid: "S-1-5-21-APPR-1");
        decision.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        decision.Body.ShouldContain(ConsentDecisionErrorCodes.ObserverNotPermitted);
        harness.Denials.Denials.ShouldContain(d => d.Reason == ConsentDecisionDenialReasons.Observer);
        harness.Store.Requests.Single(r => r.Id == routed.Id).Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
    }

    [Fact]
    public async Task Observer_never_revokes_even_with_the_revoke_role()
    {
        await using var harness = ConsentApiTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", ObserverClient, enabled: true, ExternalWorkflowSystemRole.Observer);
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-observer-revoke");
        var decideClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);
        var decided = await harness.DecideAsync(decideClient, req.Id, "approve", "idem-observer-grant", "\"r1\"", approverSid: "S-1-5-21-APPR-1");
        decided.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var grantId = decided.ReadJson().GetProperty("activatedGrant").GetProperty("id").GetGuid();

        var observer = Observer(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve, ExternalApiPermissions.GrantsRevoke);
        var revoke = await harness.RevokeGrantAsync(observer, grantId, "Observer must not revoke", "rev-observer-1");
        revoke.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        revoke.Body.ShouldContain(ConsentDecisionErrorCodes.ObserverNotPermitted);
        harness.Denials.Denials.ShouldContain(d => d.Reason == ConsentDecisionDenialReasons.Observer);
    }

    [Fact]
    public async Task Workflow_system_keeps_its_routed_only_view()
    {
        var (harness, routed, internalId) = await SeedAsync();
        await using var _ = harness;
        var workflow = ConsentApiTestHarness.BuildAppPrincipal(
            "tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve);

        var ids = (await harness.ListRequestsAsync(workflow)).ReadJson().GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.ShouldBe([routed.Id]);
        (await harness.GetRequestByIdAsync(workflow, internalId)).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Disabled_observer_reads_nothing()
    {
        await using var harness = ConsentApiTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", ObserverClient, enabled: false, ExternalWorkflowSystemRole.Observer);
        var routed = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-observer-off");
        var observer = Observer(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve);

        (await harness.ListRequestsAsync(observer)).ReadJson().GetProperty("items").GetArrayLength().ShouldBe(0);
        (await harness.GetRequestByIdAsync(observer, routed.Id)).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await harness.GetEventsAsync(observer)).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }
}
