namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// WP-SC-01 / SEC-SC-01 on a real engine: the indexed consent query returns exactly the former in-memory filter result
/// (case-insensitive SIDs, role and alias consents, validity, revocation, tenant). Every test seeds its own table, so tests do not interfere.
/// </summary>
public abstract class ConsentQueryContractTestsBase : IAsyncLifetime
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    protected IGovernanceRepository Repo { get; private set; } = null!;
    protected bool Available { get; private set; }

    protected abstract Task<IGovernanceRepository?> CreateRepositoryAsync();

    protected abstract Task DisposeRepositoryAsync();

    /// <summary>Makes <paramref name="sid"/> a member of the GovernanceAdmin role (the manifest apply actor must be authorized to revoke).</summary>
    protected abstract Task GrantGovernanceAdminAsync(string sid);

    public async Task InitializeAsync()
    {
        var repo = await CreateRepositoryAsync();
        Available = repo != null;
        if (repo != null)
        {
            Repo = repo;
        }
    }

    public Task DisposeAsync() => DisposeRepositoryAsync();

    private sealed record Seed(DateTimeOffset Now, TableIdentifier Table, TableIdentifier Other, Guid TableId, List<Consent> Consents, HashSet<Guid> Revoked);

    private async Task<Guid> UpsertTableAsync(TableIdentifier id)
    {
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        var meta = await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        return meta.Table.Id;
    }

    private async Task<Seed> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = new TableIdentifier("Sales", "Crm", "Orders_" + suffix);
        var other = new TableIdentifier("Sales", "Crm", "Other_" + suffix);
        var tableId = await UpsertTableAsync(table);
        var otherId = await UpsertTableAsync(other);
        var consents = new List<Consent>();
        var revoked = new HashSet<Guid>();

        async Task Add(Guid tid, TableIdentifier id, TenantId tenant, GranteeType type, string? sid, string? role,
            ConsentEffect effect, int fromDays, int toDays, bool revoke = false)
        {
            var c = await Repo.CreateConsentAsync(new Consent
            {
                TableId = tid, TableIdentifier = id, TenantId = tenant, GranteeType = type,
                GranteeSid = sid == null ? (Sid?)null : new Sid(sid), RoleName = role, Effect = effect,
                ValidFrom = now.AddDays(fromDays), ValidTo = now.AddDays(toDays)
            });
            if (revoke)
            {
                await Repo.RevokeConsentAsync(c.Id, new Sid(sid!), "test");
                revoked.Add(c.Id);
            }

            consents.Add(c);
        }

        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "s-1-5-21-bob", null, ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.Group, "S-1-5-21-GROUP-1", null, ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "alias:alice@corp", null, ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.Role, null, "analyst", ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Deny, 2, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, 2, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -10, -1);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Carol", null, ConsentEffect.Allow, -1, 10, revoke: true);
        await Add(tableId, table, TenantB, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantB, GranteeType.Role, null, "analyst", ConsentEffect.Allow, -1, 10);
        await Add(otherId, other, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -1, 10);
        return new Seed(now, table, other, tableId, consents, revoked);
    }

    private static HashSet<Guid> Oracle(Seed seed, string[] subjects, TenantId? tenant)
    {
        var set = subjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return seed.Consents
            .Where(c => !seed.Revoked.Contains(c.Id) && c.TableId == seed.TableId && (tenant == null || c.TenantId == tenant))
            .Where(c => seed.Now < c.ValidTo && !(seed.Now < c.ValidFrom && c.Effect != ConsentEffect.Deny))
            .Where(c => c.GranteeType == GranteeType.Role || (c.GranteeSid != null && set.Contains(c.GranteeSid.Value.Value)))
            .Select(c => c.Id).ToHashSet();
    }

    public static IEnumerable<object[]> SubjectCases() =>
    [
        [new[] { "S-1-5-21-Alice" }],
        [new[] { "s-1-5-21-ALICE" }],
        [new[] { "S-1-5-21-BOB", "S-1-5-21-group-1" }],
        [new[] { "ALIAS:ALICE@CORP" }],
        [new[] { "S-1-5-21-Nobody" }],
        [new[] { "S-1-5-21-Carol" }],
        [new[] { "S-1-5-21-Alice", "s-1-5-21-bob", "S-1-5-21-GROUP-1", "alias:alice@corp" }]
    ];

    [Theory]
    [MemberData(nameof(SubjectCases))]
    public async Task SEC_SC_01_SubjectQuery_EqualsFormerFilter(string[] subjects)
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var sids = subjects.Select(s => new Sid(s)).ToArray();

        foreach (var tenant in new TenantId?[] { TenantA, TenantB, null })
        {
            var actual = (await Repo.GetActiveConsentsForSubjectsAsync(sids, seed.Table, seed.Now, tenant)).Select(c => c.Id).ToHashSet();
            actual.ShouldBe(Oracle(seed, subjects, tenant), ignoreOrder: true, customMessage: $"tenant={tenant?.Value ?? "<none>"}");
        }
    }

    [Fact]
    public async Task SEC_SC_01_TableNameMatchIsCaseInsensitive_AndFutureDenyIsKept()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var upper = new TableIdentifier(seed.Table.Domain.ToUpperInvariant(), seed.Table.Schema.ToUpperInvariant(), seed.Table.TableName.ToUpperInvariant());

        var result = await Repo.GetActiveConsentsForSubjectsAsync([new Sid("s-1-5-21-alice")], upper, seed.Now, TenantA);

        result.ShouldContain(c => c.Effect == ConsentEffect.Deny && c.ValidFrom > seed.Now);
        result.ShouldNotContain(c => c.Effect == ConsentEffect.Allow && c.ValidFrom > seed.Now);
        result.ShouldAllBe(c => c.TableId == seed.TableId && c.TenantId == TenantA);
    }

    [Fact]
    public async Task SEC_SC_01_RoleConsentIsReturnedWithoutSubjectMatch_ButNeverAcrossTenants()
    {
        if (!Available) return;
        var seed = await SeedAsync();

        var result = await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Nobody")], seed.Table, seed.Now, TenantA);

        result.Count.ShouldBe(1);
        result[0].GranteeType.ShouldBe(GranteeType.Role);
        result[0].TenantId.ShouldBe(TenantA);
    }

    [Fact]
    public async Task SEC_SC_01_ThousandsOfSubjects_AreChunkedAndStillExact()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var sids = Enumerable.Range(0, 5000).Select(i => new Sid($"S-1-5-21-X{i}")).Append(new Sid("S-1-5-21-ALICE")).ToArray();

        var result = await Repo.GetActiveConsentsForSubjectsAsync(sids, seed.Table, seed.Now, TenantA);

        result.ShouldContain(c => c.GranteeSid != null && c.GranteeSid.Value.Value == "S-1-5-21-Alice");
        result.Select(c => c.Id).Distinct().Count().ShouldBe(result.Count);
    }

    [Theory]
    [InlineData("analyst", 1)]
    [InlineData("ANALYST", 1)]
    [InlineData("other", 0)]
    public async Task SEC_SC_01_AllQuery_FiltersRolesCaseInsensitive_AndByTenant(string role, int expectedRoleConsents)
    {
        if (!Available) return;
        var seed = await SeedAsync();

        var result = await Repo.GetAllActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice")], [role], seed.Now, TenantA);

        result.Where(c => c.TableId == seed.TableId && c.GranteeType == GranteeType.Role).Count().ShouldBe(expectedRoleConsents);
        result.ShouldAllBe(c => c.TenantId == TenantA);
        result.ShouldNotContain(c => c.ValidTo <= seed.Now || c.ValidFrom > seed.Now);
        result.ShouldContain(c => c.TableId == seed.TableId && c.GranteeType == GranteeType.User);
    }

    [Fact]
    public async Task SEC_SC_01_AllQuery_WithoutSubjects_StillReturnsMatchingRoleConsents()
    {
        if (!Available) return;
        var seed = await SeedAsync();

        var result = await Repo.GetAllActiveConsentsForSubjectsAsync([], ["analyst"], seed.Now, TenantA);

        result.ShouldContain(c => c.TableId == seed.TableId && c.GranteeType == GranteeType.Role);
        result.ShouldNotContain(c => c.GranteeType != GranteeType.Role);
    }

    // ---- WP-SC-02 / SEC-SC-03: role lookup and atomic replace ----

    private static Consent NewConsent(Seed seed, GranteeType type, string? sid, string? role, TenantId tenant,
        ConsentEffect effect = ConsentEffect.Allow, int fromDays = -1, double toDays = 30) => new()
    {
        TableId = seed.TableId, TableIdentifier = seed.Table, TenantId = tenant, GranteeType = type, Effect = effect,
        GranteeSid = sid == null ? (Sid?)null : new Sid(sid), RoleName = role,
        ValidFrom = seed.Now.AddDays(fromDays), ValidTo = seed.Now.AddDays(toDays), CreatedBySid = new Sid("SYSTEM")
    };

    private static Consent OffsetConsent(Seed seed, string sid, DateTimeOffset from, DateTimeOffset to, TimeSpan offset) => new()
    {
        TableId = seed.TableId, TableIdentifier = seed.Table, TenantId = TenantA, GranteeType = GranteeType.User, GranteeSid = new Sid(sid),
        ValidFrom = from.ToOffset(offset), ValidTo = to.ToOffset(offset), CreatedBySid = new Sid("SYSTEM")
    };

    [Fact]
    public async Task SEC_SC_03_GetActiveRoleConsents_MatchesRoleTenantAndTableOnly()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var expected = seed.Consents.Where(c => c.GranteeType == GranteeType.Role && c.TenantId == TenantA).Select(c => c.Id).ToList();

        var result = await Repo.GetActiveRoleConsentsAsync(seed.Table, "ANALYST", TenantA, seed.Now);

        result.Select(c => c.Id).ShouldBe(expected, ignoreOrder: true);
        (await Repo.GetActiveRoleConsentsAsync(seed.Table, "nobody", TenantA, seed.Now)).ShouldBeEmpty();
        (await Repo.GetActiveRoleConsentsAsync(seed.Other, "analyst", TenantA, seed.Now)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SEC_SC_03_Replace_IsAtomic_AndBumpsTheEpochOnce()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var alice = new Sid("S-1-5-21-Alice");
        var old = seed.Consents.First(c => c.TenantId == TenantA && c.GranteeSid?.Value == "S-1-5-21-Alice" && c.Effect == ConsentEffect.Allow && c.ValidFrom < seed.Now && c.ValidTo > seed.Now);
        var before = await Repo.GetTableEpochAsync(seed.Table);

        await Repo.ReplaceConsentsAsync([old.Id], [NewConsent(seed, GranteeType.User, "S-1-5-21-Dave", null, TenantA), NewConsent(seed, GranteeType.User, "S-1-5-21-Erin", null, TenantA)], alice, "apply");

        (await Repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeTrue();
        (await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Dave"), new Sid("S-1-5-21-Erin")], seed.Table, seed.Now, TenantA)).Count(c => c.GranteeType != GranteeType.Role).ShouldBe(2);
        (await Repo.GetTableEpochAsync(seed.Table)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task ManifestApply_EndToEnd_UsesTheTransactionalReplace_IsIdempotentAndRevokesChangedPolicies()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        await GrantGovernanceAdminAsync("S-1-5-21-MANIFEST-ADMIN");
        var svc = new Autheris.Application.Governance.Services.DeclarativeConsentApplyService(Repo, Repo, Repo);
        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "S-1-5-21-MANIFEST-ADMIN"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "GOVERNANCE_ADMIN"),
            new System.Security.Claims.Claim("tenant_id", TenantA.Value)
        ], "test"));
        var policy = new DeclarativeConsentPolicy(seed.Table.Domain, seed.Table.Schema, seed.Table.TableName, "Role", "manifest-role", "Allow");
        var epochBefore = await Repo.GetTableEpochAsync(seed.Table);

        var first = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("1", [policy]));
        var epochAfterFirst = await Repo.GetTableEpochAsync(seed.Table);
        var second = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("1", [policy]));
        var third = await svc.ApplyManifestAsync(caller, new DeclarativeConsentManifest("2", [policy with { ColumnRules = [new DeclarativeColumnRule("id", "Mask")] }]));

        first.Applied.ShouldBe(1);
        second.Unchanged.ShouldBe(1);
        second.Applied.ShouldBe(0);
        third.Applied.ShouldBe(1);
        third.Revoked.ShouldBe(1);
        epochAfterFirst.ShouldBe(epochBefore + 1);
        (await Repo.GetTableEpochAsync(seed.Table)).ShouldBe(epochAfterFirst + 1);
        (await Repo.GetActiveRoleConsentsAsync(seed.Table, "manifest-role", TenantA, DateTimeOffset.UtcNow)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task SEC_SC_03_Replace_FaultAfterRevoke_RollsBackEverything()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var old = seed.Consents.First(c => c.TenantId == TenantA && c.GranteeSid?.Value == "S-1-5-21-Alice" && c.Effect == ConsentEffect.Allow && c.ValidFrom < seed.Now && c.ValidTo > seed.Now);
        var before = await Repo.GetTableEpochAsync(seed.Table);
        var clash = new Consent
        {
            Id = old.Id, TableId = seed.TableId, TableIdentifier = seed.Table, TenantId = TenantA, GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-Frank"), ValidFrom = seed.Now.AddDays(-1), ValidTo = seed.Now.AddDays(3)
        };

        await Should.ThrowAsync<Exception>(() => Repo.ReplaceConsentsAsync([old.Id], [clash], new Sid("S-1-5-21-Alice"), "apply"));

        (await Repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeFalse();
        (await Repo.GetTableEpochAsync(seed.Table)).ShouldBe(before);
        (await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Frank")], seed.Table, seed.Now, TenantA)).ShouldNotContain(c => c.GranteeType != GranteeType.Role);
    }

    [Fact]
    public async Task SEC_SC_03_Replace_UnauthorizedActorOrUnknownId_ChangesNothing()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var old = seed.Consents.First(c => c.TenantId == TenantA && c.GranteeSid?.Value == "S-1-5-21-Alice" && c.Effect == ConsentEffect.Allow && c.ValidFrom < seed.Now && c.ValidTo > seed.Now);

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            Repo.ReplaceConsentsAsync([old.Id], [NewConsent(seed, GranteeType.User, "S-1-5-21-Gina", null, TenantA)], new Sid("S-1-5-21-STRANGER"), "x"));
        await Should.ThrowAsync<KeyNotFoundException>(() =>
            Repo.ReplaceConsentsAsync([Guid.NewGuid()], [NewConsent(seed, GranteeType.User, "S-1-5-21-Gina", null, TenantA)], new Sid("S-1-5-21-Alice"), "x"));

        (await Repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeFalse();
        (await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Gina")], seed.Table, seed.Now, TenantA)).ShouldNotContain(c => c.GranteeType != GranteeType.Role);
    }

    [Fact]
    public async Task SEC_SC_20_LegacyPermanentDeny_StaysActive_WhileALegacyAllowIsClamped()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var sid = new Sid("S-1-5-21-Legacy");
        var deny = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, sid.Value, null, TenantA, ConsentEffect.Deny, -400, 365 * 50));
        var allow = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, sid.Value, null, TenantA, ConsentEffect.Allow, -400, 365 * 50));

        var active = await Repo.GetActiveConsentsForSubjectsAsync([sid], seed.Table, seed.Now, TenantA);

        active.ShouldContain(c => c.Id == deny.Id);
        active.ShouldNotContain(c => c.Id == allow.Id);
    }

    [Fact]
    public async Task SEC_SC_20_Renewal_RestartsTheClampPeriod_AtDay150()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var d150 = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-R150", null, TenantA, ConsentEffect.Allow, -150, 10));

        await Repo.ExtendConsentExpiryAsync(d150.Id, seed.Now.AddDays(170));

        (await Repo.GetConsentByIdAsync(d150.Id))!.IsActive(seed.Now.AddDays(100)).ShouldBeTrue();
    }

    private async Task AssertExtendRejectedAsync(Seed seed, Guid consentId, DateTimeOffset newValidTo)
    {
        var before = await Repo.GetConsentByIdAsync(consentId);
        var epochBefore = await Repo.GetTableEpochAsync(seed.Table);
        var auditBefore = (await Repo.GetAuditLogEntriesAsync(1000)).Count(e => e.EventType == "CONSENT_EXPIRY_EXTENDED");

        var ex = await Should.ThrowAsync<ConsentNotExtendableException>(() => Repo.ExtendConsentExpiryAsync(consentId, newValidTo));

        ex.Message.ShouldNotContain(consentId.ToString());
        var after = await Repo.GetConsentByIdAsync(consentId);
        after?.ValidTo.ShouldBe(before!.ValidTo);
        after?.ValidFrom.ShouldBe(before!.ValidFrom);
        after?.RenewalCount.ShouldBe(before!.RenewalCount);
        (await Repo.GetTableEpochAsync(seed.Table)).ShouldBe(epochBefore);
        (await Repo.GetAuditLogEntriesAsync(1000)).Count(e => e.EventType == "CONSENT_EXPIRY_EXTENDED").ShouldBe(auditBefore);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_ExpiredConsent_IsRejected_RowAndRenewalCountUnchanged()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var expired = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-RExp", null, TenantA, ConsentEffect.Allow, -200, -1));

        await AssertExtendRejectedAsync(seed, expired.Id, seed.Now.AddDays(60));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_RevokedConsent_IsRejected()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-RRev", null, TenantA, ConsentEffect.Allow, -1, 30));
        await Repo.RevokeConsentAsync(consent.Id, new Sid("S-1-5-21-Admin"), "no longer needed");

        await AssertExtendRejectedAsync(seed, consent.Id, seed.Now.AddDays(60));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_UnknownId_IsRejected_NoAuditNoEpoch()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var epochBefore = await Repo.GetTableEpochAsync(seed.Table);
        var auditBefore = (await Repo.GetAuditLogEntriesAsync(1000)).Count(e => e.EventType == "CONSENT_EXPIRY_EXTENDED");

        await Should.ThrowAsync<ConsentNotExtendableException>(() => Repo.ExtendConsentExpiryAsync(Guid.NewGuid(), seed.Now.AddDays(60)));

        (await Repo.GetTableEpochAsync(seed.Table)).ShouldBe(epochBefore);
        (await Repo.GetAuditLogEntriesAsync(1000)).Count(e => e.EventType == "CONSENT_EXPIRY_EXTENDED").ShouldBe(auditBefore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_EqualOrEarlierValidTo_IsRejected(int deltaDays)
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-REq", null, TenantA, ConsentEffect.Allow, -1, 30));

        await AssertExtendRejectedAsync(seed, consent.Id, consent.ValidTo.AddDays(deltaDays));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_Rejection_DoesNotBumpEpoch()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-REpoch", null, TenantA, ConsentEffect.Allow, -1, 30));
        var epochBefore = await Repo.GetTableEpochAsync(seed.Table);

        await Should.ThrowAsync<ConsentNotExtendableException>(() => Repo.ExtendConsentExpiryAsync(consent.Id, consent.ValidTo.AddDays(-1)));

        (await Repo.GetTableEpochAsync(seed.Table)).ShouldBe(epochBefore);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_UsesServerTimeInTheUpdatePredicate()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-RSrv", null, TenantA, ConsentEffect.Allow, -5, 0));

        // valid_to was a moment ago when the UPDATE runs; the SQL predicate (not a caller-side check) must reject it.
        await Should.ThrowAsync<ConsentNotExtendableException>(() => Repo.ExtendConsentExpiryAsync(consent.Id, seed.Now.AddDays(60)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_ExpiryAtExactNow_IsRejected()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var consent = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-RNow", null, TenantA, ConsentEffect.Allow, -5, 0));

        await Should.ThrowAsync<ConsentNotExtendableException>(() => Repo.ExtendConsentExpiryAsync(consent.Id, seed.Now.AddDays(60)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Extend_NonUtcStoredValidTo_ComparesCorrectly()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var plus14 = TimeSpan.FromHours(14);
        var minus10 = TimeSpan.FromHours(-10);
        var active = await Repo.CreateConsentAsync(OffsetConsent(seed, "S-1-5-21-RTzA", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(1), plus14));
        var expired = await Repo.CreateConsentAsync(OffsetConsent(seed, "S-1-5-21-RTzE", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddHours(-1), minus10));

        await Repo.ExtendConsentExpiryAsync(active.Id, DateTimeOffset.UtcNow.AddDays(10).ToOffset(plus14));
        await AssertExtendRejectedAsync(seed, expired.Id, DateTimeOffset.UtcNow.AddDays(10));
    }

    [Fact]
    public async Task SEC_SC_20_RenewalCount_IsPersistedAndIncrementedByExtensions()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var consent = seed.Consents.First(c => c.TenantId == TenantA && string.Equals(c.GranteeSid?.Value, "S-1-5-21-Bob", StringComparison.OrdinalIgnoreCase));
        (await Repo.GetConsentByIdAsync(consent.Id))!.RenewalCount.ShouldBe(0);

        await Repo.ExtendConsentExpiryAsync(consent.Id, seed.Now.AddDays(60));
        await Repo.ExtendConsentExpiryAsync(consent.Id, seed.Now.AddDays(90));

        (await Repo.GetConsentByIdAsync(consent.Id))!.RenewalCount.ShouldBe(2);
        (await Repo.GetExpiringConsentsAsync(seed.Now.AddDays(100))).Single(c => c.Id == consent.Id).RenewalCount.ShouldBe(2);
    }

    [Fact]
    public async Task Expiring_ExcludesAlreadyExpired()
    {
        // PERS-04: an expired consent is never returned (and never re-ticketed), on every provider.
        if (!Available) return;
        var seed = await SeedAsync();
        var expired = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-ExpA", null, TenantA, ConsentEffect.Allow, -200, -1));
        var expiring = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-ExpB", null, TenantA, ConsentEffect.Allow, -20, 5));

        var result = await Repo.GetExpiringConsentsAsync(seed.Now.AddDays(30));

        result.ShouldNotContain(c => c.Id == expired.Id);
        result.ShouldContain(c => c.Id == expiring.Id);
    }

    [Fact]
    public async Task Expiring_RespectsMaxResults()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-Lim1", null, TenantA, ConsentEffect.Allow, -20, 3));
        await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-Lim2", null, TenantA, ConsentEffect.Allow, -20, 4));
        await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-Lim3", null, TenantA, ConsentEffect.Allow, -20, 5));

        var result = await Repo.GetExpiringConsentsAsync(seed.Now.AddDays(30), maxResults: 2);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SEC_SC_16_ConsentPage_WalksTheTenantOnce_FiltersAndFailsClosedWithoutTenant()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var expected = seed.Consents.Where(c => c.TenantId == TenantA && c.TableId == seed.TableId).Select(c => c.Id).ToHashSet();
        var query = new Autheris.Application.Governance.Paging.ConsentPageQuery(TenantA, Table: seed.Table, IncludeInactive: true, PageSize: 4);

        var seen = new List<Guid>();
        Guid? after = null;
        do
        {
            var page = await Repo.GetConsentPageAsync(query with { AfterId = after });
            page.Items.ShouldAllBe(c => c.TenantId == TenantA && c.TableId == seed.TableId);
            seen.AddRange(page.Items.Select(c => c.Id));
            after = page.NextAfterId;
        }
        while (after != null);

        seen.Count.ShouldBe(seen.Distinct().Count());
        seen.ShouldBe(expected, ignoreOrder: true);
        (await Repo.GetConsentPageAsync(query with { PageSize = 500, IncludeInactive = false, Grantee = "s-1-5-21-alice" }))
            .Items.ShouldAllBe(c => c.GranteeSid != null && c.GranteeSid.Value.Value.Equals("S-1-5-21-Alice", StringComparison.OrdinalIgnoreCase));
        (await Repo.GetConsentPageAsync(query with { GranteeType = GranteeType.Role, PageSize = 500 })).Items.ShouldAllBe(c => c.GranteeType == GranteeType.Role);
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetConsentPageAsync(new Autheris.Application.Governance.Paging.ConsentPageQuery(default)));
    }
}
