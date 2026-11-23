using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>
/// Real-store checks for deprovisioning grant revocation: the kind-complete enumeration sees UNMASK grants that the data-path read
/// hides, and the by-id system revocation is tenant-scoped.
/// </summary>
public sealed class DeprovisioningGrantRevocationIntegrationTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");
    private static readonly Sid Subject = new("S-1-5-21-Deprovisioned");
    private static readonly Sid SystemActor = new("SYSTEM:DEPROVISIONING");

    private readonly PostgreSqlGovernanceRepository _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService());
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private Guid _tableId;

    public void Dispose() => _repo.Dispose();

    private async Task EnsureTableAsync()
    {
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = _table.Domain, SchemaName = _table.Schema, TableName = _table.TableName,
            Description = "deprovisioning", IsActive = true
        };
        _tableId = (await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = _table, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "ssn", DataType = "VARCHAR" }]
        })).Table.Id;
    }

    private Task<Consent> CreateAsync(PermissionGrantKind kind) =>
        _repo.CreateConsentAsync(new Consent
        {
            TableId = _tableId, TableIdentifier = _table, TenantId = TenantA, GranteeType = GranteeType.User, GranteeSid = Subject, Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30), CreatedBySid = Admin,
            GrantKind = kind, TargetColumns = kind == PermissionGrantKind.Unmask ? ["ssn"] : null
        });

    [Trait("Category", "Security")]
    [Fact]
    public async Task Revokes_unmask_grants()
    {
        await EnsureTableAsync();
        var data = await CreateAsync(PermissionGrantKind.Data);
        var unmask = await CreateAsync(PermissionGrantKind.Unmask);
        var now = DateTimeOffset.UtcNow;

        var dataPath = await _repo.GetAllActiveConsentsForSubjectsAsync([Subject], null, now, TenantA);
        dataPath.Select(c => c.Id).ShouldBe([data.Id]);

        var all = await _repo.GetActiveGrantsForSubjectsAsync(TenantA, [Subject], now, 100);
        all.Select(c => c.Id).ShouldBe([data.Id, unmask.Id], ignoreOrder: true);

        (await _repo.RevokeSystemConsentByIdAsync(TenantA, data.Id, SystemActor, "SCIM_DEPROVISION")).ShouldBeTrue();
        (await _repo.RevokeSystemConsentByIdAsync(TenantA, unmask.Id, SystemActor, "SCIM_DEPROVISION")).ShouldBeTrue();

        (await _repo.GetActiveGrantsForSubjectsAsync(TenantA, [Subject], now, 100)).ShouldBeEmpty();
        (await _repo.GetConsentByIdAsync(unmask.Id))!.IsRevoked.ShouldBeTrue();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task System_revoke_by_id_is_tenant_scoped()
    {
        await EnsureTableAsync();
        var data = await CreateAsync(PermissionGrantKind.Data);

        (await _repo.RevokeSystemConsentByIdAsync(TenantB, data.Id, SystemActor, "wrong tenant")).ShouldBeFalse();
        (await _repo.GetConsentByIdAsync(data.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.RevokeSystemConsentByIdAsync(TenantA, data.Id, SystemActor, "right tenant")).ShouldBeTrue();
    }
}
