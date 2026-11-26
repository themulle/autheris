namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Grants.Records;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConsentApiGrantAndRequestSecurityTests
{
    [Fact]
    public async Task Requests_Create_Read_ETag_And_Withdraw_EnforceTenantAndPrincipalRules()
    {
        await using var harness = ConsentApiTestHarness.Create();

        // App-only service principal cannot submit access requests -> 403
        var appClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentRead);
        var appPost = await harness.CreateRequestAsync(
            appClient,
            new CreateConsentRequestBody("pg", "public", "orders", Justification: "Needs analytics read", DurationDays: 7));
        appPost.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        // Interactive user submits request -> 201 Created + ETag "r1"
        var userClient = ConsentApiTestHarness.BuildUserPrincipal("tenant-a", "S-1-5-21-USER-1", "Developer");
        var createResp = await harness.CreateRequestAsync(
            userClient,
            new CreateConsentRequestBody("pg", "public", "orders", Justification: "Quarterly revenue report", DurationDays: 7));
        createResp.StatusCode.ShouldBe(StatusCodes.Status201Created);
        createResp.Headers.ETag.ToString().ShouldBe("\"r1\"");

        var createdId = createResp.ReadJson().GetProperty("id").GetGuid();

        // Another plain user in same tenant cannot view -> 404
        var otherUser = ConsentApiTestHarness.BuildUserPrincipal("tenant-a", "S-1-5-21-USER-2", "Developer");
        var otherGet = await harness.GetRequestByIdAsync(otherUser, createdId);
        otherGet.StatusCode.ShouldBe(StatusCodes.Status404NotFound);

        // Cross-tenant admin cannot see another tenant's request -> 404
        var otherTenantUser = ConsentApiTestHarness.BuildUserPrincipal("tenant-b", "S-1-5-21-ADMIN-B", "GovernanceAdmin");
        var crossTenantGet = await harness.GetRequestByIdAsync(otherTenantUser, createdId);
        crossTenantGet.StatusCode.ShouldBe(StatusCodes.Status404NotFound);

        // Owner reads -> 200 with ETag "r1"
        var ownerGet = await harness.GetRequestByIdAsync(userClient, createdId);
        ownerGet.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ownerGet.Headers.ETag.ToString().ShouldBe("\"r1\"");

        // Owner withdraws -> 200
        var withdraw = await harness.WithdrawRequestAsync(userClient, createdId);
        withdraw.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Events_ExpiredCursor_Returns410_And_ValidCursorReturnsEvents()
    {
        await using var harness = ConsentApiTestHarness.Create();
        harness.Store.AddEvent("tenant-a", 10, ConsentEventTypes.RequestCreated, "S-1-5-21-USER-1", "{\"event\":\"created\"}", ConsentApiTestHarness.FixedNow, harness.RegisteredSystemId);

        var readerClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentRead);
        var expiredResp = await harness.GetEventsAsync(readerClient, since: 1);
        expiredResp.StatusCode.ShouldBe(StatusCodes.Status410Gone);
        expiredResp.Body.ShouldContain("CURSOR_EXPIRED");

        var validResp = await harness.GetEventsAsync(readerClient, since: 9);
        validResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        validResp.ReadJson().GetProperty("events").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Grants_Revoke_EnforcesIdempotency_AlreadyEnded409_And_CrossSystem404()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-revoke-1");
        var decideClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);
        var decideResp = await harness.DecideAsync(decideClient, req.Id, "approve", "idem-make-grant", "\"r1\"", approverSid: "S-1-5-21-APPR-1");
        decideResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var grantId = decideResp.ReadJson().GetProperty("activatedGrant").GetProperty("id").GetGuid();

        // Missing Idempotency-Key -> 428
        var revokeClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.GrantsRevoke);
        var missingKeyResp = await harness.RevokeGrantAsync(revokeClient, grantId, "Access no longer needed", idempotencyKey: null);
        missingKeyResp.StatusCode.ShouldBe(StatusCodes.Status428PreconditionRequired);

        // Another registered external system cannot revoke -> 404
        harness.RegisterExternalSystem("tenant-a", "other-client", enabled: true);
        var otherSystemClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", "other-client", ExternalApiPermissions.GrantsRevoke);
        var otherRevoke = await harness.RevokeGrantAsync(otherSystemClient, grantId, "Access no longer needed", "rev-other-1");
        otherRevoke.StatusCode.ShouldBe(StatusCodes.Status404NotFound);

        // Owning external system revokes -> 200
        var firstRevoke = await harness.RevokeGrantAsync(revokeClient, grantId, "Access no longer needed", "rev-own-1");
        firstRevoke.StatusCode.ShouldBe(StatusCodes.Status200OK);

        // Replay same key + reason -> 200 Idempotent-Replayed: true
        var replayRevoke = await harness.RevokeGrantAsync(revokeClient, grantId, "Access no longer needed", "rev-own-1");
        replayRevoke.StatusCode.ShouldBe(StatusCodes.Status200OK);
        replayRevoke.Headers.ContainsKey("Idempotent-Replayed").ShouldBeTrue();

        // New key after grant already revoked -> 409 GRANT_ALREADY_ENDED
        var alreadyEnded = await harness.RevokeGrantAsync(revokeClient, grantId, "Second revoke attempt", "rev-own-2");
        alreadyEnded.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        alreadyEnded.Body.ShouldContain("GRANT_ALREADY_ENDED");

        // Issue #1170: the revocation appends grant.revoked in its own transaction; the endpoint adds none, so the revoke, its replay
        // and the refused second revoke leave exactly one event, keyed by the grant id.
        harness.Store.Events.Where(e => e.Type == ConsentEventTypes.GrantRevoked).ShouldHaveSingleItem().SubjectId.ShouldBe(grantId.ToString("D"));
    }

    [Fact]
    public async Task GrantRecords_Proof_Seq_Verify_Keys_And_PermissionPolicies_AreEnforced()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = harness.SeedExternalRequest("tenant-a", harness.RegisteredSystemId, "ext-proof-1");
        var decideClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.ConsentDecide);
        var decideResp = await harness.DecideAsync(decideClient, req.Id, "approve", "idem-proof-1", "\"r1\"", approverSid: "S-1-5-21-APPR-1");
        decideResp.StatusCode.ShouldBe(StatusCodes.Status200OK);

        // Service principal with only Catalog.Read is denied on ConsentRead and GrantVerify endpoints (403)
        var catalogOnlyClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.CatalogRead);
        (await harness.ListRequestsAsync(catalogOnlyClient)).StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await harness.EvaluateProofAsync(catalogOnlyClient)).StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        // Service principal with Grants.Verify can read proof, seq, verify range, and keys
        var verifyClient = ConsentApiTestHarness.BuildAppPrincipal("tenant-a", harness.RegisteredClientId, ExternalApiPermissions.GrantsVerify);
        var proofResp = await harness.EvaluateProofAsync(verifyClient);
        proofResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var proofJson = proofResp.ReadJson();
        proofJson.GetProperty("verified").GetBoolean().ShouldBeTrue();
        proofJson.GetProperty("headSeq").GetInt64().ShouldBe(1L);

        var seqResp = await harness.GetGrantRecordBySeqAsync(verifyClient, 1);
        seqResp.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var rangeVerifyResp = await harness.VerifyGrantRecordsRangeAsync(verifyClient, new GrantRecordVerifyRequest(FromSeq: 1, ToSeq: 1));
        rangeVerifyResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        rangeVerifyResp.ReadJson().GetProperty("isValid").GetBoolean().ShouldBeTrue();

        var keysResp = await harness.GetGrantRecordKeysAsync(verifyClient);
        keysResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var keysJson = keysResp.ReadJson();
        keysJson.GetProperty("keys").GetArrayLength().ShouldBeGreaterThan(0);
        keysJson.GetProperty("keys")[0].GetProperty("kty").GetString().ShouldBe("EC");
        keysJson.GetProperty("keys")[0].GetProperty("crv").GetString().ShouldBe("P-256");

        // Authorization policy checks for CatalogRead vs CatalogReadAdmin
        var catalogReader = ConsentApiTestHarness.BuildPrincipal("app", "tenant-a", "client-cat", roles: [ExternalApiPermissions.CatalogRead]);
        var catalogAdmin = ConsentApiTestHarness.BuildPrincipal("app", "tenant-a", "client-cat-admin", roles: [ExternalApiPermissions.CatalogReadAdmin]);
        (await harness.AuthorizeAsync(catalogReader, GatewayPolicies.CatalogRead)).Succeeded.ShouldBeTrue();
        (await harness.AuthorizeAsync(catalogReader, GatewayPolicies.CatalogReadAdmin)).Succeeded.ShouldBeFalse();
        (await harness.AuthorizeAsync(catalogAdmin, GatewayPolicies.CatalogRead)).Succeeded.ShouldBeTrue();
        (await harness.AuthorizeAsync(catalogAdmin, GatewayPolicies.CatalogReadAdmin)).Succeeded.ShouldBeTrue();
    }
}
