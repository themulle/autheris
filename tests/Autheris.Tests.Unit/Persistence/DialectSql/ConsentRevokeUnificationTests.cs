using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>
/// DEF-02/03/04 (SQLite runtime; the PostgreSQL and SQL Server variants are in the Integration project): one revoke behaviour for all entry
/// points: fail-closed tenant resolution with a refusal audit, one decision literal, approver check inside the transaction.
/// </summary>
public sealed class ConsentRevokeUnificationTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");
    private static readonly Guid SyncMarker = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private Guid _tableId;

    public void Dispose() => _repo.Dispose();

    public static TheoryData<string, string> EntryPointsAndInvalidTenants()
    {
        var data = new TheoryData<string, string>();
        foreach (var entry in new[] { "RevokeConsentAsync", "ReplaceConsentsAsync", "RevokeSystemConsentAsync" })
        {
            foreach (var tenant in new[] { "", "   ", "bad tenant!", new string('x', 65), "tenant\nwith-newline" })
            {
                data.Add(entry, tenant);
            }
        }

        return data;
    }

    private async Task EnsureTableAsync()
    {
        await ExecAsync("INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES ('role-admin', 'GovernanceAdmin', 'test');" +
                        "INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid) VALUES ('" + Guid.NewGuid() + "', 'role-admin', 'User', '" + Admin.Value + "');");
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

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await _repo.Connections.OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Consent> CreateConsentAsync(string grantee = "S-1-5-21-Alice") =>
        await _repo.CreateConsentAsync(new Consent
        {
            TableId = _tableId, TableIdentifier = _table, TenantId = TenantA, GranteeType = GranteeType.User, GranteeSid = new Sid(grantee), Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30), CreatedBySid = Admin
        });

    private Task PlantTenantAsync(Guid consentId, string raw) =>
        ExecAsync("UPDATE CONSENTS SET tenant_id = @t, consent_request_id = NULL WHERE id = @id", ("@t", raw), ("@id", consentId.ToString()));

    private async Task MarkSystemConsentAsync(Guid consentId)
    {
        await ExecAsync("PRAGMA foreign_keys = OFF;");
        await ExecAsync("UPDATE CONSENTS SET consent_request_id = @r WHERE id = @id", ("@r", SyncMarker.ToString()), ("@id", consentId.ToString()));
    }

    // GetConsentByIdAsync maps the tenant and would itself throw on a planted invalid value, so the flag is read raw.
    private async Task<bool> IsRevokedAsync(Guid consentId)
    {
        await using var conn = await _repo.Connections.OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_revoked FROM CONSENTS WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", consentId.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private async Task<List<AuditLogEntry>> AuditAsync(string eventType) =>
        (await _repo.GetAuditLogEntriesAsync(500)).Where(e => e.EventType == eventType).ToList();

    private Task InvokeAsync(string entry, Guid consentId, Sid actor) => entry switch
    {
        "RevokeConsentAsync" => _repo.RevokeConsentAsync(consentId, actor, "r"),
        "ReplaceConsentsAsync" => _repo.ReplaceConsentsAsync([consentId], [], actor, "r"),
        _ => _repo.RevokeSystemConsentAsync(consentId, SyncMarker, actor, "r")
    };

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(EntryPointsAndInvalidTenants))]
    public async Task Revoke_Invalid_Tenant_Value_Fails_Closed_All_Dialects(string entry, string rawTenant)
    {
        // DEF-T-01 / DEF-SC-01..03 (SQLite): no silent default, no raw value echo, nothing revoked, one refusal audit row.
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        await PlantTenantAsync(consent.Id, rawTenant);
        if (entry == "RevokeSystemConsentAsync")
        {
            await MarkSystemConsentAsync(consent.Id);
        }

        var epochBefore = await _repo.GetTableEpochAsync(_table);

        var ex = await Should.ThrowAsync<ConsentTenantIntegrityException>(() => InvokeAsync(entry, consent.Id, Admin));

        ex.ErrorCode.ShouldBe("CONSENT_TENANT_INVALID");
        ex.InnerException.ShouldBeNull();
        if (rawTenant.Trim().Length > 0)
        {
            ex.ToString().ShouldNotContain(rawTenant);
        }

        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await AuditAsync("CONSENT_REVOKED")).ShouldBeEmpty();
        var refusals = await AuditAsync(AuditEventTypes.ConsentTenantIntegrityRefused);
        var refusal = refusals.ShouldHaveSingleItem();
        refusal.TenantId.ShouldBe(ConsentTenantRefusalAudit.IntegrityAuditTenant);
        refusal.Decision.ShouldBe("DENY");
        refusal.ActorSid.ShouldBe(Admin);
        refusal.DetailsJson!.ShouldContain(consent.Id.ToString());
        refusal.DetailsJson.ShouldContain("CONSENT_TENANT_INVALID");
        if (rawTenant.Trim().Length > 0)
        {
            refusal.DetailsJson.ShouldNotContain(rawTenant);
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_Refusal_Audit_Not_Written_For_Unknown_Consent()
    {
        // DEF-T-07 / DEF-SC-02: no audit amplification for ids that do not exist.
        await EnsureTableAsync();

        await Should.ThrowAsync<KeyNotFoundException>(() => _repo.RevokeConsentAsync(Guid.NewGuid(), Admin, "r"));
        await Should.ThrowAsync<KeyNotFoundException>(() => _repo.ReplaceConsentsAsync([Guid.NewGuid()], [], Admin, "r"));

        (await AuditAsync(AuditEventTypes.ConsentTenantIntegrityRefused)).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_Refusal_Audit_For_Unauthorized_Actor_Is_Not_Written()
    {
        // DEF-SC-02: an unauthorised actor gets UnauthorizedAccessException first, so it cannot make the platform write refusal rows.
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        await PlantTenantAsync(consent.Id, "bad tenant!");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => _repo.RevokeConsentAsync(consent.Id, new Sid("S-1-5-21-MALLORY"), "r"));

        (await AuditAsync(AuditEventTypes.ConsentTenantIntegrityRefused)).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_Refusal_Audit_Failure_Propagates_And_Revoke_Stays_Refused()
    {
        // DEF-T-06 / DEF-SC-03: a failing refusal audit write is not swallowed and never turns the refusal into success.
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        await PlantTenantAsync(consent.Id, "bad tenant!");
        await ExecAsync(@"CREATE TRIGGER fail_refusal_audit BEFORE INSERT ON AUDIT_LOG_ENTRIES
                          WHEN NEW.event_type = 'CONSENT_TENANT_INTEGRITY_REFUSED'
                          BEGIN SELECT RAISE(ABORT, 'audit down'); END;");

        var ex = await Should.ThrowAsync<Exception>(() => _repo.RevokeConsentAsync(consent.Id, Admin, "r"));

        ex.ShouldNotBeOfType<ConsentTenantIntegrityException>();
        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    [InlineData("RevokeSystemConsentAsync")]
    public async Task Revoke_Audit_Decision_Literal_Is_Identical_Across_Dialects(string entry)
    {
        // DEF-T-02 / DEF-SC-07 (SQLite runtime; the IL check over all dialects is in the Architecture project).
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        if (entry == "RevokeSystemConsentAsync")
        {
            await MarkSystemConsentAsync(consent.Id);
        }

        await InvokeAsync(entry, consent.Id, Admin);

        var audit = (await AuditAsync("CONSENT_REVOKED")).ShouldHaveSingleItem();
        audit.Decision.ShouldBe("REVOKED");
        audit.TenantId.ShouldBe(TenantA);
        ConsentGrantSqlCore.RevokedDecision.ShouldBe("REVOKED");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    public async Task Revoke_Approver_Demoted_Before_Lock_Is_Rejected(string entry)
    {
        // DEF-T-03 / DEF-SC-06: an approver demoted (committed) before the revoke takes the lock is rejected, nothing changes, no audit row.
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        var epochBefore = await _repo.GetTableEpochAsync(_table);
        await ExecAsync("DELETE FROM ROLE_MEMBERS WHERE member_sid = @s", ("@s", Admin.Value));

        await Should.ThrowAsync<UnauthorizedAccessException>(() => InvokeAsync(entry, consent.Id, Admin));

        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await AuditAsync("CONSENT_REVOKED")).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    public async Task Revoke_Already_Revoked_Is_NoOp_Without_Audit_Epoch_Or_Eviction(string entry)
    {
        // DEF-T-05 / DEF-SC-05 (PERS-05).
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        await InvokeAsync(entry, consent.Id, Admin);
        var epoch = await _repo.GetTableEpochAsync(_table);
        var audits = (await AuditAsync("CONSENT_REVOKED")).Count;

        await InvokeAsync(entry, consent.Id, Admin);

        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epoch);
        (await AuditAsync("CONSENT_REVOKED")).Count.ShouldBe(audits);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    public async Task Revoke_Unauthorized_Approver_Leaves_No_State_Change_All_Dialects(string entry)
    {
        // DEF-T-08 (SQLite).
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        var epochBefore = await _repo.GetTableEpochAsync(_table);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => InvokeAsync(entry, consent.Id, new Sid("S-1-5-21-MALLORY")));

        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await AuditAsync("CONSENT_REVOKED")).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ReplaceConsents_Sqlite_Evicts_MetadataCache()
    {
        // DEF-01 / DEF-T-09: ReplaceConsentsAsync evicts the cached table metadata like PG/MSSQL (applied on commit).
        await EnsureTableAsync();
        var consent = await CreateConsentAsync();
        (await _repo.GetTableMetadataAsync(_table))!.Table.Sensitivity.ShouldBe("LOW");
        await ExecAsync("UPDATE TABLES SET sensitivity = 'HIGH' WHERE id = @id", ("@id", _tableId.ToString()));

        await _repo.ReplaceConsentsAsync([consent.Id], [], Admin, "evict");

        (await _repo.GetTableMetadataAsync(_table))!.Table.Sensitivity.ShouldBe("HIGH");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad tenant!")]
    public void ResolveConsentTenant_Throws_Governed_Exception_Without_Value(string? raw)
    {
        var id = Guid.NewGuid();

        var ex = Should.Throw<ConsentTenantIntegrityException>(() => ConsentGrantSqlCore.ResolveConsentTenant(raw, id, "a.b.c"));

        ex.ConsentId.ShouldBe(id);
        ex.Message.ShouldBe("The consent tenant value is invalid; the operation was refused.");
        ConsentGrantSqlCore.ResolveConsentTenant("tenant-a", id, null).ShouldBe(TenantA);
    }
}
