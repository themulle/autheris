namespace Autheris.Tests.Unit.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

/// <summary>Issue #1173: revoking a profile assignment appends one grant.revoked per revoked consent, after all other writes.</summary>
[Trait("Category", "Security")]
public sealed class ProfileAssignmentRevocationEventTests
{
    private static readonly Sid Actor = new("S-1-5-21-ADMIN");

    private static Consent SeedConsent(ProfileAssignmentTests.Fixture fx, Guid assignmentId, string name, TenantId? tenant = null)
    {
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant ?? ProfileAssignmentTests.Tenant,
            TableIdentifier = new TableIdentifier("pg", "sales", name),
            GranteeType = GranteeType.User,
            GranteeSid = ProfileAssignmentTests.Requester,
            ProfileAssignmentId = assignmentId,
            ProfileItemNo = 1,
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            WorkflowMode = ConsentWorkflowMode.Internal
        };
        fx.Consents.Add(consent);
        return consent;
    }

    [Fact]
    public async Task Revocation_appends_one_grant_revoked_per_revoked_consent_after_all_writes()
    {
        var fx = new ProfileAssignmentTests.Fixture();
        var assignment = fx.SeedApprovedAssignment();
        var a = SeedConsent(fx, assignment.Id, "orders");
        var b = SeedConsent(fx, assignment.Id, "items");
        fx.CallLog.Clear();

        var count = await fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, assignment.Id, Actor, "left the team");

        count.ShouldBe(2);
        var batch = fx.AppendedRevokedEvents.ShouldHaveSingleItem();
        batch.Select(g => g.ConsentId).ShouldBe([a.Id, b.Id]);
        batch.Select(g => g.Table).ShouldBe([a.TableIdentifier, b.TableIdentifier]);
        var appendAt = fx.CallLog.IndexOf("append");
        fx.CallLog.LastIndexOf("mark").ShouldBeLessThan(appendAt);
        fx.CallLog.LastIndexOf("ledger").ShouldBeLessThan(appendAt);
        fx.CallLog.LastIndexOf("epoch").ShouldBeLessThan(appendAt);
        fx.CallLog.LastIndexOf("audit").ShouldBeLessThan(appendAt);
        fx.CallLog.Last().ShouldBe("commit");
        fx.CallLog.IndexOf("commit").ShouldBeGreaterThan(appendAt);
        fx.InvalidatedAfterCommit.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Revocation_skips_a_consent_whose_conditional_update_lost_the_race()
    {
        var fx = new ProfileAssignmentTests.Fixture();
        var assignment = fx.SeedApprovedAssignment();
        var winner = SeedConsent(fx, assignment.Id, "orders");
        var loser = SeedConsent(fx, assignment.Id, "items");
        fx.LoseRevokeRace.Add(loser.Id);
        fx.Records.Clear();
        fx.Audits.Clear();
        var epochsBefore = fx.EpochBumps;

        var count = await fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, assignment.Id, Actor, "left the team");

        count.ShouldBe(1);
        fx.AppendedRevokedEvents.ShouldHaveSingleItem().Select(g => g.ConsentId).ShouldBe([winner.Id]);
        fx.Records.ShouldHaveSingleItem().ConsentId.ShouldBe(winner.Id);
        fx.Audits.Count.ShouldBe(1);
        (fx.EpochBumps - epochsBefore).ShouldBe(1);
        fx.InvalidatedAfterCommit.ShouldBe([winner.TableIdentifier]);
    }

    [Fact]
    public async Task Revocation_of_nothing_appends_no_event()
    {
        var fx = new ProfileAssignmentTests.Fixture();
        var assignment = fx.SeedApprovedAssignment();
        var only = SeedConsent(fx, assignment.Id, "orders");
        fx.LoseRevokeRace.Add(only.Id);

        (await fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, assignment.Id, Actor, "x")).ShouldBe(0);
        (await fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, Guid.NewGuid(), Actor, "x")).ShouldBe(0);

        fx.CallLog.ShouldNotContain("append");
        fx.AppendedRevokedEvents.ShouldBeEmpty();
        fx.InvalidatedAfterCommit.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_event_append_rolls_back_and_rethrows()
    {
        var fx = new ProfileAssignmentTests.Fixture();
        var assignment = fx.SeedApprovedAssignment();
        SeedConsent(fx, assignment.Id, "orders");
        fx.AppendFailure = new InvalidOperationException("append failed");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, assignment.Id, Actor, "x"));

        fx.CommitCount.ShouldBe(0);
        fx.Consents.ShouldAllBe(c => !c.IsRevoked);
        fx.InvalidatedAfterCommit.ShouldBeEmpty();
    }

    [Fact]
    public async Task Second_revocation_of_the_same_assignment_appends_no_event()
    {
        var fx = new ProfileAssignmentTests.Fixture();
        var assignment = fx.SeedApprovedAssignment();
        SeedConsent(fx, assignment.Id, "orders");

        (await fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, assignment.Id, Actor, "x")).ShouldBe(1);
        (await fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, assignment.Id, Actor, "x")).ShouldBe(0);

        fx.AppendedRevokedEvents.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Revocation_skips_a_consent_of_another_tenant()
    {
        var fx = new ProfileAssignmentTests.Fixture { IgnoreTenantFilter = true };
        var assignment = fx.SeedApprovedAssignment();
        var own = SeedConsent(fx, assignment.Id, "orders");
        var foreign = SeedConsent(fx, assignment.Id, "items", new TenantId("tenant-b"));
        fx.Records.Clear();

        var count = await fx.Maintenance.RevokeAssignmentAsync(ProfileAssignmentTests.Tenant, assignment.Id, Actor, "x");

        count.ShouldBe(1);
        fx.AppendedRevokedEvents.ShouldHaveSingleItem().Select(g => g.ConsentId).ShouldBe([own.Id]);
        fx.Consents.Single(c => c.Id == foreign.Id).IsRevoked.ShouldBeFalse();
        fx.Records.ShouldHaveSingleItem().ConsentId.ShouldBe(own.Id);
    }
}
