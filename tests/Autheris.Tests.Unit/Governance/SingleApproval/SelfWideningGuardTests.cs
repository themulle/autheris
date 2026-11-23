using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.SingleApproval;

/// <summary>Compensating controls of the single approval: no self-widening, no capacity or out-of-realm roles, fail-closed audit.</summary>
[Trait("Category", "Security")]
public sealed class SelfWideningGuardTests
{
    private static readonly SingleApprovalWriter Writer = new("oid-alice", ["alice@example.test"], ["S-1-5-21-staff", "grp-analysts"]);

    [Theory]
    [InlineData("oid-alice")]
    [InlineData("OID-ALICE")]
    [InlineData("alice@example.test")]
    public void Own_identifier_is_refused(string grantee)
    {
        var ex = Should.Throw<SingleApprovalRefusedException>(() => SelfWideningGuard.EnsureDoesNotWidenOwnAccess(Writer, ["oid-bob", grantee]));
        ex.Code.ShouldBe("SelfWidening");
    }

    [Theory]
    [InlineData("S-1-5-21-staff")]
    [InlineData("GRP-ANALYSTS")]
    public void Own_group_is_refused(string grantee)
    {
        Should.Throw<SingleApprovalRefusedException>(() => SelfWideningGuard.EnsureDoesNotWidenOwnAccess(Writer, [grantee]))
            .Code.ShouldBe("SelfWidening");
    }

    [Fact]
    public void Other_grantees_are_accepted()
    {
        Should.NotThrow(() => SelfWideningGuard.EnsureDoesNotWidenOwnAccess(Writer, ["oid-bob", "grp-other"]));
    }

    [Fact]
    public void Role_outside_realm_allowlist_is_refused()
    {
        IReadOnlySet<GatewayRole> allowed = new HashSet<GatewayRole> { GatewayRole.Consumer };

        Should.Throw<SingleApprovalRefusedException>(() => SelfWideningGuard.EnsureRoleMayBeConferred("FilterAdmin", allowed))
            .Code.ShouldBe("RoleOutsideRealm");
        Should.Throw<SingleApprovalRefusedException>(() => SelfWideningGuard.EnsureRoleMayBeConferred("NoSuchRole", allowed))
            .Code.ShouldBe("RoleOutsideRealm");
        Should.NotThrow(() => SelfWideningGuard.EnsureRoleMayBeConferred("Consumer", allowed));
    }

    [Theory]
    [InlineData("GovernanceAdmin")]
    [InlineData("ClusterAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("CallerAttributeApprover")]
    [InlineData("ItAdmin")]
    public void It_and_governance_capacity_is_never_conferred_even_when_the_realm_allows_it(string role)
    {
        IReadOnlySet<GatewayRole> allowed = new HashSet<GatewayRole>
        {
            GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.CallerAttributeApprover
        };

        Should.Throw<SingleApprovalRefusedException>(() => SelfWideningGuard.EnsureRoleMayBeConferred(role, allowed))
            .Code.ShouldBe("CapacityRoleNotMappable");
    }

    [Fact]
    public async Task Audit_records_hashes_only()
    {
        var audit = new InMemoryAuditLogRepository();
        var guard = new SelfWideningGuard(audit);

        await guard.RecordAsync(Writer, new SingleApprovalChange(new TenantId("t"), AuditEventTypes.CallerAttributeDefinitionApplied, "caller_attribute:x", "h1", "h2"));

        var written = audit.Entries.ShouldHaveSingleItem();
        written.DetailsJson.ShouldContain("h1");
        written.DetailsJson.ShouldContain("h2");
        written.ActorSid.Value.ShouldBe("oid-alice");
    }

    [Fact]
    public async Task Audit_failure_denies_the_change()
    {
        var guard = new SelfWideningGuard(new InMemoryAuditLogRepository { FailWith = new InvalidOperationException("db down") });

        var ex = await Should.ThrowAsync<SingleApprovalRefusedException>(() =>
            guard.RecordAsync(Writer, new SingleApprovalChange(new TenantId("t"), AuditEventTypes.CallerAttributeDefinitionApplied, "caller_attribute:x", null, "h2")));
        ex.Code.ShouldBe("AuditFailed");
    }
}
