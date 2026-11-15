using System.Security.Claims;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class DeclarativeConsentPruneSecurityTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");
    private static readonly Sid GroupA = new("S-1-5-21-GROUP-A");

    private readonly PostgreSqlGovernanceRepository _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService());
    private readonly TableIdentifier _tableKeep = new("Sales", "Crm", "Keep_" + Guid.NewGuid().ToString("N")[..6]);
    private readonly TableIdentifier _tablePrune = new("Sales", "Crm", "Prune_" + Guid.NewGuid().ToString("N")[..6]);
    private readonly TableIdentifier _tableReq = new("Sales", "Crm", "Req_" + Guid.NewGuid().ToString("N")[..6]);

    public void Dispose() => _repo.Dispose();

    private async Task<Guid> EnsureTableAsync(TableIdentifier tableId)
    {
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            Sensitivity = "LOW",
            RequiresFourEyes = false,
            IsActive = true
        };
        return (await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        })).Table.Id;
    }

    private Consent MakeConsent(Guid tableId, TableIdentifier identifier, ConsentGrantSource source) => new()
    {
        TableId = tableId,
        TableIdentifier = identifier,
        TenantId = TenantA,
        GranteeType = GranteeType.Group,
        GranteeSid = GroupA,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30),
        CreatedBySid = Admin,
        GrantSource = source
    };

    private static ClaimsPrincipal TenantAdminPrincipal() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, Admin.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", TenantA.Value)
        ],
        "Bearer"));

    [Fact]
    public async Task Prune_LegacyNullAndUnknownGrantSource_AreNeverRevoked()
    {
        var keepId = await EnsureTableAsync(_tableKeep);
        var pruneId = await EnsureTableAsync(_tablePrune);
        var reqId = await EnsureTableAsync(_tableReq);

        var keepConsent = await _repo.CreateConsentAsync(MakeConsent(keepId, _tableKeep, ConsentGrantSource.Declarative));
        var pruneConsent = await _repo.CreateConsentAsync(MakeConsent(pruneId, _tablePrune, ConsentGrantSource.Declarative));
        var reqConsent = await _repo.CreateConsentAsync(MakeConsent(reqId, _tableReq, ConsentGrantSource.ConsentRequest));
        var unknownConsent = await _repo.CreateConsentAsync(MakeConsent(reqId, _tableReq, ConsentGrantSource.Owner));

        await using (var cmd = _repo.RawConnection().CreateCommand())
        {
            cmd.CommandText = "UPDATE CONSENTS SET grant_source = 'UNKNOWN_LEGACY' WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", unknownConsent.Id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var svc = new DeclarativeConsentApplyService(_repo, _repo, _repo);
        var manifest = new DeclarativeConsentManifest("1",
        [
            new DeclarativeConsentPolicy(_tableKeep.Domain, _tableKeep.Schema, _tableKeep.TableName, "Group", GroupA.Value, "Allow", ValidUntil: DateTimeOffset.UtcNow.AddDays(30))
        ]);

        var result = await svc.ApplyManifestAsync(TenantAdminPrincipal(), manifest, pruneMode: DeclarativePruneMode.True);

        result.PrunedCount.ShouldBe(1);
        (await _repo.GetConsentByIdAsync(pruneConsent.Id))!.IsRevoked.ShouldBeTrue();
        (await _repo.GetConsentByIdAsync(pruneConsent.Id))!.RevokeReason.ShouldBe("PRUNED_BY_MANIFEST");
        (await _repo.GetConsentByIdAsync(keepConsent.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetConsentByIdAsync(reqConsent.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetConsentByIdAsync(unknownConsent.Id))!.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task ReplaceConsentsAsync_RejectsPruneOfNonDeclarativeConsentInTransaction()
    {
        var reqId = await EnsureTableAsync(_tableReq);
        var reqConsent = await _repo.CreateConsentAsync(MakeConsent(reqId, _tableReq, ConsentGrantSource.ConsentRequest));
        var authority = ConsentRevocationAuthority.FromPrincipal(TenantAdminPrincipal());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ReplaceConsentsAsync(
                [reqConsent.Id],
                [],
                Admin,
                "superseded",
                authority,
                null,
                new HashSet<Guid> { reqConsent.Id },
                "PRUNED_BY_MANIFEST"));

        (await _repo.GetConsentByIdAsync(reqConsent.Id))!.IsRevoked.ShouldBeFalse();
    }
}
