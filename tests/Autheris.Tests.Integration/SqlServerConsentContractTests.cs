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
using Microsoft.Data.SqlClient;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Review PG-9/PG-12: contract tests for the SQL Server consent workflow. Need Docker; without it they return early.</summary>
public sealed class SqlServerConsentContractTests : IAsyncLifetime
{
    private SqlServerTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        _available = _db.IsAvailable;
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private SqlServerGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "SqlServer",
                ConnectionString = _db!.ConnectionString,
                EnableOutboxProcessor = false,
                SeedDemoData = false
            }
        });
        return new SqlServerGovernanceRepository(Substitute.For<IEpochValidationService>(), options, env);
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
        await using var conn = new SqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT is_unique FROM sys.indexes WHERE name = 'ux_consent_requests_ticket' AND object_id = OBJECT_ID('dbo.CONSENT_REQUESTS')", conn);

        var isUnique = await cmd.ExecuteScalarAsync();

        isUnique.ShouldNotBeNull();
        Convert.ToBoolean(isUnique).ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveConsentRequest_HighSensitivityTable_EnforcesFourEyesWhenRequiresFourEyesIsFalse()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        var tableId = new TableIdentifier("sales", "crm", "ms_fe_" + Guid.NewGuid().ToString("N")[..8]);
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
            TenantId = new TenantId("tenant-mssql")
        });

        var approver1 = new Sid("S-1-5-21-APP1");
        var approver2 = new Sid("S-1-5-21-APP2");

        await using (var conn = new SqlConnection(_db!.ConnectionString))
        {
            await conn.OpenAsync();
            await using var roleCmd = new SqlCommand(@"
                INSERT INTO ROLES (id, role_name, description)
                SELECT @rid, 'GovernanceAdmin', 'Governance Administrator'
                WHERE NOT EXISTS (SELECT 1 FROM ROLES WHERE role_name = 'GovernanceAdmin');
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
}

