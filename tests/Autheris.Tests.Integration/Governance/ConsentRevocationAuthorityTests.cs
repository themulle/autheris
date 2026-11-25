using System.Security.Claims;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Audit;
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
public sealed class ConsentRevocationAuthorityTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");
    private static readonly Sid Alice = new("S-1-5-21-ALICE");
    private static readonly Sid Bob = new("S-1-5-21-BOB");
    private static readonly Sid Owner = new("S-1-5-21-OWNER");

    private readonly PostgreSqlGovernanceRepository _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService());
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private Guid _tableId;

    public void Dispose() => _repo.Dispose();

    private async Task EnsureTableAsync()
    {
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = _table.Domain,
            SchemaName = _table.Schema,
            TableName = _table.TableName,
            IsActive = true
        };
        _tableId = (await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = _table,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        })).Table.Id;
    }

    private Consent UserConsent(string sid, TenantId tenant, GranteeType type = GranteeType.User) => new()
    {
        TableId = _tableId,
        TableIdentifier = _table,
        TenantId = tenant,
        GranteeType = type,
        GranteeSid = new Sid(sid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30),
        CreatedBySid = Admin
    };

    private static ClaimsPrincipal PrincipalWith(string sid, string role, string? tenantId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new(ClaimTypes.Role, role)
        };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", ClaimTypes.Name, ClaimTypes.Role));
    }

    private async Task SeedRoleMemberAsync(Sid sid, string roleName)
    {
        var conn = _repo.RawConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1;";
        cmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@mid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@role", roleName);
        cmd.Parameters.AddWithValue("@sid", sid.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Revoke_TokenGovernanceAdmin_SameTenant_Succeeds()
    {
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA));
        var authority = ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Admin.Value, "GovernanceAdmin", TenantA.Value));

        await _repo.RevokeConsentAsync(consent.Id, Admin, "admin revoke", authority);

        (await _repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_TokenGovernanceAdmin_OtherTenantConsent_ReturnsNotFound_AndKeepsConsent()
    {
        await EnsureTableAsync();
        var consentB = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantB));
        var authority = ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Admin.Value, "GovernanceAdmin", TenantA.Value));

        await Should.ThrowAsync<KeyNotFoundException>(() => _repo.RevokeConsentAsync(consentB.Id, Admin, "cross-tenant", authority));

        (await _repo.GetConsentByIdAsync(consentB.Id))!.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Revoke_TokenClusterAdmin_AnyTenant_Succeeds()
    {
        await EnsureTableAsync();
        var consentA = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA));
        var consentB = await _repo.CreateConsentAsync(UserConsent(Bob.Value, TenantB));
        var authority = ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Admin.Value, "ClusterAdmin"));

        await _repo.RevokeConsentAsync(consentA.Id, Admin, "cluster revoke A", authority);
        await _repo.RevokeConsentAsync(consentB.Id, Admin, "cluster revoke B", authority);

        (await _repo.GetConsentByIdAsync(consentA.Id))!.IsRevoked.ShouldBeTrue();
        (await _repo.GetConsentByIdAsync(consentB.Id))!.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_RoleMembersRowWithoutTokenRole_IsDenied()
    {
        await EnsureTableAsync();
        await SeedRoleMemberAsync(Bob, "GovernanceAdmin");
        var consent = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA));

        await Should.ThrowAsync<ConsentRevocationForbiddenException>(() =>
            _repo.RevokeConsentAsync(consent.Id, Bob, "stale db role", ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Bob.Value, "Consumer", TenantA.Value))));

        (await _repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Revoke_ConsumerWithoutOwnership_IsDenied_403()
    {
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA));
        var consumer = PrincipalWith(Bob.Value, "Consumer", TenantA.Value);

        var ex = await Should.ThrowAsync<ConsentRevocationForbiddenException>(() =>
            _repo.RevokeConsentAsync(consent.Id, Bob, "unauthorized", ConsentRevocationAuthority.FromPrincipal(consumer)));

        ex.Message.ShouldNotContain(Bob.Value);
        ex.Message.ShouldNotContain(_table.TableName);
        var audits = await _repo.GetAuditLogEntriesAsync(50);
        audits.ShouldContain(a => a.EventType == AuditEventTypes.ConsentDenied && a.Decision == "DENY" && a.ActorSid == Bob);
        (await _repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Revoke_ByNonGranteeToken_IsDenied_OwnersRevokeThroughVerifiedAuthority()
    {
        // #1009 W3-12b (D13): the store has no per-table owner fallback any more. A container owner revokes through the grant revoke
        // authority, which hands the store a verified tenant authority; a plain token of a non-admin, non-grantee caller is refused.
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA));

        await Should.ThrowAsync<Exception>(() => _repo.RevokeConsentAsync(
            consent.Id, Owner, "owner revoke", ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Owner.Value, "Consumer", TenantA.Value))));
        (await _repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeFalse();

        await _repo.RevokeConsentAsync(consent.Id, Owner, "owner revoke", ConsentRevocationAuthority.ForVerifiedRevocation(TenantA, "ContainerOwner"));
        (await _repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_GranteeSelf_Succeeds()
    {
        await EnsureTableAsync();
        var userConsent = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA, GranteeType.User));
        var spConsent = await _repo.CreateConsentAsync(UserConsent(Bob.Value, TenantA, GranteeType.ServicePrincipal));

        await _repo.RevokeConsentAsync(userConsent.Id, Alice, "self revoke user", ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Alice.Value, "Consumer", TenantA.Value)));
        await _repo.RevokeConsentAsync(spConsent.Id, Bob, "self revoke sp", ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Bob.Value, "Consumer", TenantA.Value)));

        (await _repo.GetConsentByIdAsync(userConsent.Id))!.IsRevoked.ShouldBeTrue();
        (await _repo.GetConsentByIdAsync(spConsent.Id))!.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Replace_ViaApply_TokenGovernanceAdmin_Succeeds()
    {
        await EnsureTableAsync();
        var old = await _repo.CreateConsentAsync(new Consent
        {
            TableId = _tableId,
            TableIdentifier = _table,
            TenantId = TenantA,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid("S-1-5-21-GROUP-1"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            CreatedBySid = Admin
        });
        var svc = new DeclarativeConsentApplyService(_repo, _repo, _repo);
        var caller = PrincipalWith(Admin.Value, "GovernanceAdmin", TenantA.Value);
        var manifest = new DeclarativeConsentManifest("2",
        [
            new DeclarativeConsentPolicy(_table.Domain, _table.Schema, _table.TableName, "Group", "S-1-5-21-GROUP-1", "Deny", Description: "integration test grant", ValidUntil: DateTimeOffset.UtcNow.AddDays(30))
        ]);

        var result = await svc.ApplyManifestAsync(caller, manifest);

        result.Applied.ShouldBe(1);
        (await _repo.GetConsentByIdAsync(old.Id))!.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Replace_PartialDenial_RollsBackWholeManifest()
    {
        await EnsureTableAsync();
        var owned = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA));
        var other = await _repo.CreateConsentAsync(UserConsent(Bob.Value, TenantA));
        var replacement = UserConsent("S-1-5-21-CARL", TenantA);

        await Should.ThrowAsync<ConsentRevocationForbiddenException>(() =>
            _repo.ReplaceConsentsAsync([owned.Id, other.Id], [replacement], Alice, "partial", ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Alice.Value, "Consumer", TenantA.Value))));

        (await _repo.GetConsentByIdAsync(owned.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetConsentByIdAsync(other.Id))!.IsRevoked.ShouldBeFalse();
        (await _repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-CARL")], _table, DateTimeOffset.UtcNow, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Revoke_ForeignTenantConsent_ForbiddenAndUnknownLookLikeEachOther()
    {
        await EnsureTableAsync();
        var consentB = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantB));
        var authority = ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Admin.Value, "GovernanceAdmin", TenantA.Value));

        var foreign = await Should.ThrowAsync<KeyNotFoundException>(() => _repo.RevokeConsentAsync(consentB.Id, Admin, "probe", authority));
        var missingId = Guid.NewGuid();
        var unknown = await Should.ThrowAsync<KeyNotFoundException>(() => _repo.RevokeConsentAsync(missingId, Admin, "probe", authority));

        foreign.Message.Replace(consentB.Id.ToString(), "<id>").ShouldBe(unknown.Message.Replace(missingId.ToString(), "<id>"));
    }

    [Fact]
    public async Task Revoke_DenialMessage_ContainsNoSidOrTable()
    {
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent(Alice.Value, TenantA));

        var ex = await Should.ThrowAsync<ConsentRevocationForbiddenException>(() =>
            _repo.RevokeConsentAsync(consent.Id, Bob, "denied", ConsentRevocationAuthority.FromPrincipal(PrincipalWith(Bob.Value, "Consumer", TenantA.Value))));

        ex.Message.ShouldNotContain(Bob.Value);
        ex.Message.ShouldNotContain(_table.TableName);
        ex.Message.ShouldNotContain(_table.Domain);
    }

    [Fact]
    public async Task Approve_RoleMembersRowWithoutOwnership_IsDenied()
    {
        await EnsureTableAsync();
        await SeedRoleMemberAsync(Bob, "GovernanceAdmin");
        var request = await _repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TenantId = TenantA,
            TableId = _tableId,
            TableIdentifier = _table,
            RequesterSid = Alice,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Alice.Value,
            BusinessJustification = "role row is no ownership",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Without a container owner group nobody decides at the store level (#1009 D13).
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _repo.ApproveConsentRequestStepAsync(request.Id, Bob));
    }
}
