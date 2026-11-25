using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Declarative;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Declarative;

/// <summary>
/// Amendment 6 (SEC-1009-29): IT submits declarative manifests as proposals that have no effect until one Data Governance confirmation; the confirmer
/// is never the submitter nor a beneficiary, a confirmation re-validates against the current state, and the first decision wins.
/// </summary>
[Trait("Category", "Security")]
public sealed class ManifestProposalServiceTests
{
    private const string ItSid = "S-1-5-21-IT-ADMIN";
    private const string DgSid = "S-1-5-21-DG-ADMIN";
    private const string DgGroup = "S-1-5-21-1-2-3-4401";
    private const string Justification = "quarterly analytics access";
    private static readonly DateTimeOffset ValidUntil = DateTimeOffset.UtcNow.AddDays(30);

    private readonly FakeConsentRepository _consents = new();
    private readonly InMemoryManifestProposalRepository _proposals = new();
    private readonly InMemoryAuditWriter _audit = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private bool _sensitive;

    private static ClaimsPrincipal Caller(string sid, string role, params Claim[] extra) => new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, sid), new Claim(ClaimTypes.Role, role), .. extra], "test"));

    private static ClaimsPrincipal It(params Claim[] extra) => Caller(ItSid, "TenantAdmin", extra);

    private static ClaimsPrincipal Dg(params Claim[] extra) => Caller(DgSid, "GovernanceAdmin", extra);

    private ITableMetadataRepository Tables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                return id.TableName.Contains('*', StringComparison.Ordinal)
                    ? null
                    : new TableMetadata
                    {
                        Table = new Table { Id = Guid.NewGuid() },
                        Identifier = id,
                        Columns = [new TableColumn { ColumnName = "id" }],
                        ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(_sensitive ? new[] { "id" } : Array.Empty<string>())
                    };
            });
        return repo;
    }

    private DeclarativeConsentApplyService Planner() => new(Tables(), _consents, Substitute.For<IAuditLogRepository>());

    private ManifestProposalService Sut(DeclarativeConsentApplyService? planner = null) =>
        new(planner ?? Planner(), _proposals, _audit, timeProvider: _time);

    private static DeclarativeConsentPolicy Policy(
        string type = "Group",
        string granteeRef = "S-1-5-21-GROUP-A",
        string? description = Justification,
        string table = "orders",
        bool noEndDate = false) =>
        new("default", "sales", table, type, granteeRef, "Allow",
            Description: description,
            ValidUntil: noEndDate ? null : ValidUntil);

    private static DeclarativeConsentManifest Manifest(params DeclarativeConsentPolicy[] policies) => new("1", policies, Name: "analytics");

    private IReadOnlyList<string> AuditTypes() => _audit.Entries.Select(e => e.EventType).ToList();

    [Fact]
    public async Task It_submit_has_no_effect_until_confirmed()
    {
        var sut = Sut();

        var proposal = await sut.SubmitAsync(It(), Manifest(Policy()));

        proposal.Status.ShouldBe(nameof(ManifestProposalStatus.Pending));
        proposal.SubmittedBy.ShouldBe(ItSid);
        proposal.Diff.ShouldHaveSingleItem().Action.ShouldBe(DeclarativeDiffAction.Add);
        proposal.Diff[0].Justification.ShouldBe(Justification);
        _consents.ReplaceCalls.ShouldBe(0);
        _consents.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
        AuditTypes().ShouldBe([AuditEventTypes.ManifestProposalSubmitted]);

        var result = await sut.ConfirmAsync(Dg(), proposal.Id);

        result.Applied.ShouldBe(1);
        _consents.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem().Description.ShouldBe(Justification);
        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Confirmed);
        _proposals.Single(proposal.Id).DecidedBy.ShouldBe(new Sid(DgSid));
        AuditTypes().ShouldBe([AuditEventTypes.ManifestProposalSubmitted, AuditEventTypes.ManifestProposalConfirmed]);

        // the resulting grant audit entries reference the proposal
        var applied = _consents.ReplaceAuditEntries.ShouldHaveSingleItem();
        JsonDocument.Parse(applied.DetailsJson).RootElement.GetProperty("ProposalId").GetGuid().ShouldBe(proposal.Id);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("ClusterAdmin")]
    public async Task It_cannot_confirm_or_reject(string role)
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy()));
        var other = Caller("S-1-5-21-OTHER-IT", role);

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() => sut.ConfirmAsync(other, proposal.Id));
        await Should.ThrowAsync<ConsentApplyForbiddenException>(() => sut.RejectAsync(other, proposal.Id, null));

        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Pending);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Unprivileged_caller_cannot_submit()
    {
        await Should.ThrowAsync<ConsentApplyForbiddenException>(() => Sut().SubmitAsync(Caller("S-1-5-21-USER", "Consumer"), Manifest(Policy())));

        _proposals.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(DgSid)]
    [InlineData("s-1-5-21-dg-admin")]
    public async Task Submitter_cannot_confirm_own_proposal(string confirmerSid)
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(Dg(), Manifest(Policy()));

        await Should.ThrowAsync<ManifestProposalSeparationOfDutiesException>(() => sut.ConfirmAsync(Caller(confirmerSid, "GovernanceAdmin"), proposal.Id));

        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Pending);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Submitter_holding_both_roles_cannot_confirm()
    {
        var sut = Sut();
        var both = Caller(ItSid, "TenantAdmin", new Claim(ClaimTypes.Role, "GovernanceAdmin"));
        var proposal = await sut.SubmitAsync(both, Manifest(Policy()));

        await Should.ThrowAsync<ManifestProposalSeparationOfDutiesException>(() => sut.ConfirmAsync(both, proposal.Id));

        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Submitter_may_be_a_beneficiary()
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy("User", ItSid)));

        var result = await sut.ConfirmAsync(Dg(), proposal.Id);

        result.Applied.ShouldBe(1);
        _consents.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem().GranteeSid.ShouldBe(new Sid(ItSid));
    }

    [Theory]
    [InlineData("User", DgSid)]
    [InlineData("Group", DgGroup)]
    public async Task Beneficiary_cannot_confirm(string type, string granteeRef)
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy(type, granteeRef)));

        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            sut.ConfirmAsync(Dg(IdentityClaimIssuers.CreateGroupSidClaim(DgGroup)), proposal.Id));

        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Pending);
        _consents.ReplaceCalls.ShouldBe(0);
        _consents.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task Confirm_refused_when_group_resolution_degraded()
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy("User", "S-1-5-21-ALICE")));

        await Should.ThrowAsync<ManifestProposalSeparationOfDutiesException>(() =>
            sut.ConfirmAsync(Dg(GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Degraded)), proposal.Id));

        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Pending);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Stale_state_returns_409_and_recomputed_diff()
    {
        var planner = Planner();
        var sut = Sut(planner);
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy()));
        var submittedHash = _proposals.Single(proposal.Id).StateHash;

        // The grant set of the grantee changes after submit: the stored diff no longer describes what confirming would do.
        await planner.ApplyManifestAsync(Caller("S-1-5-21-DG-OTHER", "GovernanceAdmin"), new DeclarativeConsentManifest("2", [Policy()]));
        _consents.ReplaceCalls.ShouldBe(1);

        var stale = await Should.ThrowAsync<ManifestProposalStaleException>(() => sut.ConfirmAsync(Dg(), proposal.Id));

        stale.RecomputedDiff.ShouldHaveSingleItem().Action.ShouldBe(DeclarativeDiffAction.Unchanged);
        var stored = _proposals.Single(proposal.Id);
        stored.Status.ShouldBe(ManifestProposalStatus.Pending);
        stored.StateHash.ShouldNotBe(submittedHash);
        stored.DiffJson.ShouldContain("Unchanged");
        _consents.ReplaceCalls.ShouldBe(1);
        AuditTypes().ShouldContain(AuditEventTypes.ManifestProposalStale);

        // The recomputed diff is confirmed again.
        var result = await sut.ConfirmAsync(Dg(), proposal.Id);
        result.Unchanged.ShouldBe(1);
        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Confirmed);
    }

    [Fact]
    public async Task First_decision_wins()
    {
        var sut = Sut();
        var confirmed = await sut.SubmitAsync(It(), Manifest(Policy()) with { Name = "first" });
        var rejected = await sut.SubmitAsync(It(), Manifest(Policy(granteeRef: "S-1-5-21-GROUP-B")) with { Name = "second" });

        await sut.ConfirmAsync(Dg(), confirmed.Id);
        (await Should.ThrowAsync<ManifestProposalClosedException>(() => sut.RejectAsync(Caller("S-1-5-21-DG-2", "GovernanceAdmin"), confirmed.Id, "late")))
            .Status.ShouldBe(ManifestProposalStatus.Confirmed);
        (await Should.ThrowAsync<ManifestProposalClosedException>(() => sut.ConfirmAsync(Caller("S-1-5-21-DG-2", "GovernanceAdmin"), confirmed.Id)))
            .Status.ShouldBe(ManifestProposalStatus.Confirmed);

        var view = await sut.RejectAsync(Dg(), rejected.Id, "not needed");
        view.Status.ShouldBe(nameof(ManifestProposalStatus.Rejected));
        view.RejectReason.ShouldBe("not needed");
        await Should.ThrowAsync<ManifestProposalClosedException>(() => sut.ConfirmAsync(Caller("S-1-5-21-DG-2", "GovernanceAdmin"), rejected.Id));

        _consents.ReplaceCalls.ShouldBe(1);
        AuditTypes().Count(t => t == AuditEventTypes.ManifestProposalConfirmed).ShouldBe(1);
        AuditTypes().Count(t => t == AuditEventTypes.ManifestProposalRejected).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_decision_after_validation_loses()
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy()));

        // Another Data Governance member rejects between the confirmer's read and its conditional status update.
        _proposals.BeforeTransition = () => _proposals.Force(proposal.Id, ManifestProposalStatus.Rejected);

        await Should.ThrowAsync<ManifestProposalClosedException>(() => sut.ConfirmAsync(Dg(), proposal.Id));
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Expired_proposal_cannot_be_confirmed()
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy()));

        _time.Advance(TimeSpan.FromDays(31));

        (await Should.ThrowAsync<ManifestProposalClosedException>(() => sut.ConfirmAsync(Dg(), proposal.Id)))
            .Status.ShouldBe(ManifestProposalStatus.Expired);
        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Expired);
        _consents.ReplaceCalls.ShouldBe(0);
        AuditTypes().ShouldContain(AuditEventTypes.ManifestProposalExpired);
    }

    [Fact]
    public async Task Expiry_sweep_closes_due_proposals()
    {
        var sut = Sut();
        var due = await sut.SubmitAsync(It(), Manifest(Policy()) with { Name = "due" });
        _time.Advance(TimeSpan.FromDays(20));
        var open = await sut.SubmitAsync(It(), Manifest(Policy()) with { Name = "open" });
        _time.Advance(TimeSpan.FromDays(11));

        (await sut.ExpireDueAsync()).ShouldBe(1);

        _proposals.Single(due.Id).Status.ShouldBe(ManifestProposalStatus.Expired);
        _proposals.Single(open.Id).Status.ShouldBe(ManifestProposalStatus.Pending);
    }

    [Fact]
    public async Task Newer_proposal_supersedes_pending_one_of_same_scope()
    {
        var sut = Sut();
        var older = await sut.SubmitAsync(It(), Manifest(Policy()));
        var newer = await sut.SubmitAsync(It(), Manifest(Policy(granteeRef: "S-1-5-21-GROUP-B")));

        _proposals.Single(older.Id).Status.ShouldBe(ManifestProposalStatus.Superseded);
        await Should.ThrowAsync<ManifestProposalClosedException>(() => sut.ConfirmAsync(Dg(), older.Id));
        (await sut.ConfirmAsync(Dg(), newer.Id)).Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Secret_object_matched_by_pattern_refused_at_submit()
    {
        _sensitive = true;

        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() => Sut().SubmitAsync(It(), Manifest(Policy(table: "*"))));

        ex.Violation.ShouldBe(DeclarativeManifestViolation.Pattern);
        _proposals.Count.ShouldBe(0);
        _audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("too short")]
    public async Task Missing_justification_refused(string? description)
    {
        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() => Sut().SubmitAsync(It(), Manifest(Policy(description: description))));

        ex.Violation.ShouldBe(DeclarativeManifestViolation.Description);
        _proposals.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Missing_valid_until_refused()
    {
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => Sut().SubmitAsync(It(), Manifest(Policy(noEndDate: true))));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.RequiresExpiry);
        _proposals.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Class_rules_apply_at_submit()
    {
        _sensitive = true;

        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut().SubmitAsync(It(), Manifest(Policy("Group", "S-1-5-21-GROUP-A") with { ValidUntil = DateTimeOffset.UtcNow.AddYears(50) })));

        _proposals.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Prune_threshold_applies_on_confirm()
    {
        const string grantee = "S-1-5-21-GROUP-P";
        var planner = Planner();
        var sut = Sut(planner);
        await planner.ApplyManifestAsync(
            Caller("S-1-5-21-DG-SEED", "GovernanceAdmin"),
            new DeclarativeConsentManifest("seed", new[] { "a", "b", "c", "d", "e" }.Select(t => Policy(granteeRef: grantee, table: t)).ToList()));

        // Pruning d and e of five grants is within the threshold at submit.
        var proposal = await sut.SubmitAsync(
            It(),
            Manifest(Policy(granteeRef: grantee, table: "a"), Policy(granteeRef: grantee, table: "b"), Policy(granteeRef: grantee, table: "c")),
            pruneMode: DeclarativePruneMode.True);
        proposal.Diff.Count(d => d.Action == DeclarativeDiffAction.Prune).ShouldBe(2);

        // b and c are revoked out of band: confirming would now prune two of three active grants (> 50 %), which needs prune=force.
        var outOfBand = _consents.Active(DateTimeOffset.UtcNow).Where(c => c.TableIdentifier.TableName is "b" or "c").Select(c => c.Id).ToList();
        await _consents.ReplaceConsentsAsync(outOfBand, [], new Sid("S-1-5-21-DG-SEED"), "manual", Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(Dg()));
        var replaceCalls = _consents.ReplaceCalls;

        await Should.ThrowAsync<DeclarativePruneThresholdExceededException>(() => sut.ConfirmAsync(Dg(), proposal.Id));

        _consents.ReplaceCalls.ShouldBe(replaceCalls);
        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Pending);
        _consents.Active(DateTimeOffset.UtcNow).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Unaudited_submit_is_closed()
    {
        var sut = new ManifestProposalService(Planner(), _proposals, new InMemoryAuditWriter(new InvalidOperationException("audit down")), timeProvider: _time);

        await Should.ThrowAsync<InvalidOperationException>(() => sut.SubmitAsync(It(), Manifest(Policy())));

        _proposals.All.ShouldHaveSingleItem().Status.ShouldBe(ManifestProposalStatus.Expired);
    }

    [Fact]
    public async Task Foreign_tenant_cannot_read_or_decide()
    {
        var sut = Sut();
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy()));
        var foreign = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "other-tenant"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-DG-FOREIGN"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "test"));

        await Should.ThrowAsync<ManifestProposalNotFoundException>(() => sut.GetAsync(foreign, proposal.Id));
        await Should.ThrowAsync<ManifestProposalNotFoundException>(() => sut.ConfirmAsync(foreign, proposal.Id));
        _consents.ReplaceCalls.ShouldBe(0);
    }
}

/// <summary>In-memory <see cref="IManifestProposalRepository"/> with the conditional transition semantics of the SQL store.</summary>
internal sealed class InMemoryManifestProposalRepository : IManifestProposalRepository
{
    private readonly Dictionary<Guid, ManifestProposal> _rows = [];

    public Action? BeforeTransition { get; set; }

    public int Count => _rows.Count;

    public IReadOnlyList<ManifestProposal> All => _rows.Values.ToList();

    public ManifestProposal Single(Guid id) => _rows[id];

    public void Force(Guid id, ManifestProposalStatus status) => _rows[id].Status = status;

    public Task<IReadOnlyList<Guid>> CreateAsync(ManifestProposal proposal, CancellationToken ct = default)
    {
        var superseded = _rows.Values
            .Where(p => p.TenantId == proposal.TenantId && p.ScopeName == proposal.ScopeName && p.Status == ManifestProposalStatus.Pending)
            .ToList();
        foreach (var p in superseded)
        {
            p.Status = ManifestProposalStatus.Superseded;
            p.DecidedAt = proposal.SubmittedAt;
        }

        _rows[proposal.Id] = Copy(proposal);
        return Task.FromResult<IReadOnlyList<Guid>>(superseded.Select(p => p.Id).ToList());
    }

    public Task<ManifestProposal?> GetAsync(TenantId tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue(id, out var p) && p.TenantId == tenantId ? Copy(p) : null);

    public Task<bool> TryTransitionAsync(
        TenantId tenantId, Guid id, ManifestProposalStatus from, ManifestProposalStatus to, Sid? decidedBy, DateTimeOffset? decidedAt, string? rejectReason, CancellationToken ct = default)
    {
        var hook = BeforeTransition;
        BeforeTransition = null;
        hook?.Invoke();
        if (!_rows.TryGetValue(id, out var p) || p.TenantId != tenantId || p.Status != from)
        {
            return Task.FromResult(false);
        }

        p.Status = to;
        p.DecidedBy = decidedBy;
        p.DecidedAt = decidedAt;
        p.RejectReason = rejectReason;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateDiffAsync(TenantId tenantId, Guid id, string diffJson, string stateHash, CancellationToken ct = default)
    {
        if (!_rows.TryGetValue(id, out var p) || p.TenantId != tenantId || p.Status != ManifestProposalStatus.Pending)
        {
            return Task.FromResult(false);
        }

        p.DiffJson = diffJson;
        p.StateHash = stateHash;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ManifestProposal>> ListExpiredPendingAsync(DateTimeOffset now, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ManifestProposal>>(_rows.Values
            .Where(p => p.Status == ManifestProposalStatus.Pending && p.ExpiresAt <= now)
            .OrderBy(p => p.ExpiresAt)
            .Take(max)
            .Select(Copy)
            .ToList());

    private static ManifestProposal Copy(ManifestProposal p) => new()
    {
        Id = p.Id,
        TenantId = p.TenantId,
        ScopeName = p.ScopeName,
        SubmittedBy = p.SubmittedBy,
        SubmittedAt = p.SubmittedAt,
        ManifestJson = p.ManifestJson,
        DiffJson = p.DiffJson,
        StateHash = p.StateHash,
        Status = p.Status,
        DecidedBy = p.DecidedBy,
        DecidedAt = p.DecidedAt,
        RejectReason = p.RejectReason,
        ExpiresAt = p.ExpiresAt
    };
}
