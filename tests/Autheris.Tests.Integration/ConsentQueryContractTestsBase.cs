namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
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
}
