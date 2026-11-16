namespace Autheris.Tests.Integration.Governance;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>
/// POL-2 / D-4: Repository tests for PostgreSQL (and ConsentApprovalPolicy contract)
/// ensuring HIGH, RESTRICTED, SECRET and other high sensitivities mandate four-eyes approval
/// even when requires_four_eyes = false.
/// </summary>
public sealed class ConsentFourEyesSensitivityTests
{
    private static async Task<(PostgreSqlGovernanceRepository repo, ConsentRequest request)> CreateRequestForTableAsync(
        string sensitivity,
        bool requiresFourEyes = false)
    {
        var repo = GovernanceTestDatabase.CreateRepository(new FakeEpochValidationService());
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

        using (var cmd = repo.RawConnection().CreateCommand())
        {
            var adminRoleId = Guid.NewGuid().ToString();
            cmd.CommandText = @"
                INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                SELECT @o1, 'S-1-5-21-APPROVER-1', 'CORP\\S-1-5-21-APPROVER-1', 'Owner 1', 'S-1-5-21-APPROVER-1@corp.example', 1
                WHERE NOT EXISTS (SELECT 1 FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-APPROVER-1');
                INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                SELECT @to1, @tid, id, 'PRIMARY' FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-APPROVER-1';
                INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                SELECT @o2, 'S-1-5-21-APPROVER-2', 'CORP\\S-1-5-21-APPROVER-2', 'Owner 2', 'S-1-5-21-APPROVER-2@corp.example', 1
                WHERE NOT EXISTS (SELECT 1 FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-APPROVER-2');
                INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                SELECT @to2, @tid, id, 'PRIMARY' FROM DATA_OWNERS WHERE ad_sid = 'S-1-5-21-APPROVER-2';
            ";
            cmd.Parameters.AddWithValue("@o1", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@to1", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@o2", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@to2", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@tid", meta.Table.Id.ToString());
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
    public async Task PostgreSql_HighSensitivityTable_RequiresFourEyesEvenIfRequiresFourEyesIsFalse(string sensitivity)
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
            ex.Message.ShouldContain("FOUR_EYES_SAME_APPROVER");

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
    public async Task PostgreSql_LowOrNormalSensitivityTable_SingleApprovalSufficesWhenRequiresFourEyesIsFalse(string sensitivity)
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
    public async Task PostgreSql_LowSensitivityTable_WithExplicitRequiresFourEyes_EnforcesSecondApproval()
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
        var status = ConsentApprovalPolicy.StatusAfterApproval(
            requiresFourEyes,
            stepNumber,
            isExternalItsm ? ConsentWorkflowMode.External : ConsentWorkflowMode.Internal,
            sensitivity);
        status.ShouldBe(expectedStatus);
    }
}
