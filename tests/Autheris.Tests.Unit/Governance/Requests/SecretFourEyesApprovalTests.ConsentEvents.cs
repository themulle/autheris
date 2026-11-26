using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>
/// Issue #1165: every committed decision appends one thin consent event in the decision transaction (request.decided for a final
/// decision, request.updated for a recorded first approval on secret data); refused and conflicting decisions append none.
/// </summary>
public sealed partial class SecretFourEyesApprovalTests
{
    [Fact]
    public async Task Single_approval_appends_one_request_decided_event_in_the_decision_transaction()
    {
        var h = new Harness(ProtectionClass.Confidential);

        var result = await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var (entry, transaction) = h.Events.Appended.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(ConsentEventTypes.RequestDecided);
        entry.SubjectId.ShouldBe(result.Request.Id.ToString("D"));
        entry.TenantId.ShouldBe(Tenant.Value);
        entry.ExternalSystemId.ShouldBeNull();
        transaction.ShouldBeSameAs(h.Store.Transaction);
        RecordingConsentEventOutbox.ReadThinRequestPayload(entry).ShouldBe(("APPROVED", 2, "INTERNAL"));
    }

    [Fact]
    public async Task Rejection_appends_one_request_decided_event()
    {
        var h = new Harness(ProtectionClass.Confidential);

        await h.Engine.DecideAsync(h.Reject(Dg("user:dg-1")));

        var entry = h.Events.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(ConsentEventTypes.RequestDecided);
        RecordingConsentEventOutbox.ReadThinRequestPayload(entry).ShouldBe(("REJECTED", 2, "INTERNAL"));
        entry.PayloadJson.ShouldNotContain(RejectReason);
    }

    [Fact]
    public async Task First_approval_on_secret_data_appends_request_updated_and_the_second_request_decided()
    {
        var h = new Harness(ProtectionClass.Secret);

        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var first = h.Events.Entries.ShouldHaveSingleItem();
        first.EventType.ShouldBe(ConsentEventTypes.RequestUpdated);
        RecordingConsentEventOutbox.ReadThinRequestPayload(first).ShouldBe((SecretFourEyesRule.PendingSecondApproval, 2, "INTERNAL"));

        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-2")));

        h.Events.Entries.Count.ShouldBe(2);
        var decided = h.Events.Entries[1];
        decided.EventType.ShouldBe(ConsentEventTypes.RequestDecided);
        RecordingConsentEventOutbox.ReadThinRequestPayload(decided).ShouldBe(("APPROVED", 3, "INTERNAL"));
        h.Events.Appended.ShouldAllBe(a => a.Transaction == h.Store.Transaction);
    }

    [Fact]
    public async Task Decision_on_an_externally_routed_request_carries_its_external_system()
    {
        var systemId = Guid.NewGuid();
        var h = new Harness(ProtectionClass.Confidential, externalSystemId: systemId);

        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var entry = h.Events.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(ConsentEventTypes.RequestDecided);
        entry.ExternalSystemId.ShouldBe(systemId);
        RecordingConsentEventOutbox.ReadThinRequestPayload(entry).ShouldBe(("APPROVED", 2, "EXTERNAL"));
    }

    [Fact]
    public async Task Decision_payload_names_neither_decider_nor_justification()
    {
        var h = new Harness(ProtectionClass.Confidential);

        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-payload")));

        var payload = h.Events.Entries.ShouldHaveSingleItem().PayloadJson;
        payload.ShouldNotContain("user:dg-payload");
        payload.ShouldNotContain("user:requester");
        payload.ShouldNotContain("user:alice");
        payload.ShouldNotContain("Quarter close");
    }

    [Fact]
    public async Task Ineligible_decider_appends_no_event()
    {
        var h = new Harness(ProtectionClass.Confidential);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Actor("user:nobody", "Reader"))));

        ex.StatusCode.ShouldBe(403);
        h.Events.Appended.ShouldBeEmpty();
    }

    [Fact]
    public async Task Same_approver_refusal_appends_no_further_event()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-1"))));

        ex.StatusCode.ShouldBe(403);
        h.Events.Entries.ShouldHaveSingleItem().EventType.ShouldBe(ConsentEventTypes.RequestUpdated);
    }

    [Fact]
    public async Task Validation_refusal_in_the_transaction_appends_no_further_event()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() =>
            h.Engine.DecideAsync(h.Approve(Dg("user:dg-2"), validTo: h.Store.Current.RequestedValidTo.AddDays(1))));

        ex.StatusCode.ShouldBe(422);
        h.Events.Entries.ShouldHaveSingleItem().EventType.ShouldBe(ConsentEventTypes.RequestUpdated);
    }

    [Fact]
    public async Task Conflicting_decisions_append_no_event()
    {
        var h = new Harness(ProtectionClass.Confidential);
        var stale = h.Approve(Dg("user:dg-1")) with { ExpectedVersion = 7 };

        var conflict = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(stale));
        conflict.StatusCode.ShouldBe(409);
        h.Events.Appended.ShouldBeEmpty();

        await h.Engine.DecideAsync(h.Reject(Dg("user:dg-1")));
        var late = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-2"))));

        late.StatusCode.ShouldBe(409);
        h.Events.Entries.ShouldHaveSingleItem().EventType.ShouldBe(ConsentEventTypes.RequestDecided);
    }

    [Fact]
    public async Task Audit_failure_appends_no_event()
    {
        var h = new Harness(ProtectionClass.Confidential, auditFailure: new AuditUnavailableException("Audit store down"));

        await Should.ThrowAsync<AuditUnavailableException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-1"))));

        h.Events.Appended.ShouldBeEmpty();
    }
}
