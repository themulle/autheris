using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Issue #1121: four-eyes approval for grants on secret data through the permission request engine.</summary>
[Trait("Category", "Security")]
public sealed class SecretFourEyesApprovalTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    private static readonly Sid OwnerGroup = new("S-1-5-21-5-5-5-1001");
    private const string RejectReason = "The requested access is broader than needed.";

    [Theory]
    [InlineData(ConsentRequestKind.Data)]
    [InlineData(ConsentRequestKind.Unmask)]
    public async Task Secret_grant_is_not_effective_after_one_approval(ConsentRequestKind kind)
    {
        var h = new Harness(ProtectionClass.Secret, kind: kind);

        var first = await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        first.Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        first.Grants.ShouldBeEmpty();
        first.Request.DecidedBy.ShouldBeNull();
        h.Applier(kind).Applied.ShouldBe(0);
        var step = h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.PermissionApprovalRecorded);
        ReadInt(step.DetailsJson, "ApprovalStep").ShouldBe(1);
        ReadInt(step.DetailsJson, "RequiredApprovals").ShouldBe(2);
        h.Audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.PermissionApproved);
        h.Notifications.Messages.ShouldContain(m => m.EventType == AuditEventTypes.PermissionApprovalRecorded);

        var second = await h.Engine.DecideAsync(h.Approve(Dg("user:dg-2")));

        second.Request.Status.ShouldBe("APPROVED");
        second.Grants.ShouldHaveSingleItem();
        h.Applier(kind).Applied.ShouldBe(1);
        h.Store.Current.ApprovalSteps.Select(s => s.StepNumber).ShouldBe([1, 2]);
        h.Store.Current.ApprovalSteps.Select(s => s.ApproverSid.Value).ShouldBe(["user:dg-1", "user:dg-2"]);
        var approved = h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.PermissionApproved);
        ReadInt(approved.DetailsJson, "ApprovalStep").ShouldBe(2);
    }

    [Fact]
    public async Task Two_owner_group_members_complete_without_data_governance()
    {
        var h = new Harness(ProtectionClass.Secret, ownerGroup: OwnerGroup);

        (await h.Engine.DecideAsync(h.Approve(Owner("user:owner-1")))).Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        var done = await h.Engine.DecideAsync(h.Approve(Owner("user:owner-2")));

        done.Request.Status.ShouldBe("APPROVED");
        done.Request.ApproverCapacity.ShouldBe(ApproverCapacity.DataOwner);
        h.DataApplier.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Same_person_cannot_give_both_approvals()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-1"))));

        ex.StatusCode.ShouldBe(403);
        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
        h.Store.Current.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        h.DataApplier.Applied.ShouldBe(0);
        h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ConsentDenied && e.DetailsJson.Contains(ConsentDecisionErrorCodes.FourEyesSameApprover));
    }

    [Theory]
    [InlineData("S-1-5-21-100-200-300-1001", "alice.approver@corp.example")]
    [InlineData("alice.approver@corp.example", "S-1-5-21-100-200-300-1001")]
    [InlineData("user:alice.approver@corp.example", " ALICE.APPROVER@corp.example ")]
    public async Task Same_person_under_another_identifier_form_cannot_give_the_second_approval(string firstPrimary, string secondPrimary)
    {
        var h = new Harness(ProtectionClass.Secret);
        // First call: the token names the SID and the UPN. Second call: the same person, with another primary identifier form.
        var first = Dg(firstPrimary, extraClaims: [new Claim(ClaimTypes.Upn, "alice.approver@corp.example"), new Claim("onprem_sid", "S-1-5-21-100-200-300-1001")]);
        var second = Dg(secondPrimary);
        await h.Engine.DecideAsync(h.Approve(first));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(second)));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Stored_identifier_forms_are_compared_not_only_the_stored_sid()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("S-1-5-21-100-200-300-1001", extraClaims: [new Claim("oid", "6f1c2b8e-0000-4000-8000-000000000001")])));

        h.Store.Current.ApprovalSteps.Single().ApproverIdentifiers.ShouldContain("6f1c2b8e-0000-4000-8000-000000000001");
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() =>
            h.Engine.DecideAsync(h.Approve(Dg("6f1c2b8e-0000-4000-8000-000000000001"))));
        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
    }

    [Fact]
    public async Task Requester_cannot_give_either_approval()
    {
        var h = new Harness(ProtectionClass.Secret, requester: "user:dg-requester");

        var firstEx = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-requester"))));
        firstEx.ErrorCode.ShouldBe("SOD_VIOLATION");

        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));
        var secondEx = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-requester"))));
        secondEx.ErrorCode.ShouldBe("SOD_VIOLATION");
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Beneficiary_cannot_give_the_second_approval()
    {
        var h = new Harness(ProtectionClass.Secret, grantee: "user:dg-beneficiary");
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-beneficiary"))));

        ex.ErrorCode.ShouldBe("SOD_VIOLATION");
        h.Store.Current.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task One_rejection_after_the_first_approval_ends_the_request()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        var rejected = await h.Engine.DecideAsync(h.Reject(Dg("user:dg-2")));
        rejected.Request.Status.ShouldBe("REJECTED");

        var late = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-3"))));
        late.StatusCode.ShouldBe(409);
        late.ErrorCode.ShouldBe("REQUEST_ALREADY_DECIDED");
        h.DataApplier.Applied.ShouldBe(0);
        h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.PermissionRejected);
    }

    [Fact]
    public async Task One_rejection_before_any_approval_ends_the_request()
    {
        var h = new Harness(ProtectionClass.Secret);

        (await h.Engine.DecideAsync(h.Reject(Dg("user:dg-1")))).Request.Status.ShouldBe("REJECTED");

        (await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-2"))))).StatusCode.ShouldBe(409);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Theory]
    [InlineData(ProtectionClass.Confidential, null)]
    [InlineData(ProtectionClass.Internal, ProtectionClass.Confidential)]
    public async Task Non_secret_grants_stay_single_approval(ProtectionClass tableClass, ProtectionClass? columnClass)
    {
        var h = new Harness(tableClass, columnClass: columnClass, kind: ConsentRequestKind.Unmask);

        var result = await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        result.Request.Status.ShouldBe("APPROVED");
        h.UnmaskApplier.Applied.ShouldBe(1);
        h.Audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.PermissionApprovalRecorded);
    }

    [Fact]
    public async Task A_secret_column_makes_a_table_grant_four_eyes()
    {
        var h = new Harness(ProtectionClass.Internal, columnClass: ProtectionClass.Secret);

        (await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")))).Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task It_administrator_cannot_approve_secret_data()
    {
        // Without an owner group IT is an eligible approver of ordinary grants; for secret data only owner members and Data Governance decide.
        var h = new Harness(ProtectionClass.Secret);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Actor("user:it-1", "TenantAdmin"))));

        ex.StatusCode.ShouldBe(403);
        h.Store.Current.Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task Second_approval_cannot_extend_the_period_of_the_first()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1"), validTo: Now.AddDays(10)));

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-2"), validTo: Now.AddDays(20))));
        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ValidityExceedsRequested);

        var done = await h.Engine.DecideAsync(h.Approve(Dg("user:dg-2"), validTo: Now.AddDays(5)));
        done.Request.RequestedValidTo.ShouldBe(Now.AddDays(5));
        h.DataApplier.Last!.ValidTo.ShouldBe(Now.AddDays(5));
    }

    [Fact]
    public async Task Failed_audit_of_the_first_approval_records_nothing()
    {
        var h = new Harness(ProtectionClass.Secret, auditFailure: new InvalidOperationException("audit down"));

        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-1"))));

        h.Store.Current.Status.ShouldBe("PENDING");
        h.Store.Current.ApprovalSteps.ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_second_approvals_by_different_people_complete_exactly_once()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));

        // Both deciders read the same version before either decision runs.
        var commands = new[] { h.Approve(Dg("user:dg-2")), h.Approve(Dg("user:dg-3")) };
        var outcomes = await Task.WhenAll(commands.Select(c => Outcome(h.Engine.DecideAsync(c))));

        outcomes.Count(o => o == "APPROVED").ShouldBe(1);
        outcomes.Count(o => o == "REQUEST_ALREADY_DECIDED").ShouldBe(1);
        h.DataApplier.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_first_approvals_by_different_people_never_complete_with_a_stale_version()
    {
        var h = new Harness(ProtectionClass.Secret);

        // Both deciders read the same version before either decision runs.
        var commands = new[] { h.Approve(Dg("user:dg-1")), h.Approve(Dg("user:dg-2")) };
        var outcomes = await Task.WhenAll(commands.Select(c => Outcome(h.Engine.DecideAsync(c))));

        outcomes.Count(o => o == SecretFourEyesRule.PendingSecondApproval).ShouldBe(1);
        outcomes.Count(o => o == "REQUEST_ALREADY_DECIDED").ShouldBe(1);
        h.DataApplier.Applied.ShouldBe(0);

        // The second person decides against the current version and completes it exactly once.
        var winner = h.Store.Current.ApprovalSteps.Single().ApproverSid.Value;
        var other = winner == "user:dg-1" ? "user:dg-2" : "user:dg-1";
        (await h.Engine.DecideAsync(h.Approve(Dg(other)))).Request.Status.ShouldBe("APPROVED");
        h.DataApplier.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_approvals_by_the_same_person_never_complete()
    {
        var h = new Harness(ProtectionClass.Secret);

        // Both deciders read the same version before either decision runs.
        var commands = new[] { h.Approve(Dg("user:dg-1")), h.Approve(Dg("user:dg-1")) };
        var outcomes = await Task.WhenAll(commands.Select(c => Outcome(h.Engine.DecideAsync(c))));

        outcomes.ShouldNotContain("APPROVED");
        h.DataApplier.Applied.ShouldBe(0);
        h.Store.Current.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);

        var retry = await Should.ThrowAsync<ConsentDecisionException>(() => h.Engine.DecideAsync(h.Approve(Dg("user:dg-1"))));
        retry.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
        h.DataApplier.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Profile_assignment_covering_a_secret_table_needs_two_approvals()
    {
        var profileId = Guid.NewGuid();
        var profile = new AccessProfile { ProfileId = profileId.ToString("D"), TenantId = Tenant, Name = "finance", TargetTables = ["sales.dbo.orders", "sales.*"] };
        var h = new Harness(ProtectionClass.Secret, kind: ConsentRequestKind.ProfileAssignment, profile: profile, profileId: profileId);

        (await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")))).Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        h.ProfileApplier.Applied.ShouldBe(0);
        (await h.Engine.DecideAsync(h.Approve(Dg("user:dg-2")))).Request.Status.ShouldBe("APPROVED");
        h.ProfileApplier.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Profile_assignment_with_patterns_only_stays_single_approval()
    {
        // Pattern items never cover a secret table at evaluation time (see SecretFourEyesRuntimeTests), so they need no second approval.
        var profileId = Guid.NewGuid();
        var profile = new AccessProfile { ProfileId = profileId.ToString("D"), TenantId = Tenant, Name = "finance", TargetTables = ["sales.*"] };
        var h = new Harness(ProtectionClass.Secret, kind: ConsentRequestKind.ProfileAssignment, profile: profile, profileId: profileId, withTable: false);

        (await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")))).Request.Status.ShouldBe("APPROVED");
        h.ProfileApplier.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Decision_inbox_lists_a_request_awaiting_the_second_approval_to_other_deciders_only()
    {
        var h = new Harness(ProtectionClass.Secret);
        await h.Engine.DecideAsync(h.Approve(Dg("user:dg-1")));
        var inbox = h.Inbox();

        var forFirst = await inbox.GetInboxAsync(Dg("user:dg-1"), Tenant, null, 50);
        var forOther = await inbox.GetInboxAsync(Dg("user:dg-2"), Tenant, null, 50);
        var forIt = await inbox.GetInboxAsync(Actor("user:it-1", "TenantAdmin"), Tenant, null, 50);

        forFirst.Items.ShouldBeEmpty();
        forOther.Items.ShouldHaveSingleItem().Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        forIt.Items.ShouldBeEmpty();
    }

    private static async Task<string> Outcome(Task<PermissionDecisionResult> decision)
    {
        try
        {
            return (await decision).Request.Status;
        }
        catch (ConsentDecisionException ex)
        {
            return ex.ErrorCode;
        }
    }

    private static int? ReadInt(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }

    private static SecurityPrincipalContext Dg(string sid, IEnumerable<Claim>? extraClaims = null) => Actor(sid, "GovernanceAdmin", extraClaims: extraClaims);

    private static SecurityPrincipalContext Owner(string sid) => Actor(sid, "Reader", groups: [OwnerGroup]);

    private static SecurityPrincipalContext Actor(string sid, string role, IEnumerable<Sid>? groups = null, IEnumerable<Claim>? extraClaims = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("tenant_id", Tenant.Value), new(ClaimTypes.Role, role) };
        claims.AddRange(extraClaims ?? []);
        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = Tenant,
            ResolvedTenant = Tenant,
            GroupSids = new HashSet<Sid>(groups ?? []),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
    }

    /// <summary>Owner membership is the owner group SID in the principal's groups; every other capability is the real one.</summary>
    private sealed class OwnerGroupCapabilities : IGovernanceCapabilities
    {
        private readonly GovernanceCapabilities _inner = new();

        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => _inner.IsGovernance(principal, tenant);

        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => _inner.IsItAdmin(principal, tenant);

        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct) =>
            ValueTask.FromResult(principal.GroupSids.Contains(OwnerGroup));

        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) =>
            ValueTask.FromResult(false);

        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) => false;
    }

    /// <summary>
    /// The store semantics of <c>PermissionRequestStore.TryDecideAsync</c>: one row lock per request, a version CAS, the decidable statuses
    /// PENDING / PENDING_EXTERNAL_APPROVAL / PENDING_SECOND_APPROVAL, the approval steps read under the lock and nothing kept on failure.
    /// </summary>
    internal sealed class CasStore(ConsentRequest initial)
    {
        private readonly object _gate = new();
        private Task _rowLock = Task.CompletedTask;

        public ConsentRequest Current { get; private set; } = initial;

        public async Task<DecisionCasResult> TryDecideAsync(
            Guid requestId,
            int expectedVersion,
            Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>> decide,
            Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>> apply)
        {
            await Task.Yield();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task previous;
            lock (_gate)
            {
                previous = _rowLock;
                _rowLock = release.Task;
            }

            await previous;
            try
            {
                var req = Current;
                if (req.Id != requestId)
                {
                    return new DecisionCasResult(DecisionCasOutcome.NotFound);
                }

                if (req.Status is not ("PENDING" or "PENDING_EXTERNAL_APPROVAL" or "PENDING_SECOND_APPROVAL") || req.Version != expectedVersion)
                {
                    return new DecisionCasResult(DecisionCasOutcome.AlreadyDecided, req);
                }

                var tx = NullFake.Create<IGovernanceTransaction>();
                var record = await decide(tx, Copy(req, req.Status, req.Version, req.RequestedValidTo, req.ApprovalSteps, null));
                var steps = new List<ApprovalStep>(req.ApprovalSteps)
                {
                    new()
                    {
                        ConsentRequestId = req.Id,
                        StepNumber = record.StepNumber,
                        ApproverSid = record.DecidedBy,
                        Decision = record.StepDecision,
                        DecidedAt = record.DecidedAt,
                        RejectionReason = record.StepDecision == "REJECTED" ? record.Reason : null,
                        ApproverIdentifiers = [.. record.ApproverIdentifiers ?? []]
                    }
                };
                var updated = Copy(req, record.NewStatus, req.Version + 1, record.ApprovedValidTo, steps, record.IsFinal ? record : null);
                var grants = await apply(tx, record, updated);
                Current = updated;
                return new DecisionCasResult(DecisionCasOutcome.Applied, updated, grants);
            }
            finally
            {
                release.SetResult();
            }
        }

        private static ConsentRequest Copy(
            ConsentRequest r, string status, int version, DateTimeOffset validTo, List<ApprovalStep> steps, PermissionDecisionRecord? final) => new()
        {
            Id = r.Id,
            TableId = r.TableId,
            TableIdentifier = r.TableIdentifier,
            RequesterSid = r.RequesterSid,
            RequesterIdentifiers = r.RequesterIdentifiers,
            RequestedGranteeType = r.RequestedGranteeType,
            RequestedGranteeRef = r.RequestedGranteeRef,
            BusinessJustification = r.BusinessJustification,
            Status = status,
            RequestedAt = r.RequestedAt,
            RequestedValidFrom = r.RequestedValidFrom,
            RequestedValidTo = validTo,
            TenantId = r.TenantId,
            Version = version,
            RequestKind = r.RequestKind,
            GrantKind = r.GrantKind,
            ProfileId = r.ProfileId,
            ApprovalSteps = [.. steps],
            ApproverCapacity = final?.Capacity ?? r.ApproverCapacity,
            SelfGrant = final?.SelfGrant ?? r.SelfGrant,
            DecidedBy = final?.DecidedBy ?? r.DecidedBy,
            DecidedAt = final?.DecidedAt ?? r.DecidedAt
        };
    }

    private sealed class Harness
    {
        private readonly IConsentApprovalRepository _approvals;
        private readonly OwnerGroupCapabilities _capabilities = new();
        private readonly ApproverResolver _resolver;

        public Harness(
            ProtectionClass tableClass,
            ProtectionClass? columnClass = null,
            Sid? ownerGroup = null,
            ConsentRequestKind kind = ConsentRequestKind.Data,
            string requester = "user:requester",
            string grantee = "user:alice",
            AccessProfile? profile = null,
            Guid? profileId = null,
            bool withTable = true,
            Exception? auditFailure = null)
        {
            var columns = new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase);
            if (columnClass is { } cc)
            {
                columns["ssn"] = new ColumnProtection(cc, null);
            }

            var protection = new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), ownerGroup, tableClass, tableClass, null, columns, default);
            var provider = NullFake.Create<IProtectionSnapshotProvider>((_, _) => new ValueTask<EffectiveProtection>(protection));
            Audit = new InMemoryAuditWriter(auditFailure);
            Store = new CasStore(new ConsentRequest
            {
                Id = Guid.NewGuid(),
                TableId = Guid.NewGuid(),
                TableIdentifier = withTable ? Orders : default,
                RequesterSid = new Sid(requester),
                RequestedGranteeType = GranteeType.User,
                RequestedGranteeRef = grantee,
                BusinessJustification = "Quarter close needs the secret payroll figures.",
                Status = "PENDING",
                RequestedAt = Now.AddMinutes(-10),
                RequestedValidFrom = Now,
                RequestedValidTo = Now.AddDays(30),
                TenantId = Tenant,
                Version = 1,
                RequestKind = kind,
                GrantKind = kind == ConsentRequestKind.Unmask ? PermissionGrantKind.Unmask : PermissionGrantKind.Data,
                ProfileId = profileId
            });

            _approvals = NullFake.Create<IConsentApprovalRepository>((m, args) => m.Name switch
            {
                nameof(IConsentApprovalRepository.TryDecideAsync) => Store.TryDecideAsync(
                    (Guid)args![0]!,
                    (int)args[1]!,
                    (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args[2]!,
                    (Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>>)args[3]!),
                nameof(IConsentApprovalRepository.GetConsentRequestAsync) => Task.FromResult<ConsentRequest?>(Store.Current),
                nameof(IConsentApprovalRepository.QueryTenantConsentRequestsAsync) => Task.FromResult(Page((ConsentRequestPageQuery)args![0]!)),
                _ => NullFake.UseDefault
            });

            var profiles = NullFake.Create<IAccessProfileRepository>((m, _) =>
                m.Name == nameof(IAccessProfileRepository.GetProfileAsync) ? Task.FromResult(profile) : NullFake.UseDefault);
            _resolver = new ApproverResolver(protectionSnapshotProvider: provider);
            Engine = new PermissionRequestEngine(
                _approvals,
                Audit,
                _resolver,
                new ApproverEligibility(_capabilities),
                new SeparationOfDutiesService(),
                new GrantPolicy(),
                GrantValidityPolicy.CreateDefault(),
                [DataApplier, UnmaskApplier, ProfileApplier],
                protectionProvider: provider,
                accessProfileRepository: profiles,
                notificationDispatcher: Notifications,
                capabilities: _capabilities,
                timeProvider: new FakeTimeProvider(Now));
        }

        public CasStore Store { get; }

        public InMemoryAuditWriter Audit { get; }

        public RecordingNotificationDispatcher Notifications { get; } = new();

        public StubGrantApplier DataApplier { get; } = new(ConsentRequestKind.Data);

        public StubGrantApplier UnmaskApplier { get; } = new(ConsentRequestKind.Unmask);

        public StubGrantApplier ProfileApplier { get; } = new(ConsentRequestKind.ProfileAssignment);

        public PermissionRequestEngine Engine { get; }

        public StubGrantApplier Applier(ConsentRequestKind kind) => kind switch
        {
            ConsentRequestKind.Unmask => UnmaskApplier,
            ConsentRequestKind.ProfileAssignment => ProfileApplier,
            _ => DataApplier
        };

        public PermissionDecisionCommand Approve(SecurityPrincipalContext decider, DateTimeOffset? validTo = null) =>
            new(Store.Current.Id, Store.Current.Version, decider, Approve: true, ValidTo: validTo);

        public PermissionDecisionCommand Reject(SecurityPrincipalContext decider) =>
            new(Store.Current.Id, Store.Current.Version, decider, Approve: false, Reason: RejectReason);

        public PermissionInboxService Inbox() => new(
            _approvals,
            NullFake.Create<IConsentRepository>(),
            _resolver,
            new ApproverEligibility(_capabilities),
            new SeparationOfDutiesService(),
            _capabilities);

        private ConsentRequestPage Page(ConsentRequestPageQuery query)
        {
            var current = Store.Current;
            var matches = string.Equals(current.Status, query.Status, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(current.Status, query.AdditionalStatus, StringComparison.OrdinalIgnoreCase);
            // The page query reads no approval steps; the inbox reads them with the request when it needs them.
            var row = new ConsentRequest
            {
                Id = current.Id,
                TableId = current.TableId,
                TableIdentifier = current.TableIdentifier,
                RequesterSid = current.RequesterSid,
                RequestedGranteeType = current.RequestedGranteeType,
                RequestedGranteeRef = current.RequestedGranteeRef,
                Status = current.Status,
                TenantId = current.TenantId,
                Version = current.Version,
                RequestKind = current.RequestKind,
                RequestedAt = current.RequestedAt,
                RequestedValidTo = current.RequestedValidTo
            };
            return new ConsentRequestPage(matches ? [row] : [], null);
        }
    }
}
