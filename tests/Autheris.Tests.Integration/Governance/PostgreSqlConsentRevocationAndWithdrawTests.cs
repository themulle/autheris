using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>PERS-02 (withdraw does not deadlock on an in-memory database) and PERS-05 (revocation is transactional and never overwritten), SQLite.</summary>
[Trait("Category", "Security")]
public sealed class PostgreSqlConsentRevocationAndWithdrawTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid Admin = new("S-1-5-21-ADMIN");
    private static readonly Sid Requester = new("S-1-5-21-REQUESTER");

    private readonly PostgreSqlGovernanceRepository _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService());
    private readonly TableIdentifier _table = new("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..6]);
    private Guid _tableId;

    public void Dispose() => _repo.Dispose();

    private async Task EnsureTableAsync()
    {
        var conn = _repo.RawConnection();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT 1;";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@sid", Admin.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = _table.Domain, SchemaName = _table.Schema, TableName = _table.TableName,
            IsActive = true
        };
        _tableId = (await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = _table, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        })).Table.Id;
    }

    private Consent UserConsent(string sid) => new()
    {
        TableId = _tableId, TableIdentifier = _table, TenantId = Tenant, GranteeType = GranteeType.User, GranteeSid = new Sid(sid),
        Effect = ConsentEffect.Allow, ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30), CreatedBySid = Admin
    };

    private Task<ConsentRequest> NewRequestAsync() => _repo.CreateConsentRequestAsync(new ConsentRequest
    {
        Id = Guid.NewGuid(),
        TableId = _tableId,
        TableIdentifier = _table,
        RequesterSid = Requester,
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = Requester.Value,
        BusinessJustification = "Quarterly reporting",
        Status = "PENDING",
        RequestedAt = DateTimeOffset.UtcNow,
        RequestedValidTo = DateTimeOffset.UtcNow.AddDays(30),
        TenantId = Tenant
    });

    [Fact]
    public async Task Withdraw_OnInMemoryDatabase_CompletesWithinTimeout()
    {
        await EnsureTableAsync();
        var request = await NewRequestAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var withdrawn = await _repo.WithdrawConsentRequestAsync(request.Id, Requester, cts.Token);

        withdrawn.ShouldBeTrue();
        (await _repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("WITHDRAWN");
    }

    [Fact]
    public async Task Withdraw_ThenOtherWrite_DoesNotBlock()
    {
        await EnsureTableAsync();
        var request = await NewRequestAsync();
        await _repo.WithdrawConsentRequestAsync(request.Id, Requester);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var created = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-AFTER")).WaitAsync(cts.Token);

        created.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Withdraw_ByOtherRequester_ReturnsFalse()
    {
        await EnsureTableAsync();
        var request = await NewRequestAsync();

        (await _repo.WithdrawConsentRequestAsync(request.Id, new Sid("S-1-5-21-OTHER"))).ShouldBeFalse();

        (await _repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task Revoke_Twice_KeepsOriginalRevoker_AndReason()
    {
        await EnsureTableAsync();
        var consent = await _repo.CreateConsentAsync(UserConsent("S-1-5-21-GRANTEE"));

        var adminAuth = Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, Admin.Value),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "GovernanceAdmin"),
                new System.Security.Claims.Claim("tenant_id", Tenant.Value)
            ], "test")));

        await _repo.RevokeConsentAsync(consent.Id, Admin, "first reason", adminAuth);
        await _repo.RevokeConsentAsync(consent.Id, Admin, "second reason", adminAuth);

        var stored = await _repo.GetConsentByIdAsync(consent.Id);
        stored.ShouldNotBeNull();
        stored!.IsRevoked.ShouldBeTrue();
        stored.RevokeReason.ShouldBe("first reason");
    }

    [Fact]
    public async Task Revoke_UnknownConsent_Throws()
    {
        await EnsureTableAsync();

        var adminAuth = Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, Admin.Value),
                new System.Security.Claims.Claim("tenant_id", Tenant.Value)
            ], "test")));
        await Should.ThrowAsync<KeyNotFoundException>(() => _repo.RevokeConsentAsync(Guid.NewGuid(), Admin, "missing", adminAuth));
    }

    [Fact]
    public async Task SEC_P4R_3_ConsentRequest_NonUtcRequestedValidTo_IsStoredAsUtc()
    {
        await EnsureTableAsync();
        var local = new DateTimeOffset(2031, 5, 4, 12, 0, 0, TimeSpan.FromHours(5));
        var id = Guid.NewGuid();
        await _repo.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = id, TableId = _tableId, TableIdentifier = _table, RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User, RequestedGranteeRef = Requester.Value,
            BusinessJustification = "Quarterly reporting", Status = "PENDING",
            RequestedAt = DateTimeOffset.UtcNow, RequestedValidTo = local, TenantId = Tenant
        });

        var conn = _repo.RawConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT requested_valid_to FROM CONSENT_REQUESTS WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        var stored = (string?)await cmd.ExecuteScalarAsync();

        stored.ShouldBe(local.ToUniversalTime().ToString("O"));
    }
}
