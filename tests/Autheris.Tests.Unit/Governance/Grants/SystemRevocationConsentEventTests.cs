namespace Autheris.Tests.Unit.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Governance.Requests;
using Autheris.Tests.Unit.Persistence.DialectSql;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1170: system revocations that end access (deprovisioning and the external-system revoke) append one grant.revoked per revoked
/// grant in the revocation transaction, after every other write, with the thin payload and routing of the person revocation. Issue #1173:
/// the supersede of a grant renewal appends grant.renewed for the renewed grant and nothing for the superseded one; the compensation of a
/// failed renewal appends grant.revoked only when that grant.renewed was committed.
/// </summary>
[Trait("Category", "Security")]
public sealed class SystemRevocationConsentEventTests
{
    private static readonly TenantId Tenant = new("tenant-1170");
    private static readonly Sid Leaver = new("S-1-5-21-1000-2000-3000-1170");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] ThinGrantPayloadProperties =
        ["tenantId", "requestId", "grantId", "objectId", "status", "version", "workflowMode", "occurredAt", "links"];

    public static TheoryData<string> Dialects() => DialectKit.Names();

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Deprovisioning_run_appends_one_grant_revoked_per_revoked_grant_as_the_last_write(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        var audits = new List<AuditLogEntry>();
        await using var repo = CreateRepo(dialect, conn, audits);
        var routedSystem = Guid.NewGuid();
        var originRequest = Guid.NewGuid();
        var data = Grant(PermissionGrantKind.Data);
        var unmask = Grant(PermissionGrantKind.Unmask);
        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, originRequest));
        conn.EnqueueReader(OriginRequestRow(originRequest, "EXTERNAL", routedSystem));
        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(new DataTable());

        var consents = NullFake.Create<IConsentRepository>((m, args) => m.Name switch
        {
            nameof(IConsentRepository.GetActiveGrantsForSubjectsAsync) => Task.FromResult<IReadOnlyList<Consent>>([data, unmask]),
            nameof(IConsentRepository.RevokeSystemConsentByIdAsync) =>
                repo.RevokeSystemConsentByIdAsync(
                    (TenantId)args![0]!, (Guid)args[1]!, (Sid)args[2]!, (string)args[3]!, (SystemRevocationKind)args[4]!, (Guid?)args[5], (CancellationToken)args[6]!),
            _ => NullFake.UseDefault
        });
        var sut = new GrantRevocationService(
            consents, NullFake.Create<IConsentApprovalRepository>(), new InMemoryAuditWriter(), timeProvider: new FakeTimeProvider(Now));

        var result = await sut.RevokeAllForSubjectAsync(Tenant, Leaver, "SCIM_DEPROVISION");

        result.Complete.ShouldBeTrue();
        result.GrantsRevoked.ShouldBe(2);
        var events = EventInserts(conn);
        events.Count.ShouldBe(2);
        events.Select(e => Bound(e, "@subjectId")).ShouldBe([data.Id.ToString("D"), unmask.Id.ToString("D")]);
        events.ShouldAllBe(e => (string?)Bound(e, "@type") == ConsentEventTypes.GrantRevoked && e.TransactionSet);
        events.ShouldAllBe(e => (string?)Bound(e, "@tenant") == Tenant.Value);

        var routed = ReadThinGrantPayload(events[0]);
        routed.Status.ShouldBe("REVOKED");
        routed.WorkflowMode.ShouldBe("EXTERNAL");
        routed.RequestId.ShouldBe(originRequest);
        var routedDelivery = conn.Commands.Single(c =>
            c.CommandText.Contains("INSERT INTO CONSENT_EVENT_DELIVERIES", StringComparison.Ordinal) && c.Parameters.Any(p => p.Name == "@externalSystemId"));
        Bound(routedDelivery, "@externalSystemId").ShouldBe(routedSystem.ToString("D"));

        var unrouted = ReadThinGrantPayload(events[1]);
        unrouted.WorkflowMode.ShouldBe("INTERNAL");
        unrouted.RequestId.ShouldBeNull();

        // The event lock is the last lock of each revocation: grant row, ledger, audit and table epoch come first.
        AssertEventAppendedLast(conn, dialect, revocations: 2, expectEpochBump: true);
        audits.Count(a => a.EventType == AuditEventTypes.ConsentRevoked).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task System_revocation_by_id_appends_no_event_when_nothing_was_revoked(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var actor = new Sid("SYSTEM:DEPROVISIONING");

        conn.EnqueueReader(new DataTable());
        (await repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, "missing")).ShouldBeFalse();

        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: true, requestId: null));
        (await repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, "already revoked")).ShouldBeFalse();

        conn.EnqueueReader(RevokeTargetRow("tenant-other", isRevoked: false, requestId: null));
        (await repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, "other tenant")).ShouldBeFalse();

        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, requestId: null));
        conn.NonQueryResult = 0;
        (await repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, "lost race")).ShouldBeFalse();

        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("CONSENT_EVENT", StringComparison.Ordinal));
        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("consent-events", StringComparison.Ordinal) || IsEventLock(c));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task External_system_revocation_appends_exactly_one_grant_revoked_in_its_transaction(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var system = Guid.NewGuid();
        var originRequest = Guid.NewGuid();
        var consentId = Guid.NewGuid();
        var actor = new Sid("external-system:" + system.ToString("D"));
        conn.EnqueueReader(SystemRevokeTargetRow());
        conn.EnqueueReader(OriginRequestRow(originRequest, "EXTERNAL", system));

        (await repo.RevokeSystemConsentAsync(consentId, originRequest, actor, "Access no longer needed")).ShouldBeTrue();

        conn.EnqueueReader(new DataTable());
        (await repo.RevokeSystemConsentAsync(consentId, originRequest, actor, "Second attempt")).ShouldBeFalse();

        var appended = EventInserts(conn).ShouldHaveSingleItem();
        Bound(appended, "@type").ShouldBe(ConsentEventTypes.GrantRevoked);
        Bound(appended, "@subjectId").ShouldBe(consentId.ToString("D"));
        var payload = ReadThinGrantPayload(appended);
        payload.Status.ShouldBe("REVOKED");
        payload.WorkflowMode.ShouldBe("EXTERNAL");
        payload.RequestId.ShouldBe(originRequest);
        appended.Parameters.Select(p => p.Value as string ?? string.Empty).ShouldNotContain(v => v.Contains("Access no longer needed", StringComparison.Ordinal));
        AssertEventAppendedLast(conn, dialect, revocations: 1, expectEpochBump: false);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Renewal_appends_no_grant_revoked_for_the_superseded_or_the_rolled_back_grant(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var renewer = SelfGrantHarness.Actor("user:dg", "GovernanceAdmin");

        // Successful renewal: the superseded grant is revoked through the real repository and only grant.renewed is appended.
        var renewed = RenewableGrant();
        var success = new SelfGrantHarness(ProtectionClass.Internal) { Existing = renewed, RevokeThrough = repo };
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, renewed.ConsentRequestId));
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(OriginRequestRow(renewed.ConsentRequestId!.Value, "INTERNAL", null));
        await success.Engine.RenewGrantAsync(RenewCommand(renewed, renewer));
        success.RevokedConsents.ShouldBe([renewed.Id]);
        var afterSuccess = conn.Commands.Count;
        Bound(EventInserts(conn).ShouldHaveSingleItem(), "@type").ShouldBe(ConsentEventTypes.GrantRenewed);

        // Failed renewal before the supersede commit: ending the original grant fails, the compensation revokes the renewed grant
        // through the real repository. It was never announced, so nothing is appended and no event lock is taken.
        var original = RenewableGrant();
        var failed = new SelfGrantHarness(ProtectionClass.Internal) { Existing = original, RevokeThrough = repo, FailRevokeConsent = id => id == original.Id };
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, original.ConsentRequestId));
        conn.EnqueueReader(new DataTable());
        await Should.ThrowAsync<InvalidOperationException>(() => failed.Engine.RenewGrantAsync(RenewCommand(original, renewer)));
        failed.RevokedConsents.ShouldBe([failed.CreatedConsents.ShouldHaveSingleItem().Id]);

        success.RevocationKinds.ShouldBe([SystemRevocationKind.Renewal]);
        failed.RevocationKinds.ShouldBe([SystemRevocationKind.RenewalRollback]);
        var failedPath = conn.Commands.Skip(afterSuccess).ToList();
        failedPath.ShouldNotContain(c => c.CommandText.Contains("INSERT INTO CONSENT_EVENTS", StringComparison.Ordinal) || IsEventLock(c));
        conn.Commands.Count(c => c.CommandText.TrimStart().StartsWith("UPDATE CONSENTS", StringComparison.Ordinal)).ShouldBe(2, dialect);
        conn.Commands.Count(IsEventLock).ShouldBe(1, dialect);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Successful_renewal_appends_one_grant_renewed_for_the_renewed_grant_as_the_last_write(string dialect)
    {
        var run = await RunSuccessfulRenewalAsync(dialect);

        var appended = EventInserts(run.Conn).ShouldHaveSingleItem();
        Bound(appended, "@type").ShouldBe(ConsentEventTypes.GrantRenewed);
        Bound(appended, "@subjectId").ShouldBe(run.Harness.CreatedConsents.Single().Id.ToString("D"));
        Bound(appended, "@tenant").ShouldBe(SelfGrantHarness.Tenant.Value);
        EventInserts(run.Conn).ShouldNotContain(e => (string?)Bound(e, "@type") == ConsentEventTypes.GrantRevoked);
        run.Harness.RevocationKinds.ShouldBe([SystemRevocationKind.Renewal]);
        run.Conn.Commands.Count(c => c.CommandText.Contains("INSERT INTO CONSENT_EVENT_DELIVERIES", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(1);
        AssertEventAppendedLast(run.Conn, dialect, revocations: 1, expectEpochBump: true);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Grant_renewed_payload_has_exactly_the_thin_property_set_and_no_sensitive_fields(string dialect)
    {
        var run = await RunSuccessfulRenewalAsync(dialect);

        var json = (string)Bound(EventInserts(run.Conn).Single(), "@payload")!;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).Order().ShouldBe(
            new[] { "tenantId", "requestId", "grantId", "objectId", "status", "version", "workflowMode", "occurredAt", "links", "previousGrantId" }.Order());
        root.GetProperty("status").GetString().ShouldBe("ACTIVE");
        root.GetProperty("version").GetInt32().ShouldBe(1);
        root.GetProperty("grantId").GetGuid().ShouldBe(run.Harness.CreatedConsents.Single().Id);
        root.GetProperty("previousGrantId").GetGuid().ShouldBe(run.Old.Id);
        root.GetProperty("requestId").GetGuid().ShouldBe(run.Old.ConsentRequestId!.Value);
        root.GetProperty("workflowMode").GetString().ShouldBe("EXTERNAL");
        json.ShouldNotContain("user:dg", Case.Insensitive);
        json.ShouldNotContain("user:alice", Case.Insensitive);
        json.ShouldNotContain("Quarterly renewal", Case.Insensitive);
        json.ShouldNotContain("Renewed by approver", Case.Insensitive);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Renewal_routes_grant_renewed_only_to_the_origin_request_system_and_stored_tenant(string dialect)
    {
        var run = await RunSuccessfulRenewalAsync(dialect);

        var routing = run.Conn.Commands.Where(c => c.CommandText.Contains("LEFT JOIN CONSENT_REQUESTS", StringComparison.Ordinal)).ToList();
        var read = routing.ShouldHaveSingleItem();
        read.CommandText.ShouldContain("c.tenant_id = @tenant");
        read.CommandText.ShouldContain("r.tenant_id = c.tenant_id");
        Bound(read, "@tenant").ShouldBe(SelfGrantHarness.Tenant.Value);
        Bound(read, "@id").ShouldBe(run.Old.Id.ToString("D"));

        var deliveries = run.Conn.Commands.Where(c =>
            c.CommandText.Contains("INSERT INTO CONSENT_EVENT_DELIVERIES", StringComparison.Ordinal) && c.Parameters.Any(p => p.Name == "@externalSystemId")).ToList();
        deliveries.ShouldHaveSingleItem();
        Bound(deliveries[0], "@externalSystemId").ShouldBe(run.System.ToString("D"));
        Bound(EventInserts(run.Conn).Single(), "@tenant").ShouldBe(SelfGrantHarness.Tenant.Value);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Renewal_whose_audit_fails_after_the_supersede_commit_appends_grant_revoked_for_the_renewed_grant(string dialect)
    {
        var run = await RunRenewalFailingAfterSupersedeAsync(dialect, auditFails: true);
        AssertRenewedThenRevoked(run, dialect);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Renewal_failing_after_the_supersede_commit_appends_grant_revoked_for_the_renewed_grant(string dialect)
    {
        var run = await RunRenewalFailingAfterSupersedeAsync(dialect, auditFails: false);
        AssertRenewedThenRevoked(run, dialect);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Renewal_rollback_after_a_concurrent_revocation_of_the_old_grant_appends_no_event(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var old = RenewableGrant();
        var h = new SelfGrantHarness(ProtectionClass.Internal) { Existing = old, RevokeThrough = repo };

        // A person revoked the old grant meanwhile: the supersede finds it revoked and fails, the compensation finds nothing announced.
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: true, old.ConsentRequestId));
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(new DataTable());
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(RenewCommand(old, SelfGrantHarness.Actor("user:dg", "GovernanceAdmin"))));

        h.RevocationKinds.ShouldBe([SystemRevocationKind.Renewal, SystemRevocationKind.RenewalRollback]);
        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("INSERT INTO CONSENT_EVENTS", StringComparison.Ordinal) || IsEventLock(c));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Failed_renewal_rollback_leaves_only_grant_renewed(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var old = RenewableGrant();
        SelfGrantHarness? holder = null;
        var h = new SelfGrantHarness(ProtectionClass.Internal)
        {
            Existing = old,
            RevokeThrough = repo,
            ThrowAfterRevokeThrough = id => id == old.Id,
            FailRevokeConsent = id => holder!.CreatedConsents.Count > 0 && id == holder.CreatedConsents[0].Id
        };
        holder = h;
        EnqueueSupersede(conn, old, Guid.NewGuid());

        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(RenewCommand(old, SelfGrantHarness.Actor("user:dg", "GovernanceAdmin"))));

        Bound(EventInserts(conn).ShouldHaveSingleItem(), "@type").ShouldBe(ConsentEventTypes.GrantRenewed);
        h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.GrantRenewalRollbackFailed);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Repeated_renewal_rollback_appends_no_second_grant_revoked(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var renewedId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var actor = new Sid("user:dg");

        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, origin));
        conn.EnqueueReader(ExistsRow(1));
        conn.EnqueueReader(OriginRequestRow(origin, "INTERNAL", null));
        (await repo.RevokeSystemConsentByIdAsync(Tenant, renewedId, actor, "Renewal rolled back.", SystemRevocationKind.RenewalRollback)).ShouldBeTrue();

        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: true, origin));
        (await repo.RevokeSystemConsentByIdAsync(Tenant, renewedId, actor, "Renewal rolled back.", SystemRevocationKind.RenewalRollback)).ShouldBeFalse();

        var appended = EventInserts(conn).ShouldHaveSingleItem();
        Bound(appended, "@type").ShouldBe(ConsentEventTypes.GrantRevoked);
        Bound(appended, "@subjectId").ShouldBe(renewedId.ToString("D"));
        conn.Commands.Count(IsEventLock).ShouldBe(1, dialect);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Renewal_with_a_renewed_id_of_another_tenant_or_unknown_throws_and_appends_no_event(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var actor = new Sid("user:dg");

        // The renewed grant belongs to another tenant.
        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(RevokeTargetRow("tenant-other", isRevoked: false, requestId: null));
        var otherTenant = await Should.ThrowAsync<InvalidOperationException>(() =>
            repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, "Renewed by approver.", SystemRevocationKind.Renewal, Guid.NewGuid()));

        // The renewed grant does not exist.
        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(new DataTable());
        var unknown = await Should.ThrowAsync<InvalidOperationException>(() =>
            repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, "Renewed by approver.", SystemRevocationKind.Renewal, Guid.NewGuid()));

        otherTenant.Message.ShouldNotContain("tenant-other");
        unknown.Message.ShouldNotContain("Renewed by approver");
        conn.Commands.ShouldNotContain(c =>
            c.CommandText.TrimStart().StartsWith("UPDATE CONSENTS", StringComparison.Ordinal)
            || c.CommandText.Contains("INSERT INTO CONSENT_EVENTS", StringComparison.Ordinal)
            || IsEventLock(c));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Renewal_kind_requires_a_distinct_renewed_grant_id_and_other_kinds_reject_one(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var actor = new Sid("user:dg");
        var id = Guid.NewGuid();

        await Should.ThrowAsync<ArgumentException>(() => repo.RevokeSystemConsentByIdAsync(Tenant, id, actor, "r", SystemRevocationKind.Renewal, null));
        await Should.ThrowAsync<ArgumentException>(() => repo.RevokeSystemConsentByIdAsync(Tenant, id, actor, "r", SystemRevocationKind.Renewal, Guid.Empty));
        await Should.ThrowAsync<ArgumentException>(() => repo.RevokeSystemConsentByIdAsync(Tenant, id, actor, "r", SystemRevocationKind.Renewal, id));
        await Should.ThrowAsync<ArgumentException>(() => repo.RevokeSystemConsentByIdAsync(Tenant, id, actor, "r", SystemRevocationKind.Revoked, Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentException>(() => repo.RevokeSystemConsentByIdAsync(Tenant, id, actor, "r", SystemRevocationKind.RenewalRollback, Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repo.RevokeSystemConsentByIdAsync(Tenant, id, actor, "r", (SystemRevocationKind)99, null));

        conn.Commands.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Event_append_failures_do_not_leak_payload_or_reason_in_the_exception_message(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        const string reason = "Reason-with-secret-justification";
        var actor = new Sid("S-1-5-21-secret-actor");

        // The superseded grant's routing row is missing: the renewal throws instead of degrading to an internal event.
        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, requestId: null));
        var missingRouting = await Should.ThrowAsync<InvalidOperationException>(() =>
            repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, reason, SystemRevocationKind.Renewal, Guid.NewGuid()));

        // The same for the compensation of an announced renewal.
        conn.EnqueueReader(RevokeTargetRow(Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(ExistsRow(1));
        var missingRollbackRouting = await Should.ThrowAsync<InvalidOperationException>(() =>
            repo.RevokeSystemConsentByIdAsync(Tenant, Guid.NewGuid(), actor, reason, SystemRevocationKind.RenewalRollback));

        foreach (var ex in new[] { missingRouting, missingRollbackRouting })
        {
            ex.Message.ShouldNotContain(reason);
            ex.Message.ShouldNotContain(actor.Value);
            ex.Message.ShouldNotContain("payload", Case.Insensitive);
        }

        EventInserts(conn).ShouldBeEmpty();
    }

    private sealed record RenewalRun(RecordingDbConnection Conn, SelfGrantHarness Harness, Consent Old, Guid System);

    private static void EnqueueSupersede(RecordingDbConnection conn, Consent old, Guid system)
    {
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, old.ConsentRequestId));
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, requestId: null));
        conn.EnqueueReader(OriginRequestRow(old.ConsentRequestId!.Value, "EXTERNAL", system));
    }

    private static async Task<RenewalRun> RunSuccessfulRenewalAsync(string dialect)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var old = RenewableGrant();
        var system = Guid.NewGuid();
        var h = new SelfGrantHarness(ProtectionClass.Internal) { Existing = old, RevokeThrough = repo };
        EnqueueSupersede(conn, old, system);
        await h.Engine.RenewGrantAsync(RenewCommand(old, SelfGrantHarness.Actor("user:dg", "GovernanceAdmin")));
        return new RenewalRun(conn, h, old, system);
    }

    private static async Task<RenewalRun> RunRenewalFailingAfterSupersedeAsync(string dialect, bool auditFails)
    {
        var conn = DialectKit.Of(dialect).NewConnection();
        await using var repo = CreateRepo(dialect, conn, []);
        var old = RenewableGrant();
        var system = Guid.NewGuid();
        var h = new SelfGrantHarness(ProtectionClass.Internal, auditFailure: auditFails ? new InvalidOperationException("audit down") : null)
        {
            Existing = old,
            RevokeThrough = repo,
            ThrowAfterRevokeThrough = auditFails ? null : id => id == old.Id
        };
        EnqueueSupersede(conn, old, system);

        // The compensation: target of the renewed grant, the persisted grant.renewed lookup, then the routing read.
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, old.ConsentRequestId));
        conn.EnqueueReader(ExistsRow(1));
        conn.EnqueueReader(OriginRequestRow(old.ConsentRequestId!.Value, "EXTERNAL", system));
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.RenewGrantAsync(RenewCommand(old, SelfGrantHarness.Actor("user:dg", "GovernanceAdmin"))));
        return new RenewalRun(conn, h, old, system);
    }

    private static void AssertRenewedThenRevoked(RenewalRun run, string dialect)
    {
        var created = run.Harness.CreatedConsents.ShouldHaveSingleItem();
        var inserts = EventInserts(run.Conn);
        inserts.Count.ShouldBe(2, dialect);
        inserts.Select(e => (string?)Bound(e, "@type")).ShouldBe([ConsentEventTypes.GrantRenewed, ConsentEventTypes.GrantRevoked]);
        inserts.ShouldAllBe(e => (string?)Bound(e, "@subjectId") == created.Id.ToString("D"));
        using var revoked = JsonDocument.Parse((string)Bound(inserts[1], "@payload")!);
        revoked.RootElement.GetProperty("status").GetString().ShouldBe("REVOKED");
        revoked.RootElement.TryGetProperty("previousGrantId", out _).ShouldBeFalse();
        run.Harness.RevocationKinds.ShouldBe([SystemRevocationKind.Renewal, SystemRevocationKind.RenewalRollback]);
        run.Conn.Commands.Count(IsEventLock).ShouldBe(2, dialect);
        run.Conn.Commands.Count(c => c.CommandText.TrimStart().StartsWith("UPDATE CONSENTS", StringComparison.Ordinal)).ShouldBe(2, dialect);
    }

    private static DataTable ExistsRow(int value)
    {
        var table = new DataTable();
        table.Columns.Add("announced", typeof(int));
        table.Rows.Add(value);
        return table;
    }

    private static Consent RenewableGrant() => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = SelfGrantHarness.Orders,
        ConsentRequestId = Guid.NewGuid(),
        TenantId = SelfGrantHarness.Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("user:alice"),
        Effect = ConsentEffect.Allow,
        ValidFrom = SelfGrantHarness.Now.AddDays(-20),
        ValidTo = SelfGrantHarness.Now.AddDays(10),
        GrantKind = PermissionGrantKind.Data,
        GrantSource = ConsentGrantSource.ConsentRequest
    };

    private static GrantRenewalCommand RenewCommand(Consent grant, SecurityPrincipalContext renewer) =>
        new(grant.Id, renewer, SelfGrantHarness.Now.AddDays(90), "Quarterly renewal after access review.");

    private static List<RecordedCommand> EventInserts(RecordingDbConnection conn) =>
        conn.Commands.Where(c => c.CommandText.Contains("INSERT INTO CONSENT_EVENTS ", StringComparison.Ordinal)).ToList();

    private static object? Bound(RecordedCommand command, string name) => command.Parameters.Single(p => p.Name == name).Value;

    private static bool IsEventLock(RecordedCommand c) =>
        c.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) || c.CommandText.Contains("sp_getapplock", StringComparison.Ordinal);

    /// <summary>Within each revocation, the event lock and event insert follow the grant row update (and the epoch bump, where any).</summary>
    private static void AssertEventAppendedLast(RecordingDbConnection conn, string dialect, int revocations, bool expectEpochBump)
    {
        var commands = conn.Commands;
        var revokeUpdates = commands.Select((c, i) => (c, i))
            .Where(x => x.c.CommandText.TrimStart().StartsWith("UPDATE CONSENTS", StringComparison.Ordinal)).Select(x => x.i).ToList();
        var locks = commands.Select((c, i) => (c, i)).Where(x => IsEventLock(x.c)).Select(x => x.i).ToList();
        revokeUpdates.Count.ShouldBeGreaterThanOrEqualTo(revocations, dialect);
        locks.Count.ShouldBe(revocations, dialect);
        for (var n = 0; n < revocations; n++)
        {
            var writesBeforeLock = commands.Skip(revokeUpdates[n]).Take(locks[n] - revokeUpdates[n]).ToList();
            writesBeforeLock.ShouldNotContain(c => c.CommandText.Contains("INSERT INTO CONSENT_EVENTS", StringComparison.Ordinal));
            writesBeforeLock.Any(c => c.CommandText.Contains("POLICY_EPOCHS", StringComparison.Ordinal)).ShouldBe(expectEpochBump, dialect);

            // After the lock the transaction only appends the event; the next command, if any, opens the next revocation.
            var afterLock = commands.Skip(locks[n] + 1).ToList();
            afterLock.TakeWhile(IsEventCommand).ShouldContain(c => c.CommandText.Contains("INSERT INTO CONSENT_EVENTS ", StringComparison.Ordinal));
            var next = afterLock.SkipWhile(IsEventCommand).FirstOrDefault();
            if (next is not null && next.TransactionSet)
            {
                next.Kind.ShouldBe("Reader", dialect);
                next.CommandText.ShouldContain("FROM CONSENTS c", Case.Sensitive, dialect);
            }
        }
    }

    private static bool IsEventCommand(RecordedCommand c) =>
        c.CommandText.Contains("CONSENT_EVENT", StringComparison.Ordinal) || c.CommandText.Contains("COALESCE(MAX(seq)", StringComparison.Ordinal);

    private static (string Status, string WorkflowMode, Guid? RequestId) ReadThinGrantPayload(RecordedCommand insert)
    {
        using var doc = JsonDocument.Parse((string)Bound(insert, "@payload")!);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(ThinGrantPayloadProperties);
        root.GetProperty("grantId").GetGuid().ToString("D").ShouldBe((string?)Bound(insert, "@subjectId"));
        root.GetProperty("tenantId").GetString().ShouldBe(Tenant.Value);
        root.GetProperty("version").GetInt32().ShouldBe(1);
        Guid? requestId = root.TryGetProperty("requestId", out var r) ? r.GetGuid() : null;
        return (root.GetProperty("status").GetString()!, root.GetProperty("workflowMode").GetString()!, requestId);
    }

    private static Consent Grant(PermissionGrantKind kind) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
        GranteeType = GranteeType.User,
        GranteeSid = Leaver,
        GrantKind = kind,
        Effect = ConsentEffect.Allow,
        ValidFrom = Now.AddDays(-1),
        ValidTo = Now.AddDays(30)
    };

    private static DataTable RevokeTargetRow(string tenant, bool isRevoked, Guid? requestId)
    {
        var table = new DataTable();
        foreach (var column in new[] { "source_name", "schema_name", "table_name", "tenant_id", "grantee_type", "grantee_sid" })
        {
            table.Columns.Add(column, typeof(string));
        }

        table.Columns.Add("is_revoked", typeof(int));
        foreach (var column in new[] { "grant_source", "consent_request_id", "profile_assignment_id", "valid_from", "valid_to", "table_id", "effect", "object_pattern", "data_source" })
        {
            table.Columns.Add(column, typeof(string));
        }

        table.Rows.Add(
            "sales", "dbo", "orders", tenant, "User", Leaver.Value, isRevoked ? 1 : 0, "REQUEST",
            requestId?.ToString("D") ?? (object)DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value,
            Guid.NewGuid().ToString("D"), "Allow", DBNull.Value, DBNull.Value);
        return table;
    }

    private static DataTable SystemRevokeTargetRow()
    {
        var table = new DataTable();
        foreach (var column in new[]
                 {
                     "source_name", "schema_name", "table_name", "tenant_id", "table_id", "effect", "grantee_type", "grantee_sid", "grant_source",
                     "profile_assignment_id", "valid_from", "valid_to"
                 })
        {
            table.Columns.Add(column, typeof(string));
        }

        table.Rows.Add(
            "sales", "dbo", "orders", Tenant.Value, Guid.NewGuid().ToString("D"), "Allow", "User", Leaver.Value, "REQUEST",
            DBNull.Value, DBNull.Value, DBNull.Value);
        return table;
    }

    private static DataTable OriginRequestRow(Guid requestId, string workflowMode, Guid? externalSystemId)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("workflow_mode", typeof(string));
        table.Columns.Add("external_system_id", typeof(string));
        table.Rows.Add(requestId.ToString("D"), workflowMode, externalSystemId?.ToString("D") ?? (object)DBNull.Value);
        return table;
    }

    private static GovernanceRepository CreateRepo(string dialect, RecordingDbConnection conn, List<AuditLogEntry> audits)
    {
        conn.ScalarResult = 1;
        IGovernanceSqlDialect sqlDialect = dialect == "SqlServer"
            ? new SqlServerGovernanceDialect(() => conn)
            : new PostgreSqlGovernanceDialect(() => conn);
        return new GovernanceRepository(
            sqlDialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditRecorder: (entry, _, _) =>
            {
                audits.Add(entry);
                return Task.CompletedTask;
            },
            auditAnchorStore: new InMemoryAuditChainAnchorStore(),
            grantWriter: NullFake.Create<IGrantWriter>());
    }
}
