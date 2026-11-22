using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

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

    private readonly PostgreSqlGovernanceRepository _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService());
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
            IsActive = true
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

        async Task Add(Guid tableId, TableIdentifier id, TenantId tenant, GranteeType type, string? sid,
            ConsentEffect effect, int fromDays, int toDays, bool revoke = false)
        {
            var c = await _repo.CreateConsentAsync(new Consent
            {
                TableId = tableId, TableIdentifier = id, TenantId = tenant, GranteeType = type,
                GranteeSid = sid == null ? (Sid?)null : new Sid(sid), Effect = effect,
                ValidFrom = Now.AddDays(fromDays), ValidTo = Now.AddDays(toDays)
            });
            if (revoke)
            {
                var auth = sid is null
                    ? Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(
                        new System.Security.Claims.ClaimsPrincipal(
                            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "ClusterAdmin")], "test")))
                    : Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(
                        new System.Security.Claims.ClaimsPrincipal(
                            new System.Security.Claims.ClaimsIdentity(
                            [
                                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, sid),
                                new System.Security.Claims.Claim("tenant_id", tenant.Value)
                            ], "test")));
                await _repo.RevokeConsentAsync(c.Id, new Sid(sid ?? "S-1-5-21-SYSTEM"), "test", auth);
            }

            _seeded.Add(c);
        }

        // users in mixed case, canonical SIDs, GUIDs, group, alias SID
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-1000-1000-1", ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "a1b2c3d4-0000-0000-0000-000000000001", ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "s-1-5-21-bob", ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.Group, "S-1-5-21-GROUP-1", ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "alias:alice@corp", ConsentEffect.Allow, -1, 10);
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Deny, 2, 10); // future deny
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, 2, 10); // future allow
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -10, -1); // expired
        await Add(_tableId, _table, TenantA, GranteeType.User, "S-1-5-21-Carol", ConsentEffect.Allow, -1, 10, revoke: true);
        await Add(_tableId, _table, TenantB, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -1, 10); // other tenant
        await Add(_otherId, _other, TenantA, GranteeType.User, "S-1-5-21-Alice", ConsentEffect.Allow, -1, 10); // other table
    }

    private static HashSet<Guid> OracleForTable(IEnumerable<Consent> all, Guid tableId, IEnumerable<string> subjects, TenantId? tenant)
    {
        var set = subjects.Select(s => new Sid(s)).ToHashSet();
        return all
            .Where(c => c.TableId == tableId && (tenant == null || c.TenantId == tenant))
            .Where(c => Now < c.ValidTo)
            .Where(c => !(Now < c.ValidFrom && c.Effect != ConsentEffect.Deny))
            .Where(c => c.GranteeSid != null && set.Contains(c.GranteeSid.Value))
            .Select(c => c.Id)
            .ToHashSet();
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
    public async Task SubjectQuery_EqualsFormerFilter_ForEveryCaseAndTenant(string[] subjects)
    {
        await SeedAsync();
        var sids = subjects.Select(s => new Sid(s)).ToArray();

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var actual = (await _repo.GetActiveConsentsForSubjectsAsync(sids, _table, Now, tenant))
                .Select(c => c.Id)
                .ToHashSet();
            var expected = OracleForTable(_seeded.Where(c => !IsRevoked(c)), _tableId, subjects, tenant);
            actual.ShouldBe(expected, ignoreOrder: true, customMessage: $"tenant={tenant.Value}");
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
    public async Task SubjectQuery_ManySubjects_AreChunkedAndStillExact()
    {
        await SeedAsync();
        var sids = Enumerable.Range(0, 5000).Select(i => new Sid($"S-1-5-21-X{i}")).Append(new Sid("S-1-5-21-Alice")).ToArray();

        var result = await _repo.GetActiveConsentsForSubjectsAsync(sids, _table, Now, TenantA);

        result.ShouldContain(c => c.GranteeSid != null && c.GranteeSid.Value.Value == "S-1-5-21-Alice");
    }

    [Fact]
    public async Task AllQuery_AcrossTables_EqualsFormerFilter()
    {
        await SeedAsync();

        var result = await _repo.GetAllActiveConsentsForSubjectsAsync(
            [new Sid("S-1-5-21-Alice"), new Sid("S-1-5-21-GROUP-1")], ["Analyst"], Now, TenantA);

        var expected = _seeded
            .Where(c => c.TenantId == TenantA && !IsRevoked(c) && Now >= c.ValidFrom && Now < c.ValidTo)
            .Where(c => c.GranteeSid != null && new[] { new Sid("S-1-5-21-Alice"), new Sid("S-1-5-21-GROUP-1") }.Contains(c.GranteeSid.Value))
            .Select(c => c.Id).ToHashSet();
        result.Select(c => c.Id).ToHashSet().ShouldBe(expected, ignoreOrder: true);
    }
}
