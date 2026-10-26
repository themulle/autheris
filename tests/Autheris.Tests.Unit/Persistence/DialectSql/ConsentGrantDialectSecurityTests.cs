using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>
/// ARCH-MNT-22 runtime security pins of the revoke path on SQLite (the PostgreSQL and SQL Server variants run in the Integration contract
/// tests of CI, which need containers): the refactored statement-level core must keep authorisation, atomicity and the row-locked revoke by id.
/// </summary>
public sealed class ConsentGrantDialectSecurityTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");

    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private Guid _tableId;

    public void Dispose() => _repo.Dispose();

    private async Task EnsureTableAsync()
    {
        await using (var conn = await _repo.Connections.OpenAsync(CancellationToken.None))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES ('role-admin', 'GovernanceAdmin', 'test');
                                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid) VALUES (@id, 'role-admin', 'User', @sid);";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@sid", Admin.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = _table.Domain, SchemaName = _table.Schema, TableName = _table.TableName,
            Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        _tableId = (await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = _table, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        })).Table.Id;
    }

    private Consent UserConsent(string sid, TenantId tenant) => new()
    {
        TableId = _tableId, TableIdentifier = _table, TenantId = tenant, GranteeType = GranteeType.User, GranteeSid = new Sid(sid), Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30), CreatedBySid = Admin
    };

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_Unauthorized_NonGrantee_Throws_And_Writes_No_Change()
    {
        // ARCH-MNT-22-T-04 (SQLite).
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice", TenantA));
        var epochBefore = await _repo.GetTableEpochAsync(_table);
        var auditBefore = (await _repo.GetAuditLogEntriesAsync(100)).Count(e => e.EventType == "CONSENT_REVOKED");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => _repo.RevokeConsentAsync(consent.Id, new Sid("S-1-5-21-MALLORY"), "nope"));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _repo.ReplaceConsentsAsync([consent.Id], [], new Sid("S-1-5-21-MALLORY"), "nope"));

        (await _repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await _repo.GetAuditLogEntriesAsync(100)).Count(e => e.EventType == "CONSENT_REVOKED").ShouldBe(auditBefore);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_ById_Cannot_Revoke_Other_Tenant_Consent()
    {
        // ARCH-MNT-22-T-05 (SQLite): the revoke-by-id exemption (SC-04 a) touches exactly the addressed row; the other tenant's consent stays active,
        // and the audit entry carries the tenant read from the addressed (locked) row.
        await EnsureTableAsync();
        var mine = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice", TenantA));
        var foreign = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice", TenantB));

        await _repo.ReplaceConsentsAsync([mine.Id], [], Admin, "revoke tenant-a only");

        (await _repo.GetConsentByIdAsync(mine.Id))!.IsRevoked.ShouldBeTrue();
        (await _repo.GetConsentByIdAsync(foreign.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice")], _table, DateTimeOffset.UtcNow, TenantB)).Select(c => c.Id).ShouldBe([foreign.Id]);
        (await _repo.GetAuditLogEntriesAsync(100)).ShouldContain(e => e.EventType == "CONSENT_REVOKED" && e.TenantId == TenantA);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_GranteeSelf_IsAllowedWithoutApproverRights_AndAuditsTheDecisionLiteral_D1()
    {
        // Pins D1 at runtime for SQLite: the audit decision literal is "REVOKED" (PostgreSQL/SQL Server write "REVOKE"; follow-up DEF-03).
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice", TenantA));

        await _repo.RevokeConsentAsync(consent.Id, new Sid("S-1-5-21-Alice"), "self");

        (await _repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeTrue();
        (await _repo.GetAuditLogEntriesAsync(100)).ShouldContain(e => e.EventType == "CONSENT_REVOKED" && e.Decision == "REVOKED");
    }
}
