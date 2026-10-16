using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// SEC-SC-08 (M1/M2/L3): the in-transaction audit path of Tier-A consent changes is tied to the anchor like the batch path, the manifest APPLIED audit is part of
/// the replace transaction, and a failed replace restores the in-memory audit chain state (SQLite).
/// </summary>
[Trait("Category", "Security")]
public sealed class ConsentAuditAnchorAckTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");

    private readonly SwitchableAnchorStore _anchors = new();
    private readonly SqliteGovernanceRepository _repo;
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private Guid _tableId;

    public ConsentAuditAnchorAckTests() => _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), auditAnchorStore: _anchors);

    public void Dispose() => _repo.Dispose();

    private sealed class SwitchableAnchorStore : IAuditChainAnchorStore
    {
        private AuditChainAnchor? _last;
        public bool FailSave { get; set; }
        public AuditChainAnchor? Load() => _last;

        public void Save(AuditChainAnchor anchor)
        {
            if (FailSave) throw new IOException("anchor store down");
            _last = anchor;
        }
    }

    private async Task EnsureTableAsync()
    {
        await using (var conn = await _repo.Connections.OpenAsync(CancellationToken.None))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES ('role-admin', 'GovernanceAdmin', 'test');
                                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid) VALUES (@id, 'role-admin', 'User', @sid);";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@sid", Admin.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        var table = new Table { Id = Guid.NewGuid(), SourceName = _table.Domain, SchemaName = _table.Schema, TableName = _table.TableName, Sensitivity = "LOW", IsActive = true };
        _tableId = (await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = _table, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        })).Table.Id;
    }

    private Consent UserConsent(string sid) => new()
    {
        TableId = _tableId, TableIdentifier = _table, TenantId = Tenant, GranteeType = GranteeType.User, GranteeSid = new Sid(sid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30), CreatedBySid = Admin
    };

    [Fact]
    public async Task SEC_SC_08_ConsentGrantAndRevoke_AreNotAcknowledged_WhenTheAnchorCannotBeWritten()
    {
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice"));

        _anchors.FailSave = true;
        await Should.ThrowAsync<AuditUnavailableException>(() => _repo.CreateConsentAsync(UserConsent("S-1-5-21-Bob")));
        await Should.ThrowAsync<AuditUnavailableException>(() => _repo.RevokeConsentAsync(consent.Id, Admin, "test"));
        await Should.ThrowAsync<AuditUnavailableException>(() => _repo.ReplaceConsentsAsync([], [UserConsent("S-1-5-21-Carl")], Admin, "apply"));
        await Should.ThrowAsync<AuditUnavailableException>(() => _repo.ExtendConsentExpiryAsync(consent.Id, DateTimeOffset.UtcNow.AddDays(60)));

        // the chain stays linear and re-anchors once the store is back
        _anchors.FailSave = false;
        await _repo.RecordAuditEventAsync(new AuditLogEntry { TenantId = Tenant, EventType = AuditEventTypes.AuditConfigChanged, ActorSid = Admin, TargetTable = "x", Decision = "ALLOW", TraceId = "t" });
        (await _repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task SEC_SC_08_Replace_WritesTheCallersAuditInTheSameTransaction_AndRollsItBackOnFault()
    {
        await EnsureTableAsync();
        var old = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice"));
        AuditLogEntry Applied(string reason) => new()
        {
            TenantId = Tenant, EventType = "GITOPS_CONSENT_APPLY", ActorSid = Admin, TargetTable = _table.ToString(), Decision = "APPLIED", TraceId = reason
        };
        var duplicate = UserConsent("S-1-5-21-Bob");
        var clash = new Consent
        {
            Id = old.Id, TableId = _tableId, TableIdentifier = _table, TenantId = Tenant, GranteeType = GranteeType.User, GranteeSid = new Sid("S-1-5-21-Bob"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(5), CreatedBySid = Admin
        };

        await Should.ThrowAsync<Exception>(() => _repo.ReplaceConsentsAsync([old.Id], [clash], Admin, "apply", [Applied("faulted")]));

        (await _repo.GetAuditLogEntriesAsync(200)).ShouldNotContain(e => e.TraceId == "faulted");
        // L3: the rolled-back transaction restored the in-memory chain state, so the chain verifies and the next writes link correctly
        await _repo.ReplaceConsentsAsync([old.Id], [duplicate], Admin, "apply", [Applied("committed")]);
        (await _repo.GetAuditLogEntriesAsync(200)).ShouldContain(e => e.TraceId == "committed" && e.EventType == "GITOPS_CONSENT_APPLY");
        (await _repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }
}
