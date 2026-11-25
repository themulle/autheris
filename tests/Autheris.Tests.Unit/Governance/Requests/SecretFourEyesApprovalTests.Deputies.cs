using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using Deputies = Autheris.Tests.Unit.Governance.Requests.DeputyTestSupport;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>
/// Issue #1126 on top of #1121: a deputy A acting for B and B are the same decision authority. A-for-B plus B never makes the two distinct
/// approvals of a grant on secret data; A-for-B plus a third eligible person does.
/// </summary>
public sealed partial class SecretFourEyesApprovalTests
{
    private static (Harness Harness, InMemoryDeputyRelationRepository Relations) DeputyHarness()
    {
        var relations = new InMemoryDeputyRelationRepository();
        relations.Rows.Add(Deputies.Relation(Deputies.Alice, Deputies.Bob, Deputies.Now.AddDays(-1), Deputies.Now.AddDays(10)));
        var authority = new DeputyAuthority(relations, Deputies.Directory(ownerGroup: OwnerGroup), new FakeTimeProvider(Deputies.Now));
        return (new Harness(ProtectionClass.Secret, ownerGroup: OwnerGroup, deputies: authority), relations);
    }

    private static SecurityPrincipalContext DeputyAlice() => Deputies.ContextFor(Deputies.Alice);

    private static SecurityPrincipalContext OwnerBob() => Deputies.ContextFor(Deputies.Bob, groups: [OwnerGroup]);

    private static SecurityPrincipalContext OwnerCarol() => Deputies.ContextFor(Deputies.Carol, groups: [OwnerGroup]);

    [Fact]
    public async Task Deputy_for_b_and_b_never_complete_a_secret_grant()
    {
        var (h, _) = DeputyHarness();
        (await h.Engine.DecideAsync(h.Approve(DeputyAlice()))).Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(OwnerBob())));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
        h.Store.Current.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task B_first_then_deputy_for_b_never_completes_a_secret_grant()
    {
        var (h, _) = DeputyHarness();
        await h.Engine.DecideAsync(h.Approve(OwnerBob()));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(DeputyAlice())));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Deputy_for_b_plus_a_third_owner_member_completes_a_secret_grant()
    {
        var (h, _) = DeputyHarness();
        await h.Engine.DecideAsync(h.Approve(DeputyAlice()));

        var done = await h.Engine.DecideAsync(h.Approve(OwnerCarol()));

        done.Request.Status.ShouldBe("APPROVED");
        h.DataApplier.Applied.ShouldBe(1);
        var first = h.Store.Current.ApprovalSteps.Single(s => s.StepNumber == 1);
        first.ApproverIdentifiers.ShouldContain(Deputies.Bob.Reference, "the step records the authority acted for");
        first.ApproverIdentifiers.ShouldContain(Deputies.Alice.ObjectSid);
    }

    [Fact]
    public async Task Two_deputies_of_the_same_principal_are_one_authority()
    {
        var (h, relations) = DeputyHarness();
        relations.Rows.Add(Deputies.Relation(Deputies.Dave, Deputies.Bob, Deputies.Now.AddDays(-1), Deputies.Now.AddDays(10)));
        await h.Engine.DecideAsync(h.Approve(DeputyAlice()));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Deputies.ContextFor(Deputies.Dave))));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
    }

    [Fact]
    public async Task The_same_deputy_acting_for_another_principal_is_still_the_same_person()
    {
        var (h, relations) = DeputyHarness();
        relations.Rows.Add(Deputies.Relation(Deputies.Alice, Deputies.Carol, Deputies.Now.AddDays(-1), Deputies.Now.AddDays(10)));
        await h.Engine.DecideAsync(h.Approve(DeputyAlice()));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(DeputyAlice())));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
    }

    [Fact]
    public async Task Inbox_hides_the_second_approval_from_b_after_deputy_for_b_approved()
    {
        var (h, _) = DeputyHarness();
        await h.Engine.DecideAsync(h.Approve(DeputyAlice()));
        var inbox = h.Inbox();

        (await inbox.GetInboxAsync(OwnerBob(), Tenant, null, 50)).Items.ShouldBeEmpty();
        (await inbox.GetInboxAsync(DeputyAlice(), Tenant, null, 50)).Items.ShouldBeEmpty();
        (await inbox.GetInboxAsync(OwnerCarol(), Tenant, null, 50)).Items.ShouldHaveSingleItem();
    }

    [Fact]
    public void Approver_identity_of_a_deputy_includes_every_form_of_the_principal()
    {
        var relation = Deputies.Relation(Deputies.Alice, Deputies.Bob, Deputies.Now, Deputies.Now.AddDays(1));
        var identity = SecretFourEyesRule.ApproverIdentity(DeputyAlice(), DeputyAuthority.PrincipalIdentifierForms(relation));
        var bobStep = new Autheris.Domain.Model.ApprovalStep
        {
            StepNumber = 1,
            ApproverSid = new Autheris.Domain.Common.Sid(Deputies.Bob.ObjectSid),
            Decision = "APPROVED"
        };

        identity.ShouldContain(Deputies.Alice.ObjectSid);
        SecretFourEyesRule.IsSameApprover(bobStep, identity).ShouldBeTrue();
        SecretFourEyesRule.IsSameApprover(bobStep, SecretFourEyesRule.ApproverIdentity(DeputyAlice(), null)).ShouldBeFalse();
    }
}
