using System.Security.Claims;
using Autheris.Application.Governance.Declarative;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Common.Fakes;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>
/// Amendment 6 end to end on PostgreSQL: IT submits a manifest proposal, Data Governance is notified, one Data Governance confirmation makes
/// the grant effective, and the audit chain links the proposal events with the resulting grant entries.
/// </summary>
[Trait("Category", "Security")]
public sealed class ManifestProposalFlowTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid ItAdmin = new("S-1-5-21-FLOW-IT");
    private static readonly Sid DgAdmin = new("S-1-5-21-FLOW-DG");
    private static readonly Sid GroupA = new("S-1-5-21-FLOW-GROUP-A");
    private const string Justification = "integration test proposal grant";

    private readonly PostgreSqlGovernanceRepository _repo;
    private readonly string _connectionString;
    private readonly ManifestProposalStore _store;
    private readonly InMemoryAuditWriter _audit = new();
    private readonly RecordingDispatcher _notifications = new();
    private readonly TableIdentifier _table = new("Sales", "Crm", "Proposal_" + Guid.NewGuid().ToString("N")[..6]);

    public ManifestProposalFlowTests()
    {
        _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out _connectionString);
        _store = new ManifestProposalStore(new PostgreSqlGovernanceDialect(_connectionString));
    }

    public void Dispose() => _repo.Dispose();

    private static ClaimsPrincipal Principal(Sid sid, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, sid.Value), new Claim(ClaimTypes.Role, role), new Claim("tenant_id", TenantA.Value)], "Bearer"));

    private ManifestProposalService Sut() =>
        new(new DeclarativeConsentApplyService(_repo, _repo, _repo), _store, _audit, notifications: _notifications);

    private async Task EnsureTableAsync()
    {
        var table = new Table { Id = Guid.NewGuid(), SourceName = _table.Domain, SchemaName = _table.Schema, TableName = _table.TableName, IsActive = true };
        await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = _table,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
    }

    private DeclarativeConsentManifest Manifest() => new(
        "1",
        [new DeclarativeConsentPolicy(_table.Domain, _table.Schema, _table.TableName, "Group", GroupA.Value, "Allow", Description: Justification, ValidUntil: DateTimeOffset.UtcNow.AddDays(30))],
        Name: "flow");

    private async Task<IReadOnlyList<Consent>> ActiveAsync() =>
        (await _repo.GetActiveConsentsForSubjectsAsync([GroupA], _table, DateTimeOffset.UtcNow, TenantA))
            .Where(c => c.TableIdentifier.Equals(_table))
            .ToList();

    [Fact]
    public async Task Submit_notify_confirm_grant_effective_and_audit_chain()
    {
        await EnsureTableAsync();
        var sut = Sut();

        var proposal = await sut.SubmitAsync(Principal(ItAdmin, "TenantAdmin"), Manifest());

        // submit: stored, pending, no effect, Data Governance notified with a link to the diff
        var stored = await _store.GetAsync(TenantA, proposal.Id);
        stored.ShouldNotBeNull().Status.ShouldBe(ManifestProposalStatus.Pending);
        stored.StateHash.Length.ShouldBe(64);
        (await ActiveAsync()).ShouldBeEmpty();
        var message = _notifications.Messages.ShouldHaveSingleItem();
        message.EventType.ShouldBe(AuditEventTypes.ManifestProposalSubmitted);
        message.Summary.ShouldContain(proposal.Id.ToString("D"));

        // confirm: the grant becomes effective with the proposal's justification
        var result = await sut.ConfirmAsync(Principal(DgAdmin, "GovernanceAdmin"), proposal.Id);

        result.Applied.ShouldBe(1);
        var grant = (await ActiveAsync()).ShouldHaveSingleItem();
        grant.GrantSource.ShouldBe(ConsentGrantSource.Declarative);
        (await _store.GetAsync(TenantA, proposal.Id))!.Status.ShouldBe(ManifestProposalStatus.Confirmed);

        // audit chain: proposal events and the resulting grant entry that references the proposal
        _audit.Entries.Select(e => e.EventType).ShouldBe([AuditEventTypes.ManifestProposalSubmitted, AuditEventTypes.ManifestProposalConfirmed]);
        _audit.Entries.ShouldAllBe(e => e.DetailsJson.Contains(proposal.Id.ToString("D")));
        var linked = Convert.ToInt32(GovernanceTestDatabase.ScalarRaw(
            _connectionString,
            "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE event_type = 'GITOPS_CONSENT_APPLY' AND details_json LIKE @p",
            ("p", "%" + proposal.Id.ToString("D") + "%")), System.Globalization.CultureInfo.InvariantCulture);
        linked.ShouldBe(1);
    }

    [Fact]
    public async Task Rejected_proposal_never_grants()
    {
        await EnsureTableAsync();
        var sut = Sut();
        var proposal = await sut.SubmitAsync(Principal(ItAdmin, "TenantAdmin"), Manifest());

        await sut.RejectAsync(Principal(DgAdmin, "GovernanceAdmin"), proposal.Id, "not needed");

        await Should.ThrowAsync<ManifestProposalClosedException>(() => sut.ConfirmAsync(Principal(new Sid("S-1-5-21-FLOW-DG-2"), "GovernanceAdmin"), proposal.Id));
        (await ActiveAsync()).ShouldBeEmpty();
        (await _store.GetAsync(TenantA, proposal.Id))!.RejectReason.ShouldBe("not needed");
    }

    private sealed class RecordingDispatcher : IGovernanceNotificationDispatcher
    {
        public List<GovernanceNotificationMessage> Messages { get; } = [];

        public Task DispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
