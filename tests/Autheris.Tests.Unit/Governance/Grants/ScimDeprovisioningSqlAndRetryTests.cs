namespace Autheris.Tests.Unit.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Unit.Persistence.DialectSql;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimDeprovisioningSqlAndRetryTests
{
    private static readonly TenantId Tenant = new("tenant-w5");
    private static readonly Sid Subject = new("S-1-5-21-1000-2000-3000-9001");
    private static readonly Sid OtherRequester = new("S-1-5-21-1000-2000-3000-9999");
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Dialects() => DialectKit.Names();

    [Fact]
    public async Task RevokeAllForSubjectAsync_WithdrawsPendingExternalAndSecondApprovalAndGranteeTargetedRequests()
    {
        var reqPending = NewRequest(Subject, Subject, GranteeType.User, "PENDING");
        var reqExt = NewRequest(Subject, Subject, GranteeType.User, "PENDING_EXTERNAL_APPROVAL");
        var reqSecond = NewRequest(Subject, Subject, GranteeType.User, "PENDING_SECOND_APPROVAL");
        var reqForGrantee = NewRequest(OtherRequester, Subject, GranteeType.User, "PENDING_EXTERNAL_APPROVAL");
        var reqForGroupSameId = NewRequest(OtherRequester, Subject, GranteeType.Group, "PENDING");
        var withdrawn = new List<Guid>();

        var approvals = CreateApprovalsFake([reqPending, reqExt, reqSecond, reqForGrantee, reqForGroupSameId], withdrawn);
        var consents = CreateConsentsFake([], new Dictionary<Guid, int>(), []);
        var sut = new GrantRevocationService(consents, approvals, new RecordingAuditWriter(), timeProvider: new FakeTimeProvider(Now));

        var result = await sut.RevokeAllForSubjectAsync(Tenant, Subject, "SCIM_DEPROVISION");

        result.Complete.ShouldBeTrue();
        result.RequestsWithdrawn.ShouldBe(4);
        withdrawn.ShouldBe([reqPending.Id, reqExt.Id, reqSecond.Id, reqForGrantee.Id], ignoreOrder: true);
        withdrawn.ShouldNotContain(reqForGroupSameId.Id);
    }

    [Fact]
    public async Task RevokeAllForSubjectAsync_RecordsAuditBeforeMutation_SoAuditFailureLeavesItemsActiveForRetry()
    {
        var consent = NewConsent();
        var catalog = new CatalogVisibilityGrant
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            ContainerKind = ContainerKind.Domain,
            ContainerId = Guid.NewGuid(),
            GranteeSid = Subject
        };
        var req = NewRequest(Subject, Subject, GranteeType.User, "PENDING_EXTERNAL_APPROVAL");

        var revokedConsents = new List<Guid>();
        var revokedCatalog = new List<Guid>();
        var withdrawnReqs = new List<Guid>();

        var consents = NullFake.Create<IConsentRepository>((m, args) => m.Name switch
        {
            nameof(IConsentRepository.GetActiveGrantsForSubjectsAsync) =>
                Task.FromResult<IReadOnlyList<Consent>>(revokedConsents.Contains(consent.Id) ? [] : [consent]),
            nameof(IConsentRepository.RevokeSystemConsentByIdAsync) =>
                RecordBool(() => revokedConsents.Add((Guid)args![1]!)),
            _ => NullFake.UseDefault
        });
        var catalogRepo = NullFake.Create<ICatalogVisibilityGrantRepository>((m, args) => m.Name switch
        {
            nameof(ICatalogVisibilityGrantRepository.ListActiveForSubjectsAsync) =>
                Task.FromResult<IReadOnlyList<CatalogVisibilityGrant>>(revokedCatalog.Contains(catalog.Id) ? [] : [catalog]),
            "ListForDeprovisioningAsync" =>
                Task.FromResult<IReadOnlyList<CatalogVisibilityGrant>>(revokedCatalog.Contains(catalog.Id) ? [] : [catalog]),
            nameof(ICatalogVisibilityGrantRepository.RevokeAsync) =>
                RecordBool(() => revokedCatalog.Add((Guid)args![1]!)),
            _ => NullFake.UseDefault
        });
        var approvals = CreateApprovalsFake([req], withdrawnReqs);
        var audit = new RecordingAuditWriter { ShouldFail = true };
        var sut = new GrantRevocationService(consents, approvals, audit, catalogRepo, timeProvider: new FakeTimeProvider(Now));

        var failedRun = await sut.RevokeAllForSubjectAsync(Tenant, Subject, "SCIM_DEPROVISION");

        failedRun.Complete.ShouldBeFalse();
        failedRun.Failed.ShouldBe(3);
        revokedConsents.ShouldBeEmpty();
        revokedCatalog.ShouldBeEmpty();
        withdrawnReqs.ShouldBeEmpty();

        audit.ShouldFail = false;
        var retryRun = await sut.RevokeAllForSubjectAsync(Tenant, Subject, "SCIM_DEPROVISION");

        retryRun.Complete.ShouldBeTrue();
        retryRun.GrantsRevoked.ShouldBe(1);
        retryRun.CatalogGrantsRevoked.ShouldBe(1);
        retryRun.RequestsWithdrawn.ShouldBe(1);
        audit.Entries.Count(e => e.EventType == AuditEventTypes.GrantRevokedDeprovisioned).ShouldBe(2);
        audit.Entries.Count(e => e.EventType == AuditEventTypes.ConsentWithdrawn).ShouldBe(1);
    }

    [Fact]
    public async Task RevokeAllForSubjectAsync_DoesNotRetryFailingGrantInLoop_AndAdvancesPastBatchBoundary()
    {
        var grants = Enumerable.Range(0, 250)
            .Select(_ => NewConsent())
            .OrderBy(c => c.Id.ToString("D"), StringComparer.Ordinal)
            .ToList();
        var failingId = grants[0].Id;
        var attempts = new Dictionary<Guid, int>();
        var revoked = new HashSet<Guid>();
        var page1Caps = new List<int>();
        var afterIds = new List<Guid?>();
        var consents = CreateConsentsFake(grants, attempts, revoked, failId: failingId, page1Caps: page1Caps, afterIds: afterIds);
        var approvals = CreateApprovalsFake([], []);
        var sut = new GrantRevocationService(consents, approvals, new RecordingAuditWriter(), timeProvider: new FakeTimeProvider(Now));

        var result = await sut.RevokeAllForSubjectAsync(Tenant, Subject, "SCIM_DEPROVISION");

        result.Complete.ShouldBeFalse();
        result.Failed.ShouldBe(1);
        result.GrantsRevoked.ShouldBe(249);
        page1Caps.ShouldHaveSingleItem().ShouldBe(200);
        afterIds.ShouldHaveSingleItem().ShouldBe(grants[199].Id);
        attempts[failingId].ShouldBe(1);
        revoked.Count.ShouldBe(249);
        revoked.ShouldNotContain(failingId);

        // Seen-ID guard prevents spinning even if a broken store ignores afterId and repeats batch 1.
        var dupAttempts = new Dictionary<Guid, int>();
        var dupRevoked = new HashSet<Guid>();
        var nonAdvancingConsents = CreateConsentsFake(grants, dupAttempts, dupRevoked, failId: failingId, ignoreAfterId: true);
        var dupSut = new GrantRevocationService(nonAdvancingConsents, approvals, new RecordingAuditWriter(), timeProvider: new FakeTimeProvider(Now));

        var dupResult = await dupSut.RevokeAllForSubjectAsync(Tenant, Subject, "SCIM_DEPROVISION");

        dupResult.Complete.ShouldBeFalse();
        dupResult.Failed.ShouldBe(1);
        dupAttempts[failingId].ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetActiveGrantsForSubjectsAsync_PushesValidToIntoSqlBeforeFetchNext_AndIncludesFutureGrants(string dialectName)
    {
        var kit = DialectKit.Of(dialectName);
        using var conn = kit.NewConnection();
        var futureGrantId = Guid.NewGuid();
        conn.EnqueueReader(ConsentTable(futureGrantId, Now.AddDays(5), Now.AddDays(30)));
        await using var repo = CreateRepo(dialectName, conn);

        var grants = await repo.GetActiveGrantsForSubjectsAsync(Tenant, [Subject], Now, maxResults: 25);

        grants.ShouldContain(g => g.Id == futureGrantId);
        var select = conn.Commands.First(c => c.CommandText.Contains("FROM CONSENTS", StringComparison.Ordinal));
        var validToIdx = select.CommandText.IndexOf("c.valid_to IS NULL OR c.valid_to > @atTime", StringComparison.Ordinal);
        var fetchIdx = select.CommandText.IndexOf("FETCH NEXT", StringComparison.Ordinal);
        validToIdx.ShouldBeGreaterThan(0);
        fetchIdx.ShouldBeGreaterThan(validToIdx);
        select.Parameters.ShouldContain(p => p.Name == "@atTime" && Equals(p.Value, Now.ToUniversalTime().ToString("O")));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetActiveGrantsForSubjectsAfterAsync_BindsKeysetCursorAndValidTo(string dialectName)
    {
        var kit = DialectKit.Of(dialectName);
        using var conn = kit.NewConnection();
        var afterId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        conn.EnqueueReader(ConsentTable(nextId, Now.AddDays(-1), Now.AddDays(30)));
        await using var repo = CreateRepo(dialectName, conn);

        var grants = await repo.GetActiveGrantsForSubjectsAfterAsync(Tenant, [Subject], Now, afterId, maxResults: 10);

        grants.ShouldHaveSingleItem().Id.ShouldBe(nextId);
        var select = conn.Commands.First(c => c.CommandText.Contains("FROM CONSENTS", StringComparison.Ordinal));
        select.CommandText.ShouldContain("AND c.id > @afterId");
        select.CommandText.ShouldContain("c.valid_to IS NULL OR c.valid_to > @atTime");
        select.Parameters.ShouldContain(p => p.Name == "@afterId" && Equals(p.Value, afterId.ToString("D")));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetAllActiveConsentsForSubjectsAsync_StillExcludesFutureAllowGrants(string dialectName)
    {
        var kit = DialectKit.Of(dialectName);
        using var conn = kit.NewConnection();
        var futureGrantId = Guid.NewGuid();
        conn.EnqueueReader(ConsentTable(futureGrantId, Now.AddDays(5), Now.AddDays(30)));
        await using var repo = CreateRepo(dialectName, conn);

        var grants = await repo.GetAllActiveConsentsForSubjectsAsync([Subject], null, Now, Tenant);

        grants.ShouldNotContain(g => g.Id == futureGrantId);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetPendingRequestsForSubjectAsync_FiltersTenantAllPendingStatusesAndRequesterOrGrantee(string dialectName)
    {
        var kit = DialectKit.Of(dialectName);
        using var conn = kit.NewConnection();
        var userGranteeReqId = Guid.NewGuid();
        var groupColidingReqId = Guid.NewGuid();
        conn.EnqueueReader(PendingRequestTable(
            (userGranteeReqId, OtherRequester.Value, "User", Subject.Value, "PENDING_EXTERNAL_APPROVAL"),
            (groupColidingReqId, OtherRequester.Value, "Group", Subject.Value, "PENDING")));
        await using var repo = CreateRepo(dialectName, conn);

        var results = await repo.GetPendingRequestsForSubjectAsync(Subject, Tenant);

        results.ShouldHaveSingleItem().Id.ShouldBe(userGranteeReqId);
        var select = conn.Commands.First(c => c.CommandText.Contains("FROM CONSENT_REQUESTS", StringComparison.Ordinal));
        select.CommandText.ShouldContain("r.tenant_id = @tenantId");
        select.CommandText.ShouldContain("'PENDING_EXTERNAL_APPROVAL'");
        select.CommandText.ShouldContain("r.requested_grantee_ref = @sub");
        select.Parameters.ShouldContain(p => p.Name == "@tenantId" && Equals(p.Value, Tenant.Value));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task CatalogVisibilityGrantStore_ListForDeprovisioningAsync_IncludesFutureGrants_WhileListActiveExcludesThem(string dialectName)
    {
        var kit = DialectKit.Of(dialectName);
        using var conn = kit.NewConnection();
        conn.ScalarResult = 1;
        var futureId = Guid.NewGuid();
        conn.EnqueueReader(CatalogGrantTable(futureId, Now.AddDays(3), Now.AddDays(30)));
        conn.EnqueueReader(CatalogGrantTable(futureId, Now.AddDays(3), Now.AddDays(30)));

        IGovernanceSqlDialect dialect = dialectName == "SqlServer"
            ? new SqlServerGovernanceDialect(() => conn)
            : new PostgreSqlGovernanceDialect(() => conn);
        var store = new CatalogVisibilityGrantStore(dialect, new FakeTimeProvider(Now));

        var forDeprov = await store.ListForDeprovisioningAsync(Tenant, [Subject], Now);
        var activeOnly = await store.ListActiveForSubjectsAsync(Tenant, [Subject], Now);

        forDeprov.ShouldHaveSingleItem().Id.ShouldBe(futureId);
        activeOnly.ShouldBeEmpty();
        var deprovSql = conn.Commands.First(c => c.CommandText.Contains("FROM", StringComparison.Ordinal));
        deprovSql.CommandText.ShouldNotContain("valid_from <= @atTime");
        var activeSql = conn.Commands.Last(c => c.CommandText.Contains("FROM", StringComparison.Ordinal));
        activeSql.CommandText.ShouldContain("valid_from <= @atTime");
    }

    private static IConsentRepository CreateConsentsFake(
        List<Consent> grants,
        Dictionary<Guid, int> attempts,
        HashSet<Guid> revoked,
        Guid? failId = null,
        List<int>? page1Caps = null,
        List<Guid?>? afterIds = null,
        bool ignoreAfterId = false) =>
        NullFake.Create<IConsentRepository>((m, args) =>
        {
            switch (m.Name)
            {
                case nameof(IConsentRepository.GetActiveGrantsForSubjectsAsync):
                {
                    var cap = (int)args![3]!;
                    page1Caps?.Add(cap);
                    var rows = grants.Where(g => !revoked.Contains(g.Id)).Take(cap > 0 ? cap : grants.Count).ToList();
                    return Task.FromResult<IReadOnlyList<Consent>>(rows);
                }
                case nameof(IConsentRepository.GetActiveGrantsForSubjectsAfterAsync):
                {
                    var afterId = (Guid?)args![3];
                    var cap = (int)args[4]!;
                    afterIds?.Add(afterId);
                    var afterStr = ignoreAfterId ? null : afterId?.ToString("D");
                    var rows = grants
                        .Where(g => !revoked.Contains(g.Id) && (afterStr is null || string.Compare(g.Id.ToString("D"), afterStr, StringComparison.Ordinal) > 0))
                        .Take(cap > 0 ? cap : grants.Count)
                        .ToList();
                    return Task.FromResult<IReadOnlyList<Consent>>(rows);
                }
                case nameof(IConsentRepository.RevokeSystemConsentByIdAsync):
                {
                    var id = (Guid)args![1]!;
                    attempts[id] = attempts.GetValueOrDefault(id) + 1;
                    if (failId == id)
                    {
                        throw new InvalidOperationException("transient revoke failure");
                    }

                    revoked.Add(id);
                    return Task.FromResult(true);
                }
                default:
                    return NullFake.UseDefault;
            }
        });

    private static IConsentApprovalRepository CreateApprovalsFake(List<ConsentRequest> pending, List<Guid> withdrawn) =>
        NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            switch (m.Name)
            {
                case nameof(IConsentApprovalRepository.GetRequestsByRequesterSidAsync):
                {
                    var reqSid = (Sid)args![0]!;
                    var status = (string?)args[2];
                    var rows = pending.Where(r =>
                        !withdrawn.Contains(r.Id) &&
                        r.RequesterSid == reqSid &&
                        (status is null || string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase))).ToList();
                    return Task.FromResult<IReadOnlyList<ConsentRequest>>(rows);
                }
                case "GetPendingRequestsForSubjectAsync":
                {
                    var sub = (Sid)args![0]!;
                    var rows = pending.Where(r =>
                        !withdrawn.Contains(r.Id) &&
                        r.Status is "PENDING" or "PENDING_SECOND_APPROVAL" or "PENDING_EXTERNAL_APPROVAL" &&
                        (r.RequesterSid == sub ||
                         (r.RequestedGranteeType != GranteeType.Group && new Sid(r.RequestedGranteeRef) == sub))).ToList();
                    return Task.FromResult<IReadOnlyList<ConsentRequest>>(rows);
                }
                case nameof(IConsentApprovalRepository.WithdrawConsentRequestAsync):
                    withdrawn.Add((Guid)args![0]!);
                    return Task.FromResult(true);
                default:
                    return NullFake.UseDefault;
            }
        });

    private static Task<bool> RecordBool(Action act)
    {
        act();
        return Task.FromResult(true);
    }

    private static Consent NewConsent() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        TableIdentifier = new TableIdentifier("s", "dbo", "t"),
        GranteeSid = Subject,
        GrantKind = PermissionGrantKind.Data,
        Effect = ConsentEffect.Allow,
        ValidFrom = Now.AddDays(-1),
        ValidTo = Now.AddDays(30)
    };

    private static ConsentRequest NewRequest(Sid requester, Sid grantee, GranteeType type, string status) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        TableIdentifier = new TableIdentifier("s", "dbo", "t"),
        RequesterSid = requester,
        RequestedGranteeType = type,
        RequestedGranteeRef = grantee.Value,
        Status = status
    };

    private static DataTable ConsentTable(Guid id, DateTimeOffset validFrom, DateTimeOffset validTo)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("table_id", typeof(string));
        table.Columns.Add("consent_request_id", typeof(string));
        table.Columns.Add("effect", typeof(string));
        table.Columns.Add("grantee_type", typeof(string));
        table.Columns.Add("grantee_sid", typeof(string));
        table.Columns.Add("valid_from", typeof(string));
        table.Columns.Add("valid_to", typeof(string));
        table.Columns.Add("is_revoked", typeof(int));
        table.Columns.Add("source_name", typeof(string));
        table.Columns.Add("schema_name", typeof(string));
        table.Columns.Add("table_name", typeof(string));
        table.Columns.Add("tenant_id", typeof(string));
        table.Columns.Add("grant_source", typeof(string));
        table.Columns.Add("grant_record_seq", typeof(long));
        table.Columns.Add("profile_assignment_id", typeof(string));
        table.Columns.Add("expiry_notice_stage", typeof(int));
        table.Rows.Add(
            id.ToString("D"),
            Guid.NewGuid().ToString("D"),
            DBNull.Value,
            "Allow",
            "User",
            Subject.Value,
            validFrom.ToUniversalTime().ToString("O"),
            validTo.ToUniversalTime().ToString("O"),
            0,
            "sales",
            "dbo",
            "orders",
            Tenant.Value,
            "DECLARATIVE",
            1L,
            DBNull.Value,
            0);
        return table;
    }

    private static DataTable PendingRequestTable(params (Guid Id, string RequesterSid, string GranteeType, string GranteeRef, string Status)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("table_id", typeof(string));
        table.Columns.Add("requester_sid", typeof(string));
        table.Columns.Add("requested_grantee_type", typeof(string));
        table.Columns.Add("requested_grantee_ref", typeof(string));
        table.Columns.Add("business_justification", typeof(string));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("requested_at", typeof(string));
        table.Columns.Add("requested_valid_to", typeof(string));
        table.Columns.Add("source_name", typeof(string));
        table.Columns.Add("schema_name", typeof(string));
        table.Columns.Add("table_name", typeof(string));
        foreach (var r in rows)
        {
            table.Rows.Add(
                r.Id.ToString("D"),
                Guid.NewGuid().ToString("D"),
                r.RequesterSid,
                r.GranteeType,
                r.GranteeRef,
                "access needed",
                r.Status,
                Now.AddHours(-1).ToUniversalTime().ToString("O"),
                Now.AddDays(7).ToUniversalTime().ToString("O"),
                "sales",
                "dbo",
                "orders");
        }

        return table;
    }

    private static DataTable CatalogGrantTable(Guid id, DateTimeOffset validFrom, DateTimeOffset validTo)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("tenant_id", typeof(string));
        table.Columns.Add("container_kind", typeof(string));
        table.Columns.Add("container_id", typeof(string));
        table.Columns.Add("grantee_type", typeof(string));
        table.Columns.Add("grantee_sid", typeof(string));
        table.Columns.Add("valid_from", typeof(DateTimeOffset));
        table.Columns.Add("valid_to", typeof(DateTimeOffset));
        table.Columns.Add("granted_by_sid", typeof(string));
        table.Columns.Add("approver_capacity", typeof(string));
        table.Columns.Add("request_id", typeof(string));
        table.Columns.Add("is_revoked", typeof(bool));
        table.Columns.Add("revoked_by_sid", typeof(string));
        table.Columns.Add("revoked_at", typeof(DateTimeOffset));
        table.Columns.Add("revoke_reason", typeof(string));
        table.Columns.Add("created_at", typeof(DateTimeOffset));
        table.Rows.Add(
            id.ToString("D"),
            Tenant.Value,
            "DOMAIN",
            Guid.NewGuid().ToString("D"),
            "User",
            Subject.Value,
            validFrom.ToUniversalTime(),
            validTo.ToUniversalTime(),
            OtherRequester.Value,
            "DATA_OWNER",
            DBNull.Value,
            false,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            Now.ToUniversalTime());
        return table;
    }

    private static GovernanceRepository CreateRepo(string dialectName, RecordingDbConnection conn)
    {
        conn.ScalarResult = 1;
        IGovernanceSqlDialect dialect = dialectName == "SqlServer"
            ? new SqlServerGovernanceDialect(() => conn)
            : new PostgreSqlGovernanceDialect(() => conn);
        return new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditAnchorStore: new InMemoryAuditChainAnchorStore(),
            grantWriter: new StubGrantWriter());
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public bool ShouldFail { get; set; }
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (!ShouldFail)
            {
                Entries.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (ShouldFail)
            {
                throw new InvalidOperationException("audit store unavailable");
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class StubGrantWriter : IGrantWriter
    {
        private static GrantRecord Dummy(GrantRecordType type, Consent? c, GrantRecordInput input) =>
            new(input.TenantId, 1L, Guid.NewGuid(), type, c?.Id, input.RequestId,
                c?.GrantSource ?? ConsentGrantSource.Declarative, c?.ProfileAssignmentId,
                input.GranteeType ?? "User", input.GranteeRef ?? string.Empty, input.ObjectId ?? string.Empty,
                input.ScopeJson, input.ValidFrom, input.ValidUntil, input.RequestedBy,
                input.DecisionJson, input.DecidedAt, input.DecidedAt, GrantRecordChain.GenesisHash);

        public Task<GrantRecord> InsertAndGrantAsync(System.Data.Common.DbTransaction tx, Consent c, GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(Dummy(GrantRecordType.Issued, c, input));
        public Task<GrantRecord> GrantAsync(System.Data.Common.DbTransaction tx, Consent c, GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(Dummy(GrantRecordType.Issued, c, input));
        public Task<GrantRecord> RevokeAsync(System.Data.Common.DbTransaction tx, Consent c, GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(Dummy(GrantRecordType.Revoked, c, input));
        public Task<GrantRecord> RecordRejectionAsync(System.Data.Common.DbTransaction tx, GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(Dummy(GrantRecordType.Rejected, null, input));
        public Task<GrantRecord> RecordExpiryAsync(System.Data.Common.DbTransaction tx, Consent? c, GrantRecordInput input, CancellationToken ct = default) =>
            Task.FromResult(Dummy(GrantRecordType.Expired, c, input));
    }
}
