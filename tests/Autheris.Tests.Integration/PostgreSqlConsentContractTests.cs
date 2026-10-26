namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Review PG-9/PG-12: contract tests for the PostgreSQL consent workflow. Need Docker; without it they return early.</summary>
public sealed class PostgreSqlConsentContractTests : IAsyncLifetime
{
    private PostgreSqlTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            _available = true;
        }
        catch (Exception ex)
        {
            _available = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgreSqlGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = _db!.ConnectionString,
                EnableOutboxProcessor = false,
                SeedDemoData = false
            }
        });
        return GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
    }

    [Fact]
    public async Task RevokeConsent_UnknownConsent_ThrowsKeyNotFound()
    {
        if (!_available) return;

        await using var repo = NewRepository();

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            repo.RevokeConsentAsync(Guid.NewGuid(), new Sid("S-1-5-21-ADMIN"), "test"));
    }

    [Fact]
    public async Task Schema_HasUniqueTicketIndexPerTenant()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_consent_requests_ticket'", conn);

        var def = (string?)await cmd.ExecuteScalarAsync();

        def.ShouldNotBeNull();
        def.ShouldContain("UNIQUE");
    }

    [Fact]
    public async Task ApproveConsentRequest_HighSensitivityTable_EnforcesFourEyesWhenRequiresFourEyesIsFalse()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        var tableId = new TableIdentifier("sales", "crm", "pg_fe_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            Sensitivity = "HIGH",
            RequiresFourEyes = false,
            IsActive = true
        };

        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
        });

        var request = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-REQ"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-REQ",
            BusinessJustification = "Support",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = "PENDING",
            TenantId = new TenantId("tenant-pg")
        });

        var approver1 = new Sid("S-1-5-21-APP1");
        var approver2 = new Sid("S-1-5-21-APP2");

        await using (var conn = new NpgsqlConnection(_db!.ConnectionString))
        {
            await conn.OpenAsync();
            await using var roleCmd = new NpgsqlCommand(@"
                INSERT INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator')
                ON CONFLICT (role_name) DO NOTHING;
                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m1, id, 'User', 'S-1-5-21-APP1' FROM ROLES WHERE role_name = 'GovernanceAdmin';
                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m2, id, 'User', 'S-1-5-21-APP2' FROM ROLES WHERE role_name = 'GovernanceAdmin';
            ", conn);
            roleCmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
            roleCmd.Parameters.AddWithValue("@m1", Guid.NewGuid().ToString());
            roleCmd.Parameters.AddWithValue("@m2", Guid.NewGuid().ToString());
            await roleCmd.ExecuteNonQueryAsync();
        }

        var step1 = await repo.ApproveConsentRequestStepAsync(request.Id, approver1);
        step1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            repo.ApproveConsentRequestStepAsync(request.Id, approver1));

        var step2 = await repo.ApproveConsentRequestStepAsync(request.Id, approver2);
        step2.Status.ShouldBe("APPROVED");
    }

    private static async Task<(TableIdentifier Table, Guid TableId)> CreateRevokeTableAsync(PostgreSqlGovernanceRepository repo)
    {
        var tableId = new TableIdentifier("sales", "crm", "pg_rv_" + Guid.NewGuid().ToString("N")[..8]);
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
        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        return (tableId, meta.Table.Id);
    }

    private static Consent UserConsent(TableIdentifier table, Guid tableId, string sid, string tenant) => new()
    {
        TableId = tableId,
        TableIdentifier = table,
        TenantId = new TenantId(tenant),
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(sid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30),
        CreatedBySid = new Sid("S-1-5-21-ADMIN")
    };

    /// <summary>ARCH-MNT-22-T-04: a non-grantee without approver rights cannot revoke by id and nothing changes.</summary>
    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_Unauthorized_NonGrantee_Throws_And_Writes_No_Change()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        var (table, tableId) = await CreateRevokeTableAsync(repo);
        var consent = await repo.CreateConsentAsync(UserConsent(table, tableId, "S-1-5-21-ALICE", "tenant-a"));

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            repo.RevokeConsentAsync(consent.Id, new Sid("S-1-5-21-MALLORY"), "nope"));
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            repo.ReplaceConsentsAsync([consent.Id], [], new Sid("S-1-5-21-MALLORY"), "nope"));

        (await repo.GetConsentByIdAsync(consent.Id))!.IsRevoked.ShouldBeFalse();
    }

    /// <summary>ARCH-MNT-22-T-05: revoke by id (the row-locked tenant filter exemption) revokes exactly the addressed consent, never another tenant's.</summary>
    [Trait("Category", "Security")]
    [Fact]
    public async Task Revoke_ById_Cannot_Revoke_Other_Tenant_Consent()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        var (table, tableId) = await CreateRevokeTableAsync(repo);
        var alice = new Sid("S-1-5-21-ALICE");
        var mine = await repo.CreateConsentAsync(UserConsent(table, tableId, alice.Value, "tenant-a"));
        var foreign = await repo.CreateConsentAsync(UserConsent(table, tableId, alice.Value, "tenant-b"));

        await repo.ReplaceConsentsAsync([mine.Id], [], alice, "self revoke of tenant-a only");

        (await repo.GetConsentByIdAsync(mine.Id))!.IsRevoked.ShouldBeTrue();
        (await repo.GetConsentByIdAsync(foreign.Id))!.IsRevoked.ShouldBeFalse();
    }
}
