using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using H = Autheris.Tests.Unit.Governance.Requests.SelfGrantHarness;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class SelfGrantGroupMembershipTests
{
    private const string OwnGroup = "S-1-5-21-9-9-9-5001";

    private static ConsentRequest GroupRequest() => new()
    {
        Id = System.Guid.NewGuid(),
        TableId = System.Guid.NewGuid(),
        TableIdentifier = H.Orders,
        RequesterSid = new Autheris.Domain.Common.Sid("user:alice"),
        RequestedGranteeType = GranteeType.Group,
        RequestedGranteeRef = OwnGroup,
        BusinessJustification = "Group access for the incident response team.",
        Status = "PENDING",
        RequestedAt = H.Now.AddHours(-1),
        RequestedValidFrom = H.Now,
        RequestedValidTo = H.Now.AddDays(30),
        TenantId = H.Tenant,
        Version = 1,
        RequestKind = ConsentRequestKind.Data,
        GrantKind = PermissionGrantKind.Data
    };

    private static async Task<PermissionDecisionResult> DecideAsync(H h, Autheris.Domain.Security.SecurityPrincipalContext decider, ConsentRequest req)
    {
        h.Created.Add(req);
        return await h.Engine.DecideAsync(new PermissionDecisionCommand(req.Id, 1, decider, Approve: true, ValidTo: H.Now.AddDays(30)));
    }

    [Fact]
    public async Task It_secret_to_own_group_refused()
    {
        var h = new H(Autheris.Domain.Security.ProtectionClass.Secret);
        var it = H.Actor("user:it", groups: [OwnGroup]);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, it, GroupRequest()));
        ex.ErrorCode.ShouldBe("SELF_GRANT_SECRET_REFUSED");
        h.DataApplier.Applied.ShouldBe(0);
        h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.SelfGrantDenied);
    }

    [Fact]
    public async Task It_internal_to_own_group_is_completed_as_self_grant()
    {
        var h = new H(Autheris.Domain.Security.ProtectionClass.Internal);
        var it = H.Actor("user:it", groups: [OwnGroup]);
        await DecideAsync(h, it, GroupRequest());
        h.DataApplier.Applied.ShouldBe(1);
        h.DataApplier.Last!.SelfGrant.ShouldBeTrue();
        h.Notifications.Messages.ShouldContain(m => m.EventType == AuditEventTypes.SelfGrant);
    }

    [Fact]
    public async Task Dg_deciding_for_own_group_refused()
    {
        var h = new H(Autheris.Domain.Security.ProtectionClass.Internal);
        var dg = H.Actor("user:dg", "GovernanceAdmin", groups: [OwnGroup]);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, dg, GroupRequest()));
        ex.ErrorCode.ShouldBe("SOD_VIOLATION");
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Non_it_overlap_denied()
    {
        var h = new H(Autheris.Domain.Security.ProtectionClass.Public);
        var other = H.Actor("user:analyst", "GovernanceAdmin", groups: [OwnGroup]);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, other, GroupRequest()));
        ex.ErrorCode.ShouldBe("SOD_VIOLATION");
        h.DataApplier.Applied.ShouldBe(0);
    }
}
