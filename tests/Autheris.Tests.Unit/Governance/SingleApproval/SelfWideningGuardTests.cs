using System.Collections.Generic;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.SingleApproval;

/// <summary>Compensating controls of the single approval: no self-widening, fail-closed audit.</summary>
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
    public void Audit_entry_records_hashes_only()
    {
        var written = SelfWideningGuard.AuditEntry(Writer, new SingleApprovalChange(new TenantId("t"), AuditEventTypes.CallerAttributeDefinitionApplied, "caller_attribute:x", "h1", "h2"));

        written.DetailsJson.ShouldContain("h1");
        written.DetailsJson.ShouldContain("h2");
        written.ActorSid.Value.ShouldBe("oid-alice");
        written.Decision.ShouldBe("ALLOW");
        written.TenantId.ShouldBe(new TenantId("t"));
    }
}
