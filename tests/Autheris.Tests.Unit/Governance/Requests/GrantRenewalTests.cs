using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;
using H = Autheris.Tests.Unit.Governance.Requests.SelfGrantHarness;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class GrantRenewalTests
{
    private const string Reason = "Quarterly renewal after access review.";

    private static Consent Grant(string grantee = "user:alice", PermissionGrantKind kind = PermissionGrantKind.Data, bool selfGrant = false, int renewals = 0, bool withRequest = true) => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = H.Orders,
        ConsentRequestId = withRequest ? Guid.NewGuid() : null,
        TenantId = H.Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(grantee),
        ValidFrom = H.Now.AddDays(-20),
        ValidTo = H.Now.AddDays(10),
        GrantKind = kind,
        SelfGrant = selfGrant,
        RenewalCount = renewals,
        GrantSource = ConsentGrantSource.ConsentRequest
    };

    private static GrantRenewalCommand Cmd(Consent c, Autheris.Domain.Security.SecurityPrincipalContext renewer, int days = 90, string? justification = Reason) =>
        new(c.Id, renewer, H.Now.AddDays(days), justification);

    private static H Harness(Consent existing, Sid? owner = null, ProtectionClass cls = ProtectionClass.Internal) => new(cls, ownerGroup: owner) { Existing = existing };

    [Fact]
    public async Task Eligible_approver_renews_with_signed_ledger_entry()
    {
        var c = Grant();
        var h = Harness(c);
        var result = await h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin")));
        var created = h.CreatedConsents.ShouldHaveSingleItem();
        created.RenewalCount.ShouldBe(1);
        created.ValidFrom.ShouldBe(H.Now);
        created.ValidTo.ShouldBe(H.Now.AddDays(90));
        result.Capacity.ShouldBe(ApproverCapacity.Governance);
        h.RevokedConsents.ShouldContain(c.Id);
        h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.GrantRenewed);
    }

    [Fact]
    public async Task Renewal_supersedes_with_the_renewed_grant_id()
    {
        var c = Grant();
        var h = Harness(c);
        await h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin")));
        var created = h.CreatedConsents.ShouldHaveSingleItem();
        h.RevocationKinds.ShouldBe([SystemRevocationKind.Renewal]);
        h.RenewedConsentIds.ShouldBe([created.Id]);
    }

    [Fact]
    public async Task Rollback_uses_the_renewal_rollback_kind_without_a_renewed_grant_id()
    {
        var c = Grant();
        var h = Harness(c);
        h.FailRevokeConsent = id => id == c.Id;
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin"))));
        h.RevocationKinds.ShouldBe([SystemRevocationKind.RenewalRollback]);
        h.RenewedConsentIds.ShouldBe([null]);
    }

    [Fact]
    public async Task Secret_grant_renewal_stays_a_single_decision()
    {
        // Issue #1121 owner answer 2: four eyes for the first grant on secret data only; a renewal is one decision.
        var c = Grant();
        var h = Harness(c, cls: ProtectionClass.Secret);
        var result = await h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin")));
        h.CreatedConsents.ShouldHaveSingleItem().RenewalCount.ShouldBe(1);
        result.Capacity.ShouldBe(ApproverCapacity.Governance);
        h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.GrantRenewed);
        h.Audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.PermissionApprovalRecorded);
    }

    [Fact]
    public async Task Superseded_consent_ended_by_id_even_without_request_id()
    {
        var c = Grant(withRequest: false);
        var h = Harness(c);
        await h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin")));
        h.RevokedConsents.ShouldContain(c.Id);
        h.RevokedForTenants.ShouldAllBe(t => t == H.Tenant);
    }

    [Fact]
    public async Task Failed_revoke_of_superseded_consent_leaves_no_extended_grant()
    {
        var c = Grant();
        var h = Harness(c);
        h.FailRevokeConsent = id => id == c.Id;
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin"))));
        var created = h.CreatedConsents.ShouldHaveSingleItem();
        h.RevokedConsents.ShouldContain(created.Id);
        h.Audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.GrantRenewed);
    }

    [Fact]
    public async Task Failed_audit_leaves_no_extended_grant()
    {
        var c = Grant();
        var h = new H(ProtectionClass.Internal, auditFailure: new InvalidOperationException("audit down")) { Existing = c };
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin"))));
        var created = h.CreatedConsents.ShouldHaveSingleItem();
        h.RevokedConsents.ShouldContain(created.Id);
        // Known fail-closed outcome: the superseded grant was already revoked, so BOTH grants end up revoked.
        h.RevokedConsents.ShouldContain(c.Id);
    }

    [Fact]
    public async Task Failed_rollback_of_renewed_consent_is_audited()
    {
        var c = Grant();
        var h = Harness(c);
        h.FailRevokeConsent = _ => true;
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin"))));
        var created = h.CreatedConsents.ShouldHaveSingleItem();
        var entry = h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.GrantRenewalRollbackFailed);
        entry.DetailsJson.ShouldContain(created.Id.ToString());
        entry.DetailsJson.ShouldContain(c.Id.ToString());
    }

    [Fact]
    public async Task Rollback_revoke_returning_false_is_audited()
    {
        var c = Grant();
        var h = Harness(c);
        h.RevokeReturnsFalse = _ => true;
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin"))));
        var created = h.CreatedConsents.ShouldHaveSingleItem();
        h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.GrantRenewalRollbackFailed).DetailsJson.ShouldContain(created.Id.ToString());
    }

    [Fact]
    public async Task Renewal_by_grantee_denied()
    {
        var c = Grant(grantee: "user:alice");
        var h = Harness(c);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:alice", "TenantAdmin"))));
        ex.ErrorCode.ShouldBe("SOD_VIOLATION");
        h.CreatedConsents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ineligible_approver_refused()
    {
        var c = Grant();
        var h = Harness(c, owner: new Sid("S-1-5-21-5-5-5-1001"));
        // With an owner group assigned, an IT administrator is not an eligible approver.
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:it", "TenantAdmin"))));
        ex.ErrorCode.ShouldBe("FORBIDDEN");
        h.CreatedConsents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Existing_selfgrant_not_renewable_by_grantee()
    {
        var c = Grant(grantee: "user:it", selfGrant: true);
        var h = Harness(c, owner: new Sid("S-1-5-21-5-5-5-1001"));
        await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:it", "TenantAdmin"))));
        h.CreatedConsents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Renewal_needs_justification()
    {
        var c = Grant();
        var h = Harness(c);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin"), justification: "short")));
        ex.ErrorCode.ShouldBe(JustificationPolicy.TooShortErrorCode);
        h.CreatedConsents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Renewal_not_chained()
    {
        // Validity is measured from the renewal time against the renewer capacity, not from the previous end date.
        var c = Grant();
        var h = Harness(c);
        var itRenewer = H.Actor("user:it", "TenantAdmin");
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(Cmd(c, itRenewer, days: 400)));
        ex.ErrorCode.ShouldBe(Autheris.Application.Governance.Grants.GrantErrorCodes.ExceedsApproverLimit);
        h.CreatedConsents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Catalog_grant_not_renewable()
    {
        var c = Grant(kind: PermissionGrantKind.Catalog);
        var h = Harness(c);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(Cmd(c, H.Actor("user:dg", "GovernanceAdmin"))));
        ex.ErrorCode.ShouldBe(PermissionRequestEngine.CatalogGrantNotRenewableCode);
        h.CreatedConsents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_end_date_rejected()
    {
        var c = Grant();
        var h = Harness(c);
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(new GrantRenewalCommand(c.Id, H.Actor("user:dg", "GovernanceAdmin"), null, Reason)));
        ex.ErrorCode.ShouldBe(Autheris.Application.Governance.Grants.GrantErrorCodes.EndDateRequired);
    }

    [Fact]
    public async Task Unknown_grant_not_found()
    {
        var h = Harness(Grant());
        var other = Grant();
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.RenewGrantAsync(Cmd(other, H.Actor("user:dg", "GovernanceAdmin"))));
        ex.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task Renewal_mints_fresh_child_ids_and_updates_consent_id_for_all_rules_and_filters()
    {
        var originalId = Guid.NewGuid();
        var origColRule1 = Guid.NewGuid();
        var origColRule2 = Guid.NewGuid();
        var origFilter1 = Guid.NewGuid();
        var origFilter2 = Guid.NewGuid();
        var origWrite1 = Guid.NewGuid();
        var origWrite2 = Guid.NewGuid();

        var existing = new Consent
        {
            Id = originalId,
            TableId = Guid.NewGuid(),
            TableIdentifier = H.Orders,
            ConsentRequestId = Guid.NewGuid(),
            TenantId = H.Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user:alice"),
            ValidFrom = H.Now.AddDays(-20),
            ValidTo = H.Now.AddDays(10),
            GrantKind = PermissionGrantKind.Data,
            GrantSource = ConsentGrantSource.ConsentRequest,
            ColumnRules =
            [
                new ConsentColumnRule
                {
                    Id = origColRule1,
                    ConsentId = originalId,
                    TableColumnId = Guid.NewGuid(),
                    ColumnName = "id",
                    AccessLevel = ColumnAccessLevel.Clear,
                    ApplicableScopes = ["Orders.Read"]
                },
                new ConsentColumnRule
                {
                    Id = origColRule2,
                    ConsentId = originalId,
                    TableColumnId = Guid.NewGuid(),
                    ColumnName = "amount",
                    AccessLevel = ColumnAccessLevel.Mask,
                    ApplicableScopes = []
                }
            ],
            RowFilters =
            [
                new ConsentRowFilter
                {
                    Id = origFilter1,
                    ConsentId = originalId,
                    FilterGroup = 0,
                    TableColumnId = Guid.NewGuid(),
                    ColumnName = "region",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = "\"EU\"",
                    ValueSource = "LITERAL"
                },
                new ConsentRowFilter
                {
                    Id = origFilter2,
                    ConsentId = originalId,
                    FilterGroup = 1,
                    TableColumnId = Guid.NewGuid(),
                    ColumnName = "owner_sid",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = "[]",
                    ValueSource = "USER_ATTRIBUTE",
                    UserAttribute = "sid"
                }
            ],
            WriteRules =
            [
                new WriteRule(origWrite1, WriteOperation.Update, ["amount"], WriteRowScope.OwnRows, null, 25),
                new WriteRule(origWrite2, WriteOperation.Delete, [], WriteRowScope.AllRows, null, 10)
            ]
        };

        var h = Harness(existing);
        await h.Engine.RenewGrantAsync(Cmd(existing, H.Actor("user:dg", "GovernanceAdmin")));
        var created = h.CreatedConsents.ShouldHaveSingleItem();

        created.Id.ShouldNotBe(originalId);
        created.ColumnRules.Count.ShouldBe(2);
        created.ColumnRules.Select(r => r.Id).ShouldNotContain(origColRule1);
        created.ColumnRules.Select(r => r.Id).ShouldNotContain(origColRule2);
        created.ColumnRules.Select(r => r.Id).Distinct().Count().ShouldBe(2);
        created.ColumnRules.ShouldAllBe(r => r.ConsentId == created.Id);
        created.ColumnRules[0].ColumnName.ShouldBe("id");
        created.ColumnRules[0].ApplicableScopes.ShouldBe(["Orders.Read"]);
        created.ColumnRules[1].AccessLevel.ShouldBe(ColumnAccessLevel.Mask);

        created.RowFilters.Count.ShouldBe(2);
        created.RowFilters.Select(f => f.Id).ShouldNotContain(origFilter1);
        created.RowFilters.Select(f => f.Id).ShouldNotContain(origFilter2);
        created.RowFilters.Select(f => f.Id).Distinct().Count().ShouldBe(2);
        created.RowFilters.ShouldAllBe(f => f.ConsentId == created.Id);
        created.RowFilters[0].ValueJson.ShouldBe("\"EU\"");
        created.RowFilters[1].UserAttribute.ShouldBe("sid");

        created.WriteRules.Count.ShouldBe(2);
        created.WriteRules.Select(w => w.Id).ShouldNotContain(origWrite1);
        created.WriteRules.Select(w => w.Id).ShouldNotContain(origWrite2);
        created.WriteRules.Select(w => w.Id).Distinct().Count().ShouldBe(2);
        created.WriteRules[0].Columns.ShouldBe(["amount"]);
        created.WriteRules[1].Operation.ShouldBe(WriteOperation.Delete);

        // Original child IDs remain untouched on the superseded consent.
        existing.ColumnRules[0].Id.ShouldBe(origColRule1);
        existing.RowFilters[0].Id.ShouldBe(origFilter1);
        existing.WriteRules[0].Id.ShouldBe(origWrite1);
    }
}
