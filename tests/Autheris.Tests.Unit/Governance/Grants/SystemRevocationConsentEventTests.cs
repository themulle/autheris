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
/// grant in the revocation transaction, after every other write, with the thin payload and routing of the person revocation. The
/// revocations of a grant renewal (superseded grant, compensation of a failed renewal) append none.
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
                    (TenantId)args![0]!, (Guid)args[1]!, (Sid)args[2]!, (string)args[3]!, (SystemRevocationKind)args[4]!, (CancellationToken)args[5]!),
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

        // Successful renewal: the superseded grant is revoked through the real repository.
        var renewed = RenewableGrant();
        var success = new SelfGrantHarness(ProtectionClass.Internal) { Existing = renewed, RevokeThrough = repo };
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, renewed.ConsentRequestId));
        await success.Engine.RenewGrantAsync(RenewCommand(renewed, renewer));
        success.RevokedConsents.ShouldBe([renewed.Id]);

        // Failed renewal: ending the original grant fails, the compensation revokes the renewed grant through the real repository.
        var original = RenewableGrant();
        var failed = new SelfGrantHarness(ProtectionClass.Internal) { Existing = original, RevokeThrough = repo, FailRevokeConsent = id => id == original.Id };
        conn.EnqueueReader(RevokeTargetRow(SelfGrantHarness.Tenant.Value, isRevoked: false, original.ConsentRequestId));
        await Should.ThrowAsync<InvalidOperationException>(() => failed.Engine.RenewGrantAsync(RenewCommand(original, renewer)));
        failed.RevokedConsents.ShouldBe([failed.CreatedConsents.ShouldHaveSingleItem().Id]);

        success.RevocationKinds.Concat(failed.RevocationKinds).ShouldAllBe(k => k == SystemRevocationKind.Renewal);
        conn.Commands.Count(c => c.CommandText.TrimStart().StartsWith("UPDATE CONSENTS", StringComparison.Ordinal)).ShouldBe(2, dialect);
        conn.Commands.ShouldNotContain(c => c.CommandText.Contains("CONSENT_EVENT", StringComparison.Ordinal) || IsEventLock(c));
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
