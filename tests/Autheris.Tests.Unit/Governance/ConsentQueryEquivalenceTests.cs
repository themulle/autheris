using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// WP-SC-01 / SEC-SC-01: the indexed consent query returns exactly what the former in-memory subject filter returned
/// (case-insensitive SIDs, role and alias consents, validity, revocation, tenant). The oracle below is the former C# filter
/// applied to every seeded consent.
/// </summary>
[Trait("Category", "Security")]
public sealed class ConsentQueryEquivalenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private readonly TableIdentifier _other = new("Sales", "Crm", "Other_" + Guid.NewGuid().ToString("N")[..6]);
    private readonly List<Consent> _seeded = [];
    private Guid _tableId;
    private Guid _otherId;

    public void Dispose() => _repo.Dispose();

    private async Task<Guid> UpsertTableAsync(TableIdentifier id)
    {
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            Sensitivity = "LOW", RequiresFourEyes = false, IsActive = true
        };
        var meta = await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        return meta.Table.Id;
    }

    private async Task SeedAsync()
    {
        _tableId = await UpsertTableAsync(_table);
        _otherId = await UpsertTableAsync(_other);

        async Task Add(Guid tableId, TableIdentifier id, TenantId tenant, GranteeType type, string? sid, string? role,
            ConsentEffect effect, int fromDays, int toDays, bool revoke = false)
        {
            var c = await _repo.CreateConsentAsync(new Consent
            {
                TableId = tableId, TableIdentifier = id, TenantId = tenant, GranteeType = type,
                GranteeSid = sid == null ? (Sid?)null : new Sid(sid), RoleName = role, Effect = effect,
                ValidFrom = Now.AddDays(fromDays), ValidTo = Now.AddDays(toDays)
            });
            if (revoke)
            {
                await _repo.RevokeConsentAsync(c.Id, new Sid(sid ?? "SYSTEM"), "test");
            }

            _seeded.Add(c);
        }

        // users in mixed case, group, alias SID, role consent
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "s-1-5-21-bob", null, ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.Group, "S-1-5-21-GROUP-1", null, ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "alias:alice@corp", null, ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.Role, null, "analyst", ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Deny, 2, 10); // future deny
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, 2, 10); // future allow
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -10, -1); // expired
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Carol", null, ConsentEffect.Allow, -1, 10, revoke: true);
        await Add(_tableId, _table, TenantB, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -1, 10); // other tenant
        await Add(_tableId, _table, TenantB, GranteeType.Role, null, "analyst", ConsentEffect.Allow, -1, 10);
        await Add(_otherId, _other, TenantA, GranteeType.User, "S-1-5-21-Alice", null, ConsentEffect.Allow, -1, 10); // other table
    }

    private static HashSet<Guid> OracleForTable(IEnumerable<Consent> all, Guid tableId, IEnumerable<string> subjects, TenantId? tenant)
    {
        var set = subjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all
            .Where(c => c.TableId == tableId && (tenant == null || c.TenantId == tenant))
            .Where(c => Now < c.ValidTo)
            .Where(c => !(Now < c.ValidFrom && c.Effect != ConsentEffect.Deny))
            .Where(c => c.GranteeType == GranteeType.Role || (c.GranteeSid != null && set.Contains(c.GranteeSid.Value.Value)))
            .Select(c => c.Id)
            .ToHashSet();
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
    public async Task SubjectQuery_EqualsFormerFilter_ForEveryCaseAndTenant(string[] subjects)
    {
        await SeedAsync();
        var sids = subjects.Select(s => new Sid(s)).ToArray();

        foreach (var tenant in new TenantId?[] { TenantA, TenantB, null })
        {
            var actual = (await _repo.GetActiveConsentsForSubjectsAsync(sids, _table, Now, tenant)).Select(c => c.Id).ToHashSet();
            var expected = OracleForTable(_seeded.Where(c => !IsRevoked(c)), _tableId, subjects, tenant);
            actual.ShouldBe(expected, ignoreOrder: true, customMessage: $"tenant={tenant?.Value ?? "<none>"}");
        }
    }

    private bool IsRevoked(Consent c) => c.GranteeSid?.Value == "S-1-5-21-Carol";

    [Fact]
    public async Task SubjectQuery_NoSubjects_ReturnsNothing()
    {
        await SeedAsync();
        (await _repo.GetActiveConsentsForSubjectsAsync([], _table, Now, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SubjectQuery_TableNameMatchIsCaseInsensitive()
    {
        await SeedAsync();
        var upper = new TableIdentifier(_table.Domain.ToUpperInvariant(), _table.Schema.ToUpperInvariant(), _table.TableName.ToUpperInvariant());

        var result = await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice")], upper, Now, TenantA);

        result.ShouldNotBeEmpty();
        result.ShouldAllBe(c => c.TableId == _tableId);
    }

    [Fact]
    public async Task SubjectQuery_FutureDatedDenyIsReturned_FutureAllowIsNot()
    {
        await SeedAsync();

        var result = await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice")], _table, Now, TenantA);

        result.ShouldContain(c => c.Effect == ConsentEffect.Deny && c.ValidFrom > Now);
        result.ShouldNotContain(c => c.Effect == ConsentEffect.Allow && c.ValidFrom > Now);
        result.ShouldNotContain(c => c.ValidTo <= Now);
    }

    [Fact]
    public async Task SubjectQuery_RoleConsentIsReturnedWithoutSubjectMatch_ButNeverAcrossTenants()
    {
        await SeedAsync();

        var result = await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Nobody")], _table, Now, TenantA);

        result.Count.ShouldBe(1);
        result[0].GranteeType.ShouldBe(GranteeType.Role);
        result[0].TenantId.ShouldBe(TenantA);
    }

    [Fact]
    public async Task SubjectQuery_ManySubjects_AreChunkedAndStillExact()
    {
        await SeedAsync();
        var sids = Enumerable.Range(0, 5000).Select(i => new Sid($"S-1-5-21-X{i}")).Append(new Sid("S-1-5-21-ALICE")).ToArray();

        var result = await _repo.GetActiveConsentsForSubjectsAsync(sids, _table, Now, TenantA);

        result.ShouldContain(c => c.GranteeSid != null && c.GranteeSid.Value.Value == "S-1-5-21-Alice");
    }

    [Theory]
    [InlineData("analyst")]
    [InlineData("ANALYST")]
    [InlineData("other")]
    public async Task AllQuery_RoleConsentsAreFilteredByRole_CaseInsensitive(string role)
    {
        await SeedAsync();

        var result = await _repo.GetAllActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-Alice")], [role], Now, TenantA);

        var roleConsents = result.Where(c => c.GranteeType == GranteeType.Role).ToList();
        if (role.Equals("other", StringComparison.Ordinal))
        {
            roleConsents.ShouldBeEmpty();
        }
        else
        {
            roleConsents.Count.ShouldBe(1);
        }

        result.ShouldAllBe(c => c.TenantId == TenantA);
        result.ShouldNotContain(c => c.ValidTo <= Now || c.ValidFrom > Now);
    }

    [Fact]
    public async Task AllQuery_AcrossTables_EqualsFormerFilter()
    {
        await SeedAsync();

        var result = await _repo.GetAllActiveConsentsForSubjectsAsync(
            [new Sid("s-1-5-21-alice"), new Sid("S-1-5-21-GROUP-1")], ["Analyst"], Now, TenantA);

        var expected = _seeded
            .Where(c => c.TenantId == TenantA && !IsRevoked(c) && Now >= c.ValidFrom && Now < c.ValidTo)
            .Where(c => c.GranteeType == GranteeType.Role
                ? string.Equals(c.RoleName, "analyst", StringComparison.OrdinalIgnoreCase)
                : c.GranteeSid != null && new[] { "S-1-5-21-ALICE", "S-1-5-21-GROUP-1" }.Contains(c.GranteeSid.Value.Value, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Id).ToHashSet();
        result.Select(c => c.Id).ToHashSet().ShouldBe(expected, ignoreOrder: true);
    }
}
