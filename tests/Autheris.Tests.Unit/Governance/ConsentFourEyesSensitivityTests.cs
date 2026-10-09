namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>
/// POL-2 / D-4: Repository tests for SQLite (and ConsentApprovalPolicy contract)
/// ensuring HIGH, RESTRICTED, SECRET and other high sensitivities mandate four-eyes approval
/// even when requires_four_eyes = false.
/// </summary>
public sealed class ConsentFourEyesSensitivityTests
{
    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private static async Task<(SqliteGovernanceRepository repo, ConsentRequest request)> CreateRequestForTableAsync(
        string sensitivity,
        bool requiresFourEyes = false)
    {
        var repo = new SqliteGovernanceRepository(new StubEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "cust_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            Sensitivity = sensitivity,
            RequiresFourEyes = requiresFourEyes,
            IsActive = true
        };

        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
        });

        var created = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-REQUESTER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-REQUESTER",
            BusinessJustification = "Customer support investigation",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = "PENDING",
            TenantId = new TenantId("tenant-a")
        });

        using (var cmd = repo.Connection.CreateCommand())
        {
            var adminRoleId = Guid.NewGuid().ToString();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator');
                INSERT OR IGNORE INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m1, id, 'User', 'S-1-5-21-APPROVER-1' FROM ROLES WHERE role_name = 'GovernanceAdmin';
                INSERT OR IGNORE INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m2, id, 'User', 'S-1-5-21-APPROVER-2' FROM ROLES WHERE role_name = 'GovernanceAdmin';
            ";
            cmd.Parameters.AddWithValue("@rid", adminRoleId);
            cmd.Parameters.AddWithValue("@m1", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@m2", Guid.NewGuid().ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        return (repo, created);
    }

    [Theory]
    [InlineData("HIGH")]
    [InlineData("high")]
    [InlineData("RESTRICTED")]
    [InlineData("restricted")]
    [InlineData("SECRET")]
    [InlineData("secret")]
    [InlineData("CONFIDENTIAL")]
    [InlineData("PII")] // Unknown rank >= 3 (fail closed)
    public async Task Sqlite_HighSensitivityTable_RequiresFourEyesEvenIfRequiresFourEyesIsFalse(string sensitivity)
    {
        var (repo, request) = await CreateRequestForTableAsync(sensitivity, requiresFourEyes: false);
        using (repo)
        {
            var approver1 = new Sid("S-1-5-21-APPROVER-1");
            var approver2 = new Sid("S-1-5-21-APPROVER-2");

            // Step 1: Approver 1 approves
            var step1Result = await repo.ApproveConsentRequestStepAsync(request.Id, approver1);
            step1Result.Status.ShouldBe("PENDING_SECOND_APPROVAL");

            // Re-approval by Approver 1 must fail due to four-eyes principle
            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                repo.ApproveConsentRequestStepAsync(request.Id, approver1));
            ex.Message.ShouldContain("Vier-Augen-Prinzip");

            // Step 2: Approver 2 approves
            var step2Result = await repo.ApproveConsentRequestStepAsync(request.Id, approver2);
            step2Result.Status.ShouldBe("APPROVED");
        }
    }

    [Theory]
    [InlineData("PUBLIC")]
    [InlineData("LOW")]
    [InlineData("low")]
    [InlineData("INTERNAL")]
    [InlineData("NORMAL")]
    [InlineData("MEDIUM")]
    public async Task Sqlite_LowOrNormalSensitivityTable_SingleApprovalSufficesWhenRequiresFourEyesIsFalse(string sensitivity)
    {
        var (repo, request) = await CreateRequestForTableAsync(sensitivity, requiresFourEyes: false);
        using (repo)
        {
            var approver1 = new Sid("S-1-5-21-APPROVER-1");

            // Step 1: Approver 1 approves -> directly approved because sensitivity is not high and requires_four_eyes = false
            var step1Result = await repo.ApproveConsentRequestStepAsync(request.Id, approver1);
            step1Result.Status.ShouldBe("APPROVED");
        }
    }

    [Fact]
    public async Task Sqlite_LowSensitivityTable_WithExplicitRequiresFourEyes_EnforcesSecondApproval()
    {
        var (repo, request) = await CreateRequestForTableAsync("LOW", requiresFourEyes: true);
        using (repo)
        {
            var approver1 = new Sid("S-1-5-21-APPROVER-1");
            var approver2 = new Sid("S-1-5-21-APPROVER-2");

            var step1 = await repo.ApproveConsentRequestStepAsync(request.Id, approver1);
            step1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

            var step2 = await repo.ApproveConsentRequestStepAsync(request.Id, approver2);

            step2.Status.ShouldBe("APPROVED");
        }
    }

    [Theory]
    [InlineData(false, 1, false, "HIGH", "PENDING_SECOND_APPROVAL")]
    [InlineData(false, 2, false, "HIGH", "APPROVED")]
    [InlineData(false, 1, true, "HIGH", "PENDING_EXTERNAL_APPROVAL")]
    [InlineData(false, 2, true, "HIGH", "APPROVED")]
    [InlineData(false, 1, false, "PUBLIC", "APPROVED")]
    [InlineData(false, 1, false, "LOW", "APPROVED")]
    [InlineData(false, 1, false, "NORMAL", "APPROVED")]
    [InlineData(false, 1, false, "MEDIUM", "APPROVED")]
    [InlineData(true, 1, false, "LOW", "PENDING_SECOND_APPROVAL")]
    [InlineData(true, 2, false, "LOW", "APPROVED")]
    [InlineData(true, 1, true, "LOW", "PENDING_EXTERNAL_APPROVAL")]
    [InlineData(true, 2, true, "LOW", "APPROVED")]
    public void ConsentApprovalPolicy_StatusAfterApproval_MatrixMatchesExpectations(
        bool requiresFourEyes,
        int stepNumber,
        bool isExternalItsm,
        string sensitivity,
        string expectedStatus)
    {
        var status = ConsentApprovalPolicy.StatusAfterApproval(requiresFourEyes, stepNumber, isExternalItsm, sensitivity);
        status.ShouldBe(expectedStatus);
    }
}
