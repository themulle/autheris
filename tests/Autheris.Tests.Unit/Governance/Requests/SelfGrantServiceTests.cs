using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;
using H = Autheris.Tests.Unit.Governance.Requests.SelfGrantHarness;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class SelfGrantServiceTests
{
    private static int DeniedCount(H h) => h.Audit.Entries.Count(e => e.EventType == AuditEventTypes.SelfGrantDenied);

    [Fact]
    public async Task Owner_exists_refused()
    {
        var h = new H(ownerGroup: new Sid("S-1-5-21-1-1-1-1001"));
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"))));
        ex.ErrorCode.ShouldBe("SELF_GRANT_OWNER_EXISTS");
        h.DataApplier.Applied.ShouldBe(0);
        h.Created.ShouldBeEmpty();
        DeniedCount(h).ShouldBe(1);
    }

    [Fact]
    public async Task Empty_owner_group_still_counts_as_owner()
    {
        // Ownership is the presence of the assignment, not the member count.
        var h = new H(ownerGroup: new Sid("S-1-5-21-1-1-1-2002"));
        await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"))));
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Class_above_ceiling_refused()
    {
        var policy = TenantAccessPolicy.FromDeploymentDefaults(H.Tenant, new AccessPolicyOptions { SelfGrantCeiling = ProtectionClass.Internal });
        var h = new H(ProtectionClass.Confidential, tenantPolicy: policy);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"))));
        ex.ErrorCode.ShouldBe("SELF_GRANT_EXCEEDS_CEILING");
        DeniedCount(h).ShouldBe(1);
    }

    [Fact]
    public async Task Secret_refused_and_audited()
    {
        var h = new H(ProtectionClass.Secret);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"))));
        ex.ErrorCode.ShouldBe("SELF_GRANT_SECRET_REFUSED");
        DeniedCount(h).ShouldBe(1);
        h.Audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.SelfGrant);
    }

    [Fact]
    public async Task Secret_refused_and_audited_even_when_grant_write_would_fail()
    {
        var h = new H(ProtectionClass.Secret, grantWriteFails: true);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"))));
        ex.ErrorCode.ShouldBe("SELF_GRANT_SECRET_REFUSED");
        DeniedCount(h).ShouldBe(1);
    }

    [Fact]
    public async Task Confidential_table_with_secret_column_refused()
    {
        var h = new H(ProtectionClass.Confidential, columnClass: ProtectionClass.Secret);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"))));
        ex.ErrorCode.ShouldBe("SELF_GRANT_SECRET_REFUSED");
        DeniedCount(h).ShouldBe(1);
    }

    [Fact]
    public async Task Denial_audit_failure_still_denies()
    {
        var h = new H(ProtectionClass.Secret, auditFailure: new AuditUnavailableException("audit down"));
        await Should.ThrowAsync<AuditUnavailableException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"))));
        h.DataApplier.Applied.ShouldBe(0);
        h.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Over_365_days_refused()
    {
        // Beyond the configured self-grant maximum (deployment default 365 days) is refused, never truncated.
        var h = new H();
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"), days: 400)));
        ex.ErrorCode.ShouldBe(Autheris.Application.Governance.Grants.GrantErrorCodes.ExceedsApproverLimit);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Short_justification_rejected()
    {
        var h = new H();
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"), justification: "too short")));
        ex.ErrorCode.ShouldBe(JustificationPolicy.TooShortErrorCode);
        DeniedCount(h).ShouldBe(1);
    }

    [Fact]
    public async Task Unmask_allowed_with_justification()
    {
        var h = new H();
        var result = await h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"), PermissionKind.Unmask));
        h.UnmaskApplier.Applied.ShouldBe(1);
        h.UnmaskApplier.Last!.SelfGrant.ShouldBeTrue();
        h.UnmaskApplier.Last.Capacity.ShouldBe(ApproverCapacity.SelfGrant);
        result.Grants.Count.ShouldBe(1);
        h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.SelfGrant);
    }

    [Fact]
    public async Task Dg_notified()
    {
        var h = new H();
        await h.Service.SelfGrantAsync(H.Command(H.Actor("user:it")));
        h.Notifications.Messages.ShouldContain(m => m.EventType == AuditEventTypes.SelfGrant && m.TenantId == H.Tenant.Value);
        h.DataApplier.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Notification_failure_after_commit_does_not_fail_the_grant()
    {
        var h = new H();
        h.Notifications.Failure = new InvalidOperationException("smtp down");

        var result = await h.Service.SelfGrantAsync(H.Command(H.Actor("user:it")));

        result.Request.ShouldNotBeNull();
        h.DataApplier.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task ServicePrincipal_it_admin_follows_same_rules()
    {
        var h = new H(ProtectionClass.Secret);
        var sp = H.Actor("sp:pipeline", servicePrincipal: true);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(sp)));
        ex.ErrorCode.ShouldBe("SELF_GRANT_SECRET_REFUSED");
        var entry = h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.SelfGrantDenied);
        entry.DetailsJson.ShouldContain("ServicePrincipal");
    }

    [Fact]
    public async Task Non_it_actor_refused_and_audited()
    {
        var h = new H();
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:dg", "GovernanceAdmin"))));
        ex.ErrorCode.ShouldBe("FORBIDDEN");
        h.DataApplier.Applied.ShouldBe(0);
        DeniedCount(h).ShouldBe(1);
    }

    [Fact]
    public async Task Grant_to_third_party_is_not_a_self_grant()
    {
        var h = new H();
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Service.SelfGrantAsync(H.Command(H.Actor("user:it"), grantee: "user:bob")));
        ex.ErrorCode.ShouldBe("NOT_A_SELF_GRANT");
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public void Command_and_body_carry_no_self_grant_flag()
    {
        typeof(SelfGrantCommand).GetProperty("SelfGrant").ShouldBeNull();
    }
}
