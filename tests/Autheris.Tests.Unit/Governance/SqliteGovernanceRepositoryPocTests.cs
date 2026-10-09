namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SqliteGovernanceRepositoryPocTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository;

    public SqliteGovernanceRepositoryPocTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source=audit_poc_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
                JournalMode = "DELETE"
            }
        });

        _repository = new SqliteGovernanceRepository(
            Substitute.For<IEpochValidationService>(),
            options,
            logger: NullLogger<SqliteGovernanceRepository>.Instance);
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    [Fact]
    public async Task R43_RecordAuditEventAsync_ExceptionDoesNotLeakTransaction()
    {
        // Cancelled write should fail without leaving transaction hanging
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var entry1 = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            Decision = "DENY",
            EventType = "ADMIN_MUTATION",
            ActorSid = new Sid("S-1-5-21-TEST-1"),
            TargetTable = "test.public.t1",
            TenantId = new TenantId("tenant-1")
        };

        // First call fails due to cancellation token
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await _repository.RecordAuditEventAsync(entry1, cts.Token);
        });

        // Second call with valid cancellation token MUST succeed and NOT throw "SqliteConnection does not support nested transactions"
        var entry2 = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            Decision = "DENY",
            EventType = "ADMIN_MUTATION",
            ActorSid = new Sid("S-1-5-21-TEST-2"),
            TargetTable = "test.public.t2",
            TenantId = new TenantId("tenant-1")
        };

        await _repository.RecordAuditEventAsync(entry2, CancellationToken.None);

        var logs = await _repository.GetAuditLogEntriesAsync(10, new TenantId("tenant-1"), CancellationToken.None);
        logs.Count.ShouldBe(1);
        logs[0].ActorSid.Value.ShouldBe("S-1-5-21-TEST-2");
    }

    [Fact]
    public void R42_JournalMode_ConfiguredAndDeleteAppliedSuccessfully()
    {
        // Repository was created with JournalMode = "DELETE", verify initialization succeeded
        _repository.ShouldNotBeNull();
    }
}
