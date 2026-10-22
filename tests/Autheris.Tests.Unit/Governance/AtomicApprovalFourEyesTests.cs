namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>
/// Security review (four-eyes on the atomic path): <c>ExecuteAtomicApprovalWithConsentAsync</c> (final approval with row filter / column
/// masking) applies the same separation-of-duties and same-approver checks as <c>ApproveConsentRequestStepAsync</c> and only completes a
/// request whose four-eyes requirement (flag or ADR-008 sensitivity) is met by this step. Before the fix one approver could approve step 1
/// and then call the atomic path to reach APPROVED alone.
/// </summary>
public sealed class AtomicApprovalFourEyesTests
{
    private static readonly Sid Requester = new("S-1-5-21-REQUESTER");
    private static readonly Sid Approver1 = new("S-1-5-21-APPROVER-1");
    private static readonly Sid Approver2 = new("S-1-5-21-APPROVER-2");
    private static readonly TenantId Tenant = new("tenant-a");

    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private static async Task<(SqliteGovernanceRepository Repo, ConsentRequest Request)> CreateRequestAsync(string sensitivity, bool requiresFourEyes)
    {
        var repo = GovernanceStoreFactory.CreateSqlite(new StubEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "atomic_" + Guid.NewGuid().ToString("N")[..8]);
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
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            BusinessJustification = "Customer support investigation",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = "PENDING",
            TenantId = Tenant
        });

        using (var cmd = repo.RawConnection().CreateCommand())
        {
            cmd.CommandText = @"
                INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator');
                INSERT OR IGNORE INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m1, id, 'User', 'S-1-5-21-APPROVER-1' FROM ROLES WHERE role_name = 'GovernanceAdmin';
                INSERT OR IGNORE INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m2, id, 'User', 'S-1-5-21-APPROVER-2' FROM ROLES WHERE role_name = 'GovernanceAdmin';
            ";
            cmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@m1", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@m2", Guid.NewGuid().ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        return (repo, created);
    }

    private static (ApprovalStep Step, Consent Consent) BuildFinalApproval(ConsentRequest req, Sid approver, string? description = null)
    {
        var step = new ApprovalStep
        {
            Id = Guid.NewGuid(),
            ConsentRequestId = req.Id,
            StepNumber = 1, // deliberately stale: the repository assigns the step number under the lock
            ApproverSid = approver,
            Decision = "APPROVED",
            DecidedAt = DateTimeOffset.UtcNow
        };
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = req.TableId,
            TableIdentifier = req.TableIdentifier,
            ConsentRequestId = req.Id,
            TenantId = req.TenantId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(req.RequestedGranteeRef),
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = req.RequestedValidTo,
            CreatedBySid = approver,
            Description = description
        };
        return (step, consent);
    }

    private static async Task<int> CountRowsAsync(SqliteGovernanceRepository repo, string sql, Guid requestId)
    {
        using var cmd = repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", requestId.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static Task<int> CountStepsAsync(SqliteGovernanceRepository repo, Guid requestId) =>
        CountRowsAsync(repo, "SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id", requestId);

    private static Task<int> CountConsentsAsync(SqliteGovernanceRepository repo, Guid requestId) =>
        CountRowsAsync(repo, "SELECT COUNT(*) FROM CONSENTS WHERE consent_request_id = @id", requestId);

    [Theory]
    [InlineData("LOW", true)]
    [InlineData("HIGH", false)]
    [InlineData("RESTRICTED", false)]
    [InlineData("SECRET", false)]
    public async Task SameApprover_AfterFirstStep_CannotFinishFourEyesAloneViaAtomicPath(string sensitivity, bool requiresFourEyes)
    {
        var (repo, request) = await CreateRequestAsync(sensitivity, requiresFourEyes);
        using (repo)
        {
            (await repo.ApproveConsentRequestStepAsync(request.Id, Approver1)).Status.ShouldBe("PENDING_SECOND_APPROVAL");

            var (step, consent) = BuildFinalApproval(request, Approver1);
            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                repo.ExecuteAtomicApprovalWithConsentAsync(request.Id, step, consent, [], [], CancellationToken.None));
            ex.ShouldNotBeOfType<ConsentRequestStatusConflictException>();
            ex.Message.ShouldContain("Vier-Augen-Prinzip");

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
            (await CountStepsAsync(repo, request.Id)).ShouldBe(1);
            (await CountConsentsAsync(repo, request.Id)).ShouldBe(0);
        }
    }

    [Theory]
    [InlineData("LOW", true)]
    [InlineData("CONFIDENTIAL", false)]
    [InlineData("HIGH", false)]
    public async Task FirstStep_OfFourEyesRequest_IsRefusedOnAtomicPath_AndWritesNothing(string sensitivity, bool requiresFourEyes)
    {
        var (repo, request) = await CreateRequestAsync(sensitivity, requiresFourEyes);
        using (repo)
        {
            var (step, consent) = BuildFinalApproval(request, Approver1);
            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                repo.ExecuteAtomicApprovalWithConsentAsync(request.Id, step, consent, [], [], CancellationToken.None));
            ex.ShouldNotBeOfType<ConsentRequestStatusConflictException>();

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING");
            (await CountStepsAsync(repo, request.Id)).ShouldBe(0);
            (await CountConsentsAsync(repo, request.Id)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Requester_CannotApproveOwnRequestViaAtomicPath()
    {
        var (repo, request) = await CreateRequestAsync("LOW", requiresFourEyes: false);
        using (repo)
        {
            var (step, consent) = BuildFinalApproval(request, Requester);
            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                repo.ExecuteAtomicApprovalWithConsentAsync(request.Id, step, consent, [], [], CancellationToken.None));
            ex.Message.ShouldContain("Separation of duties");

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING");
            (await CountConsentsAsync(repo, request.Id)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task SecondApprover_CompletesFourEyesViaAtomicPath_WithDescriptionAndRepositoryStepNumber()
    {
        var (repo, request) = await CreateRequestAsync("HIGH", requiresFourEyes: false);
        using (repo)
        {
            (await repo.ApproveConsentRequestStepAsync(request.Id, Approver1)).Status.ShouldBe("PENDING_SECOND_APPROVAL");

            var (step, consent) = BuildFinalApproval(request, Approver2, description: "Support case 4711");
            await repo.ExecuteAtomicApprovalWithConsentAsync(request.Id, step, consent, [], [], CancellationToken.None);

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("APPROVED");
            (await CountStepsAsync(repo, request.Id)).ShouldBe(2);
            (await CountRowsAsync(repo, "SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id AND step_number = 2", request.Id)).ShouldBe(1);

            var active = await repo.GetActiveConsentsForSubjectsAsync([Requester], request.TableIdentifier, DateTimeOffset.UtcNow.AddMinutes(1), Tenant);
            active.Count(c => c.ConsentRequestId == request.Id).ShouldBe(1);
            await repo.LoadDescriptionsAsync(active.ToList());
            active.Single(c => c.ConsentRequestId == request.Id).Description.ShouldBe("Support case 4711");
        }
    }

    [Fact]
    public async Task SingleApproval_OnNonFourEyesTable_StillCompletesViaAtomicPath()
    {
        var (repo, request) = await CreateRequestAsync("LOW", requiresFourEyes: false);
        using (repo)
        {
            var (step, consent) = BuildFinalApproval(request, Approver1);
            await repo.ExecuteAtomicApprovalWithConsentAsync(request.Id, step, consent, [], [], CancellationToken.None);

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("APPROVED");
            (await CountConsentsAsync(repo, request.Id)).ShouldBe(1);
        }
    }
}
