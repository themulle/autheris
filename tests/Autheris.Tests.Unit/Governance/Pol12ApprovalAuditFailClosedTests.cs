namespace Autheris.Tests.Unit.Governance;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

public sealed class Pol12ApprovalAuditFailClosedTests
{
    [Fact]
    public async Task ApproveConsentRequestStepAsync_WhenAuditFails_RollsBackApprovalStep()
    {
        // POL-12: Audit of approval steps must be atomic / before commit and fail closed.
        // If audit recording fails, the approval step and request status update must NOT be committed to the database.
        var eventBus = new InProcessChannelEventBus();
        var epochService = new EpochValidationService(eventBus: eventBus);
        var repo = new SqliteGovernanceRepository(epochService);

        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var approver = new Sid("S-1-5-21-DATAOWNER-1");
        var requester = new Sid("S-1-5-21-REQUESTER-1");

        var req = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = table,
            TenantId = new TenantId("tenant-a"),
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(30),
            Status = "PENDING"
        });

        // Break the audit table to simulate an unrecoverable audit write failure
        using (var cmd = repo.Connection.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE AUDIT_LOG_ENTRIES;";
            cmd.ExecuteNonQuery();
        }

        // Act & Assert: approval must throw due to audit failure
        await Should.ThrowAsync<Exception>(async () =>
        {
            await repo.ApproveConsentRequestStepAsync(req.Id, approver);
        });

        // Verify that the approval was rolled back and is NOT committed in the database
        using (var verifyCmd = repo.Connection.CreateCommand())
        {
            verifyCmd.CommandText = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id";
            verifyCmd.Parameters.AddWithValue("@id", req.Id.ToString());
            var status = (string)(await verifyCmd.ExecuteScalarAsync())!;

            // RED PHASE: If tx committed before audit, status would be 'APPROVED' even though audit failed!
            // GREEN PHASE: Must be 'PENDING' because tx was rolled back.
            status.ShouldBe("PENDING");
        }
    }
}
