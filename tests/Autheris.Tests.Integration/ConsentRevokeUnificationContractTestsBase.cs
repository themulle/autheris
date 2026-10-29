namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// DEF-02/03/04 on a real engine (PostgreSQL, SQL Server; the SQLite variants run in the Unit project): one fail-closed revoke behaviour for
/// <c>RevokeConsentAsync</c>, <c>ReplaceConsentsAsync</c> and <c>RevokeSystemConsentAsync</c>, one decision literal, approver check under the row lock.
/// </summary>
public abstract class ConsentRevokeUnificationContractTestsBase : IAsyncLifetime
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid Admin = new("S-1-5-21-DEF-ADMIN");
    private static readonly Guid SyncMarker = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private TableIdentifier _table = new("Sales", "Crm", "Orders");
    private Guid _tableId;

    protected IGovernanceRepository Repo { get; private set; } = null!;

    protected bool Available { get; private set; }

    protected abstract Task<IGovernanceRepository?> CreateRepositoryAsync();

    protected abstract Task DisposeRepositoryAsync();

    /// <summary>Runs one raw statement with named parameters and returns the first column of the first row (or null).</summary>
    protected abstract Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters);

    /// <summary>Makes <paramref name="sid"/> a member of the GovernanceAdmin role.</summary>
    protected abstract Task GrantGovernanceAdminAsync(string sid);

    /// <summary>Takes the row lock of one consent in an open transaction; the holder runs one statement and commits (or rolls back on dispose).</summary>
    protected abstract Task<ILockHolder> HoldConsentRowLockAsync(Guid consentId);

    /// <summary>Counts sessions currently blocked on a lock; the interleaving test waits for it to become positive.</summary>
    protected abstract Task<long> BlockedSessionCountAsync();

    protected interface ILockHolder : IAsyncDisposable
    {
        /// <summary>Executes <paramref name="sql"/> inside the lock transaction, then commits (releasing the lock).</summary>
        Task ExecuteAndCommitAsync(string sql, params (string Name, object Value)[] parameters);
    }

    public static TheoryData<string, string> EntryPointsAndInvalidTenants()
    {
        var data = new TheoryData<string, string>();
        foreach (var entry in new[] { "RevokeConsentAsync", "ReplaceConsentsAsync", "RevokeSystemConsentAsync" })
        {
            foreach (var tenant in new[] { "", "   ", "bad tenant!", "tenant\nwith-newline" })
            {
                data.Add(entry, tenant);
            }
        }

        return data;
    }

    public async Task InitializeAsync()
    {
        var repo = await CreateRepositoryAsync();
        Available = repo != null;
        if (repo == null)
        {
            return;
        }

        Repo = repo;
        _table = new TableIdentifier("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = _table.Domain, SchemaName = _table.Schema, TableName = _table.TableName,
            Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        _tableId = (await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = _table, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        })).Table.Id;
        await GrantGovernanceAdminAsync(Admin.Value);
    }

    public Task DisposeAsync() => DisposeRepositoryAsync();

    private Task<Consent> CreateConsentAsync() =>
        Repo.CreateConsentAsync(new Consent
        {
            TableId = _tableId, TableIdentifier = _table, TenantId = TenantA, GranteeType = GranteeType.User, GranteeSid = new Sid("S-1-5-21-Alice"), Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30), CreatedBySid = Admin
        });

    private async Task<bool> IsRevokedAsync(Guid id) => Convert.ToInt32(await ScalarAsync("SELECT is_revoked FROM CONSENTS WHERE id = @id", ("@id", id.ToString()))) == 1;

    private async Task<List<AuditLogEntry>> AuditAsync(string eventType) =>
        (await Repo.GetAuditLogEntriesAsync(2000)).Where(e => e.EventType == eventType && e.TargetTable == _table.ToString()).ToList();

    private Task InvokeAsync(string entry, Guid id, Sid actor) => entry switch
    {
        "RevokeConsentAsync" => Repo.RevokeConsentAsync(id, actor, "r"),
        "ReplaceConsentsAsync" => Repo.ReplaceConsentsAsync([id], [], actor, "r"),
        _ => Repo.RevokeSystemConsentAsync(id, SyncMarker, actor, "r")
    };

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(EntryPointsAndInvalidTenants))]
    public async Task Revoke_Invalid_Tenant_Value_Fails_Closed_All_Dialects(string entry, string rawTenant)
    {
        // DEF-T-01 / DEF-SC-01..03: no silent default, no raw value echo, nothing revoked, one refusal audit row. The value is planted with raw SQL.
        if (!Available)
        {
            return;
        }

        var consent = await CreateConsentAsync();
        await ScalarAsync(
            "UPDATE CONSENTS SET tenant_id = @t, consent_request_id = @r WHERE id = @id",
            ("@t", rawTenant), ("@r", entry == "RevokeSystemConsentAsync" ? SyncMarker.ToString() : (object)DBNull.Value), ("@id", consent.Id.ToString()));
        var epochBefore = await Repo.GetTableEpochAsync(_table);

        var ex = await Should.ThrowAsync<ConsentTenantIntegrityException>(() => InvokeAsync(entry, consent.Id, Admin));

        ex.ErrorCode.ShouldBe("CONSENT_TENANT_INVALID");
        ex.InnerException.ShouldBeNull();
        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
        (await Repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await AuditAsync("CONSENT_REVOKED")).ShouldBeEmpty();
        var refusal = (await AuditAsync(AuditEventTypes.ConsentTenantIntegrityRefused)).ShouldHaveSingleItem();
        refusal.Decision.ShouldBe("DENY");
        refusal.ActorSid.ShouldBe(Admin);
        refusal.DetailsJson!.ShouldContain(consent.Id.ToString());
        refusal.DetailsJson.ShouldContain("CONSENT_TENANT_INVALID");
        if (rawTenant.Trim().Length > 0)
        {
            ex.ToString().ShouldNotContain(rawTenant);
            refusal.DetailsJson.ShouldNotContain(rawTenant);
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_Refusal_Audit_Not_Written_For_Unknown_Consent()
    {
        // DEF-T-07 / DEF-SC-02.
        if (!Available)
        {
            return;
        }

        var before = (await Repo.GetAuditLogEntriesAsync(2000)).Count(e => e.EventType == AuditEventTypes.ConsentTenantIntegrityRefused);

        await Should.ThrowAsync<KeyNotFoundException>(() => Repo.RevokeConsentAsync(Guid.NewGuid(), Admin, "r"));
        await Should.ThrowAsync<KeyNotFoundException>(() => Repo.ReplaceConsentsAsync([Guid.NewGuid()], [], Admin, "r"));

        (await Repo.GetAuditLogEntriesAsync(2000)).Count(e => e.EventType == AuditEventTypes.ConsentTenantIntegrityRefused).ShouldBe(before);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    [InlineData("RevokeSystemConsentAsync")]
    public async Task Revoke_Audit_Decision_Literal_Is_Identical_Across_Dialects(string entry)
    {
        // DEF-T-02 / DEF-SC-07: every entry point writes REVOKED.
        if (!Available)
        {
            return;
        }

        var consent = await CreateConsentAsync();
        if (entry == "RevokeSystemConsentAsync")
        {
            await ScalarAsync("UPDATE CONSENTS SET consent_request_id = @r WHERE id = @id", ("@r", SyncMarker.ToString()), ("@id", consent.Id.ToString()));
        }

        await InvokeAsync(entry, consent.Id, Admin);

        (await AuditAsync("CONSENT_REVOKED")).ShouldHaveSingleItem().Decision.ShouldBe("REVOKED");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    public async Task Revoke_Approver_Demoted_Before_Lock_Is_Rejected(string entry)
    {
        // DEF-T-03 / DEF-SC-06: a demotion committed before the revoke runs is seen by the approver check; nothing changes.
        if (!Available)
        {
            return;
        }

        var consent = await CreateConsentAsync();
        var epochBefore = await Repo.GetTableEpochAsync(_table);
        await ScalarAsync("DELETE FROM ROLE_MEMBERS WHERE member_sid = @s", ("@s", Admin.Value));

        await Should.ThrowAsync<UnauthorizedAccessException>(() => InvokeAsync(entry, consent.Id, Admin));

        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
        (await Repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await AuditAsync("CONSENT_REVOKED")).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    public async Task Revoke_Approver_Demoted_While_Waiting_For_The_Row_Lock_Is_Rejected(string entry)
    {
        // DEF-T-03 interleaving: session A holds the consent row lock, B (the revoke) waits for it, A commits the demotion, B resumes and is rejected.
        if (!Available)
        {
            return;
        }

        var consent = await CreateConsentAsync();
        var epochBefore = await Repo.GetTableEpochAsync(_table);
        await using var holder = await HoldConsentRowLockAsync(consent.Id);

        var revoke = InvokeAsync(entry, consent.Id, Admin);
        var watch = Stopwatch.StartNew();
        while (await BlockedSessionCountAsync() == 0)
        {
            revoke.IsCompleted.ShouldBeFalse("the revoke must wait for the consent row lock");
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the revoke never blocked on the row lock");
            await Task.Yield();
        }

        await holder.ExecuteAndCommitAsync("DELETE FROM ROLE_MEMBERS WHERE member_sid = @s", ("@s", Admin.Value));

        await Should.ThrowAsync<UnauthorizedAccessException>(() => revoke);
        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
        (await Repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await AuditAsync("CONSENT_REVOKED")).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    public async Task Revoke_Already_Revoked_Is_NoOp_Without_Audit_Epoch_Or_Eviction(string entry)
    {
        // DEF-T-05 / DEF-SC-05 (PERS-05).
        if (!Available)
        {
            return;
        }

        var consent = await CreateConsentAsync();
        await InvokeAsync(entry, consent.Id, Admin);
        var epoch = await Repo.GetTableEpochAsync(_table);
        var audits = (await AuditAsync("CONSENT_REVOKED")).Count;

        await InvokeAsync(entry, consent.Id, Admin);

        (await Repo.GetTableEpochAsync(_table)).ShouldBe(epoch);
        (await AuditAsync("CONSENT_REVOKED")).Count.ShouldBe(audits);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("RevokeConsentAsync")]
    [InlineData("ReplaceConsentsAsync")]
    public async Task Revoke_Unauthorized_Approver_Leaves_No_State_Change_All_Dialects(string entry)
    {
        // DEF-T-08.
        if (!Available)
        {
            return;
        }

        var consent = await CreateConsentAsync();
        var epochBefore = await Repo.GetTableEpochAsync(_table);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => InvokeAsync(entry, consent.Id, new Sid("S-1-5-21-MALLORY")));

        (await IsRevokedAsync(consent.Id)).ShouldBeFalse();
        (await Repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await AuditAsync("CONSENT_REVOKED")).ShouldBeEmpty();
    }
}
