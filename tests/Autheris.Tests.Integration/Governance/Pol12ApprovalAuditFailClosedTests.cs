namespace Autheris.Tests.Integration.Governance;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

public sealed class Pol12ApprovalAuditFailClosedTests
{
    [Fact]
    public async Task RejectConsentRequestAsync_WhenAuditFails_RollsBackDecisionStep()
    {
        // POL-12: Audit of decision steps must be atomic / before commit and fail closed.
        // If audit recording fails, the decision step and request status update must NOT be committed to the database.
        // Approvals run through the permission request engine (issue #1132), whose fail-closed audit is covered by its own tests.
        var eventBus = new InProcessChannelEventBus();
        var epochService = new EpochValidationService(eventBus: eventBus);
        var repo = GovernanceTestDatabase.CreateRepository(epochService);

        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var approver = new Sid("S-1-5-21-1000-1000-1000-1001");
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

        // The decider is the owner group of the table's container (#1009 D13), so only the audit failure can stop the decision.
        await Autheris.Tests.Integration.Governance.ContainerOwnerSeed.SetOwnerAsync(repo.RawConnection(), "tenant-a", meta.Table.Id, approver.Value);

        // Break the audit table to simulate an unrecoverable audit write failure
        using (var cmd = repo.RawConnection().CreateCommand())
        {
            cmd.CommandText = "DROP TABLE AUDIT_LOG_ENTRIES CASCADE;";
            cmd.ExecuteNonQuery();
        }

        // Act & Assert: the decision must throw due to audit failure
        await Should.ThrowAsync<Exception>(async () =>
        {
            await repo.RejectConsentRequestAsync(req.Id, approver, "audit failure");
        });

        // Verify that the decision was rolled back and is NOT committed in the database
        using (var verifyCmd = repo.RawConnection().CreateCommand())
        {
            verifyCmd.CommandText = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id";
            verifyCmd.Parameters.AddWithValue("@id", req.Id.ToString());
            var status = (string)(await verifyCmd.ExecuteScalarAsync())!;

            // RED PHASE: If tx committed before audit, status would be 'REJECTED' even though audit failed!
            // GREEN PHASE: Must be 'PENDING' because tx was rolled back.
            status.ShouldBe("PENDING");
        }
    }
}
