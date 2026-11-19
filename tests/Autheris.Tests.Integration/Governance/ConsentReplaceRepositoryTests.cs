using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>WP-SC-02 (repository part, SEC-SC-03): role-grantee lookup and the atomic revoke-and-create with a single epoch bump (PostgreSQL).</summary>
[Trait("Category", "Security")]
public sealed class ConsentReplaceRepositoryTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");

    private readonly PostgreSqlGovernanceRepository _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService());
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private Guid _tableId;

    public void Dispose() => _repo.Dispose();

    private async Task EnsureTableAsync()
    {
        await GrantGovernanceAdminAsync(Admin);
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

    private async Task GrantGovernanceAdminAsync(Sid sid)
    {
        var conn = _repo.RawConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1;";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@sid", sid.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private Consent GroupConsent(string group, TenantId tenant, int fromDays = -1, int toDays = 30) => new()
    {
        TableId = _tableId, TableIdentifier = _table, TenantId = tenant, GranteeType = GranteeType.Group, GranteeSid = new Sid(group), Effect = ConsentEffect.Deny,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(fromDays), ValidTo = DateTimeOffset.UtcNow.AddDays(toDays), CreatedBySid = Admin
    };

    private Consent UserConsent(string sid, ConsentEffect effect = ConsentEffect.Allow, int fromDays = -1, double toDays = 30) => new()
    {
        TableId = _tableId, TableIdentifier = _table, TenantId = TenantA, GranteeType = GranteeType.User, GranteeSid = new Sid(sid), Effect = effect,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(fromDays), ValidTo = DateTimeOffset.UtcNow.AddDays(toDays), CreatedBySid = Admin
    };

    [Fact]
    public async Task Replace_RevokesAndCreatesInOneTransaction_AndBumpsTheEpochOnce()
    {
        await EnsureTableAsync();
        var old = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice"));
        var epochBefore = await _repo.GetTableEpochAsync(_table);
        var replacement = UserConsent("S-1-5-21-Alice");

        await _repo.ReplaceConsentsAsync([old.Id], [replacement, UserConsent("S-1-5-21-Bob")], Admin, "manifest apply", AdminAuth());

        var active = await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice"), new Sid("S-1-5-21-Bob")], _table, DateTimeOffset.UtcNow, TenantA);
        active.Select(c => c.Id).ShouldNotContain(old.Id);
        active.Count.ShouldBe(2);
        (await _repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeTrue();
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore + 1);
    }

    [Fact]
    public async Task ManifestApply_EndToEnd_UsesTheTransactionalReplace_IsIdempotentAndRevokesChangedPolicies()
    {
        await EnsureTableAsync();
        var svc = new Autheris.Application.Governance.Services.DeclarativeConsentApplyService(_repo, _repo, _repo);
        var caller = ManifestAdmin(Admin.Value, TenantA.Value);
        var policy = new DeclarativeConsentPolicy(_table.Domain, _table.Schema, _table.TableName, "Group", "S-1-5-21-MANIFEST-GROUP", "Allow", ValidUntil: DateTimeOffset.UtcNow.AddDays(30));
        var epochBefore = await _repo.GetTableEpochAsync(_table);

        var first = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("1", [policy]));
        var epochAfterFirst = await _repo.GetTableEpochAsync(_table);
        var second = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("1", [policy]));
        var changed = policy with { ColumnRules = [new DeclarativeColumnRule("id", "Mask")] };
        var third = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("2", [changed]));

        first.Applied.ShouldBe(1);
        second.Unchanged.ShouldBe(1);
        second.Applied.ShouldBe(0);
        third.Applied.ShouldBe(1);
        third.Revoked.ShouldBe(1);
        epochAfterFirst.ShouldBe(epochBefore + 1);
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochAfterFirst + 1);
        (await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-MANIFEST-GROUP")], _table, DateTimeOffset.UtcNow, TenantA)).Count(c => c.GranteeType == GranteeType.Group).ShouldBe(1);
    }

    [Fact]
    public async Task ManifestApply_EndToEnd_BaseAndScopeRuleAndTwoRowFiltersOnOneColumn_ReApplyIsUnchangedAndWritesNothing()
    {
        // Issue #806: a base rule plus a scope rule on the same column and two row filters on one column, read back from the database.
        await EnsureTableAsync();
        var svc = new Autheris.Application.Governance.Services.DeclarativeConsentApplyService(_repo, _repo, _repo);
        var caller = ManifestAdmin(Admin.Value, TenantA.Value);
        var grantee = new Sid("S-1-5-21-MANIFEST-GROUP-806");
        var policy = new DeclarativeConsentPolicy(
            _table.Domain, _table.Schema, _table.TableName, "Group", grantee.Value, "Allow",
            ColumnRules: [new DeclarativeColumnRule("id", "Clear"), new DeclarativeColumnRule("id", "Mask", ["Agent.Read"])],
            RowFilters: [new DeclarativeRowFilter("id", "GT", "int", "[0]"), new DeclarativeRowFilter("id", "LT", "int", "[1000]")],
            ValidUntil: DateTimeOffset.UtcNow.AddDays(30));

        var first = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("1", [policy]));
        var stored = (await _repo.GetActiveConsentsForSubjectsAsync([grantee], _table, DateTimeOffset.UtcNow, TenantA)).Single(c => c.GranteeType == GranteeType.Group);
        var epochAfterFirst = await _repo.GetTableEpochAsync(_table);
        var second = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("1", [policy]));

        first.Applied.ShouldBe(1);
        second.Unchanged.ShouldBe(1);
        second.Applied.ShouldBe(0);
        second.Revoked.ShouldBe(0);
        second.Updated.ShouldBe(0);
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochAfterFirst);
        var active = (await _repo.GetActiveConsentsForSubjectsAsync([grantee], _table, DateTimeOffset.UtcNow, TenantA)).Where(c => c.GranteeType == GranteeType.Group).ToList();
        active.Select(c => c.Id).ShouldBe([stored.Id]);
        active[0].ColumnRules.Count.ShouldBe(2);
        active[0].RowFilters.Count.ShouldBe(2);
    }

    private static Autheris.Domain.Security.ConsentRevocationAuthority AdminAuth() =>
        Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(ManifestAdmin(Admin.Value, TenantA.Value));

    private static System.Security.Claims.ClaimsPrincipal ManifestAdmin(string sid, string tenant) => new(new System.Security.Claims.ClaimsIdentity(
    [
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, sid),
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "GovernanceAdmin"),
        new System.Security.Claims.Claim("tenant_id", tenant)
    ], "test"));

    [Fact]
    public async Task Replace_FaultAfterTheRevocation_LeavesNothingChanged()
    {
        await EnsureTableAsync();
        var old = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice"));
        var epochBefore = await _repo.GetTableEpochAsync(_table);
        var duplicateId = new Consent
        {
            Id = old.Id, // primary key violation after the revoke step
            TableId = _tableId, TableIdentifier = _table, TenantId = TenantA, GranteeType = GranteeType.User, GranteeSid = new Sid("S-1-5-21-Bob"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(5), CreatedBySid = Admin
        };

        await Should.ThrowAsync<Exception>(() => _repo.ReplaceConsentsAsync([old.Id], [duplicateId], Admin, "manifest apply", AdminAuth()));

        (await _repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetTableEpochAsync(_table)).ShouldBe(epochBefore);
        (await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Bob")], _table, DateTimeOffset.UtcNow, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Replace_UnauthorizedActor_ChangesNothing()
    {
        await EnsureTableAsync();
        var old = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice"));

        await Should.ThrowAsync<Autheris.Domain.Exceptions.ConsentRevocationForbiddenException>(() =>
            _repo.ReplaceConsentsAsync([old.Id], [UserConsent("S-1-5-21-Bob")], new Sid("S-1-5-21-STRANGER"), "x", Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(null)));

        (await _repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Bob")], _table, DateTimeOffset.UtcNow, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Replace_UnknownConsentId_ThrowsAndChangesNothing()
    {
        await EnsureTableAsync();

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            _repo.ReplaceConsentsAsync([Guid.NewGuid()], [UserConsent("S-1-5-21-Bob")], Admin, "x", AdminAuth()));

        (await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Bob")], _table, DateTimeOffset.UtcNow, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Replace_WritesGrantAndRevokeAuditEntries()
    {
        await EnsureTableAsync();
        var old = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice"));

        await _repo.ReplaceConsentsAsync([old.Id], [UserConsent("S-1-5-21-Bob")], Admin, "manifest apply", AdminAuth());

        var audit = await _repo.GetAuditLogEntriesAsync(50);
        audit.ShouldContain(e => e.EventType == "CONSENT_REVOKED" && e.ActorSid == Admin);
        audit.Count(e => e.EventType == "CONSENT_GRANTED").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task SEC_SC_20_LegacyPermanentDeny_StaysActive_WhileALegacyAllowIsClamped()
    {
        // The read-time clamp exists only for an operator-configured limit (no compiled-in ceiling).
        Autheris.Domain.Model.IndividualGrantLimits.Configure(TimeSpan.FromDays(180), null, null);
        try
        {
            await EnsureTableAsync();
            var now = DateTimeOffset.UtcNow;
            var deny = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice", ConsentEffect.Deny, -400, 365 * 50));
            var allow = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice", ConsentEffect.Allow, -400, 365 * 50));

            var active = await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice")], _table, now, TenantA);

            active.ShouldContain(c => c.Id == deny.Id);
            active.ShouldNotContain(c => c.Id == allow.Id);
        }
        finally
        {
            Autheris.Domain.Model.IndividualGrantLimits.Reset();
        }
    }
    [Fact]
    public async Task SEC_SC_16_ConsentPage_WalksTheTenantOnceByKeyset_AndNeverCrossesTenants()
    {
        await EnsureTableAsync();
        var created = new List<Consent>();
        for (var i = 0; i < 7; i++)
        {
            created.Add(await _repo.CreateConsentAsync(UserConsent("S-1-5-21-U" + i)));
        }

        created.Add(await _repo.CreateConsentAsync(GroupConsent("Analyst", TenantA)));
        await _repo.CreateConsentAsync(GroupConsent("Analyst", TenantB));

        var seen = new List<Guid>();
        Guid? after = null;
        var pages = 0;
        do
        {
            var page = await _repo.GetConsentPageAsync(new Autheris.Application.Governance.Paging.ConsentPageQuery(TenantA, AfterId: after, PageSize: 3));
            page.Items.Count.ShouldBeLessThanOrEqualTo(3);
            page.Items.ShouldAllBe(c => c.TenantId == TenantA);
            seen.AddRange(page.Items.Select(c => c.Id));
            after = page.NextAfterId;
            pages++;
        }
        while (after != null);

        seen.ShouldBe(created.Select(c => c.Id).OrderBy(i => i.ToString(), StringComparer.Ordinal).ToList());
        pages.ShouldBe(3);
    }

    [Fact]
    public async Task SEC_SC_16_ConsentPage_FiltersByTableGranteeTypeAndGrantee_AndHidesRevokedByDefault()
    {
        await EnsureTableAsync();
        var alice = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Alice"));
        await _repo.CreateConsentAsync(UserConsent("S-1-5-21-Bob"));
        var role = await _repo.CreateConsentAsync(GroupConsent("Analyst", TenantA));
        await _repo.RevokeConsentAsync(alice.Id, new Sid("S-1-5-21-Alice"), "self", Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(null));
        var tenant = new Autheris.Application.Governance.Paging.ConsentPageQuery(TenantA, Table: _table);

        (await _repo.GetConsentPageAsync(tenant with { GranteeType = GranteeType.Group })).Items.Select(c => c.Id).ShouldBe([role.Id]);
        (await _repo.GetConsentPageAsync(tenant with { Grantee = "s-1-5-21-alice" })).Items.ShouldBeEmpty();
        (await _repo.GetConsentPageAsync(tenant with { Grantee = "s-1-5-21-alice", IncludeInactive = true })).Items.Single().RenewalCount.ShouldBe(0);
        (await _repo.GetConsentPageAsync(tenant with { Grantee = "ANALYST" })).Items.Single().Id.ShouldBe(role.Id);
        (await _repo.GetConsentPageAsync(tenant with { Table = new TableIdentifier("x", "y", "z") })).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task SEC_SC_16_ConsentPage_WithoutTenant_FailsClosed()
    {
        await EnsureTableAsync();

        await Should.ThrowAsync<ArgumentException>(() =>
            _repo.GetConsentPageAsync(new Autheris.Application.Governance.Paging.ConsentPageQuery(default)));
    }
}
