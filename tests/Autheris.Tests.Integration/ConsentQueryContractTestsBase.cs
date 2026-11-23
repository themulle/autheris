namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// WP-SC-01 / SEC-SC-01 on a real engine: the indexed consent query returns exactly the former in-memory filter result
/// (case-insensitive SIDs, alias consents, validity, revocation, tenant). Every test seeds its own table, so tests do not interfere.
/// </summary>
public abstract class ConsentQueryContractTestsBase : IAsyncLifetime
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private static Autheris.Domain.Security.ConsentRevocationAuthority TenantAuthority(string sid, TenantId tenant) =>
        Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, sid),
                new System.Security.Claims.Claim("tenant_id", tenant.Value)
            ], "test")));

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
            IsActive = true
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

        async Task Add(Guid tid, TableIdentifier id, TenantId tenant, GranteeType type, string? sid,
            ConsentEffect effect, int fromDays, int toDays, bool revoke = false)
        {
            var c = await Repo.CreateConsentAsync(new Consent
            {
                TableId = tid, TableIdentifier = id, TenantId = tenant, GranteeType = type,
                GranteeSid = sid == null ? (Sid?)null : new Sid(sid), Effect = effect,
                ValidFrom = now.AddDays(fromDays), ValidTo = now.AddDays(toDays)
            });
            if (revoke)
            {
                await Repo.RevokeConsentAsync(c.Id, new Sid(sid!), "test", TenantAuthority(sid!, tenant));
                revoked.Add(c.Id);
            }

            consents.Add(c);
        }

        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-1000-1000-1", ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "a1b2c3d4-0000-0000-0000-000000000001", ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "s-1-5-21-bob", ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.Group, "S-1-5-21-GROUP-1", ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "alias:alice@corp", ConsentEffect.Allow, -1, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Deny, 2, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, 2, 10);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -10, -1);
        await Add(tableId, table, TenantA, GranteeType.User, "S-1-5-21-Carol", ConsentEffect.Allow, -1, 10, revoke: true);
        await Add(tableId, table, TenantB, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -1, 10);
        await Add(otherId, other, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -1, 10);
        return new Seed(now, table, other, tableId, consents, revoked);
    }

    private static HashSet<Guid> Oracle(Seed seed, string[] subjects, TenantId? tenant)
    {
        var set = subjects.Select(s => new Sid(s)).ToHashSet();
        return seed.Consents
            .Where(c => !seed.Revoked.Contains(c.Id) && c.TableId == seed.TableId && (tenant == null || c.TenantId == tenant))
            .Where(c => seed.Now < c.ValidTo && !(seed.Now < c.ValidFrom && c.Effect != ConsentEffect.Deny))
            .Where(c => c.GranteeSid != null && set.Contains(c.GranteeSid.Value))
            .Select(c => c.Id).ToHashSet();
    }

    public static IEnumerable<object[]> SubjectCases() =>
    [
        [new[] { "S-1-5-21-Alice" }],
        [new[] { "s-1-5-21-ALICE" }],
        [new[] { "s-1-5-21-1000-1000-1", "A1B2C3D4-0000-0000-0000-000000000001" }],
        [new[] { "s-1-5-21-bob", "S-1-5-21-GROUP-1" }],
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

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var actual = (await Repo.GetActiveConsentsForSubjectsAsync(sids, seed.Table, seed.Now, tenant))
                .Select(c => c.Id)
                .ToHashSet();
            actual.ShouldBe(Oracle(seed, subjects, tenant), ignoreOrder: true, customMessage: $"tenant={tenant.Value}");
        }
    }

    /// <summary>
    /// Issue #1043: the batch read returns, for every requested table (in any spelling), exactly the consents of the per-table read:
    /// subjects, validity, not-yet-valid denies, revocation, tenant and table matching are unchanged. Unknown tables map to no consents.
    /// </summary>
    [Theory]
    [MemberData(nameof(SubjectCases))]
    public async Task Issue1043_BatchRead_EqualsThePerTableReads(string[] subjects)
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var sids = subjects.Select(s => new Sid(s)).ToArray();
        var upper = new TableIdentifier(seed.Table.Domain.ToUpperInvariant(), seed.Table.Schema.ToUpperInvariant(), seed.Table.TableName.ToUpperInvariant());
        var unknown = new TableIdentifier("Sales", "Crm", "Missing_" + Guid.NewGuid().ToString("N")[..8]);

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var batch = await Repo.GetActiveConsentsForSubjectsBatchAsync(sids, [upper, seed.Other, unknown], seed.Now, tenant);

            foreach (var table in new[] { upper, seed.Other, unknown })
            {
                var single = (await Repo.GetActiveConsentsForSubjectsAsync(sids, table, seed.Now, tenant)).ToList();
                batch.ShouldContainKey(table);
                var fromBatch = batch[table];
                fromBatch.Select(static c => c.Id).ShouldBe(single.Select(static c => c.Id), ignoreOrder: true, customMessage: $"table={table} tenant={tenant.Value}");
                fromBatch.ShouldAllBe(c => c.TableIdentifier.Equals(table));

                // Hydrated like the per-table read.
                foreach (var consent in fromBatch)
                {
                    var twin = single.Single(c => c.Id == consent.Id);
                    consent.ColumnRules.Count.ShouldBe(twin.ColumnRules.Count);
                    consent.RowFilters.Count.ShouldBe(twin.RowFilters.Count);
                    consent.Effect.ShouldBe(twin.Effect);
                    consent.ValidFrom.ShouldBe(twin.ValidFrom);
                    consent.ValidTo.ShouldBe(twin.ValidTo);
                }
            }

            batch[upper].Select(static c => c.Id).ToHashSet().ShouldBe(Oracle(seed, subjects, tenant), ignoreOrder: true);
        }
    }

    [Fact]
    public async Task SEC_SC_01_TableNameMatchIsCaseInsensitive_AndFutureDenyIsKept()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var upper = new TableIdentifier(seed.Table.Domain.ToUpperInvariant(), seed.Table.Schema.ToUpperInvariant(), seed.Table.TableName.ToUpperInvariant());

        var result = await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice")], upper, seed.Now, TenantA);

        result.ShouldContain(c => c.Effect == ConsentEffect.Deny && c.ValidFrom > seed.Now);
        result.ShouldNotContain(c => c.Effect == ConsentEffect.Allow && c.ValidFrom > seed.Now);
        result.ShouldAllBe(c => c.TableId == seed.TableId && c.TenantId == TenantA);
    }

    [Fact]
    public async Task SEC_SC_01_ThousandsOfSubjects_AreChunkedAndStillExact()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var sids = Enumerable.Range(0, 5000).Select(i => new Sid($"S-1-5-21-X{i}")).Append(new Sid("S-1-5-21-Alice")).ToArray();

        var result = await Repo.GetActiveConsentsForSubjectsAsync(sids, seed.Table, seed.Now, TenantA);

        result.ShouldContain(c => c.GranteeSid != null && c.GranteeSid.Value.Value == "S-1-5-21-Alice");
        result.Select(c => c.Id).Distinct().Count().ShouldBe(result.Count);
    }

    // ---- WP-SC-02 / SEC-SC-03: atomic replace ----

    private static Consent NewConsent(Seed seed, GranteeType type, string? sid, TenantId tenant,
        ConsentEffect effect = ConsentEffect.Allow, int fromDays = -1, double toDays = 30) => new()
    {
        TableId = seed.TableId, TableIdentifier = seed.Table, TenantId = tenant, GranteeType = type, Effect = effect,
        GranteeSid = sid == null ? (Sid?)null : new Sid(sid),
        ValidFrom = seed.Now.AddDays(fromDays), ValidTo = seed.Now.AddDays(toDays), CreatedBySid = new Sid("SYSTEM")
    };

    private static Consent OffsetConsent(Seed seed, string sid, DateTimeOffset from, DateTimeOffset to, TimeSpan offset) => new()
    {
        TableId = seed.TableId, TableIdentifier = seed.Table, TenantId = TenantA, GranteeType = GranteeType.User, GranteeSid = new Sid(sid),
        ValidFrom = from.ToOffset(offset), ValidTo = to.ToOffset(offset), CreatedBySid = new Sid("SYSTEM")
    };

    [Fact]
    public async Task SEC_SC_03_Replace_IsAtomic_AndBumpsTheEpochOnce()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var alice = new Sid("S-1-5-21-Alice");
        var old = seed.Consents.First(c => c.TenantId == TenantA && c.GranteeSid?.Value == "S-1-5-21-Alice" && c.Effect == ConsentEffect.Allow && c.ValidFrom < seed.Now && c.ValidTo > seed.Now);
        var before = await Repo.GetTableEpochAsync(seed.Table);

        await Repo.ReplaceConsentsAsync([old.Id], [NewConsent(seed, GranteeType.User, "S-1-5-21-Dave", TenantA), NewConsent(seed, GranteeType.User, "S-1-5-21-Erin", TenantA)], alice, "apply", TenantAuthority(alice.Value, TenantA));

        (await Repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeTrue();
        (await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Dave"), new Sid("S-1-5-21-Erin")], seed.Table, seed.Now, TenantA)).Count.ShouldBe(2);
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
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "GovernanceAdmin"),
            new System.Security.Claims.Claim("tenant_id", TenantA.Value)
        ], "test"));
        var policy = new DeclarativeConsentPolicy(seed.Table.Domain, seed.Table.Schema, seed.Table.TableName, "Group", "S-1-5-21-MANIFEST-GROUP", "Allow", Description: "integration test grant", ValidUntil: DateTimeOffset.UtcNow.AddDays(30));
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
        (await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-MANIFEST-GROUP")], seed.Table, DateTimeOffset.UtcNow, TenantA)).Count(c => c.GranteeType == GranteeType.Group).ShouldBe(1);
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

        await Should.ThrowAsync<Exception>(() => Repo.ReplaceConsentsAsync([old.Id], [clash], new Sid("S-1-5-21-Alice"), "apply", TenantAuthority("S-1-5-21-Alice", TenantA)));

        (await Repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeFalse();
        (await Repo.GetTableEpochAsync(seed.Table)).ShouldBe(before);
        (await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Frank")], seed.Table, seed.Now, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SEC_SC_03_Replace_UnauthorizedActorOrUnknownId_ChangesNothing()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        var old = seed.Consents.First(c => c.TenantId == TenantA && c.GranteeSid?.Value == "S-1-5-21-Alice" && c.Effect == ConsentEffect.Allow && c.ValidFrom < seed.Now && c.ValidTo > seed.Now);
        var stranger = TenantAuthority("S-1-5-21-STRANGER", TenantA);
        var aliceAuth = TenantAuthority("S-1-5-21-Alice", TenantA);

        await Should.ThrowAsync<Autheris.Domain.Exceptions.ConsentRevocationForbiddenException>(() =>
            Repo.ReplaceConsentsAsync([old.Id], [NewConsent(seed, GranteeType.User, "S-1-5-21-Gina", TenantA)], new Sid("S-1-5-21-STRANGER"), "x", stranger));
        await Should.ThrowAsync<KeyNotFoundException>(() =>
            Repo.ReplaceConsentsAsync([Guid.NewGuid()], [NewConsent(seed, GranteeType.User, "S-1-5-21-Gina", TenantA)], new Sid("S-1-5-21-Alice"), "x", aliceAuth));

        (await Repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeFalse();
        (await Repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Gina")], seed.Table, seed.Now, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SEC_SC_20_LegacyPermanentDeny_StaysActive_WhileALegacyAllowIsClamped()
    {
        // The read-time clamp exists only for an operator-configured limit (no compiled-in ceiling).
        Autheris.Domain.Model.IndividualGrantLimits.Configure(TimeSpan.FromDays(180), null, null);
        try
        {
            if (!Available) return;
            var seed = await SeedAsync();
            var sid = new Sid("S-1-5-21-Legacy");
            var deny = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, sid.Value, TenantA, ConsentEffect.Deny, -400, 365 * 50));
            var allow = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, sid.Value, TenantA, ConsentEffect.Allow, -400, 365 * 50));

            var active = await Repo.GetActiveConsentsForSubjectsAsync([sid], seed.Table, seed.Now, TenantA);

            active.ShouldContain(c => c.Id == deny.Id);
            active.ShouldNotContain(c => c.Id == allow.Id);
        }
        finally
        {
            Autheris.Domain.Model.IndividualGrantLimits.Reset();
        }
    }
    [Fact]
    public async Task Expiring_ExcludesAlreadyExpired()
    {
        // PERS-04: an expired consent is never returned (and never re-ticketed), on every provider.
        if (!Available) return;
        var seed = await SeedAsync();
        var expired = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-ExpA", TenantA, ConsentEffect.Allow, -200, -1));
        var expiring = await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-ExpB", TenantA, ConsentEffect.Allow, -20, 5));

        var result = await Repo.GetExpiringConsentsAsync(seed.Now.AddDays(30));

        result.ShouldNotContain(c => c.Id == expired.Id);
        result.ShouldContain(c => c.Id == expiring.Id);
    }

    [Fact]
    public async Task Expiring_RespectsMaxResults()
    {
        if (!Available) return;
        var seed = await SeedAsync();
        await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-Lim1", TenantA, ConsentEffect.Allow, -20, 3));
        await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-Lim2", TenantA, ConsentEffect.Allow, -20, 4));
        await Repo.CreateConsentAsync(NewConsent(seed, GranteeType.User, "S-1-5-21-Lim3", TenantA, ConsentEffect.Allow, -20, 5));

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
        (await Repo.GetConsentPageAsync(query with { GranteeType = GranteeType.Group, PageSize = 500 })).Items.ShouldAllBe(c => c.GranteeType == GranteeType.Group);
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetConsentPageAsync(new Autheris.Application.Governance.Paging.ConsentPageQuery(default)));
    }
}
