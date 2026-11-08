namespace Autheris.Tests.Integration.Governance;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>An anchor store shared by two replicas never regresses when a replica commits a caller-managed transaction (PostgreSQL).</summary>
[Trait("Category", "Security")]
public sealed class AnchorStoreMonotonicityPostgreSqlTests
{
    [Fact]
    public async Task PostgreSql_OnTransactionCommitted_DoesNotRegressAnchor()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        var shared = new InMemoryAuditChainAnchorStore();

        using var replicaA = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out var csA, environment: env, auditAnchorStore: shared);
        using var replicaB = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), environment: env, auditAnchorStore: shared);
        for (var i = 1; i <= 5; i++)
        {
            await replicaB.RecordAuditEventAsync(NewEntry(i));
        }

        shared.Load()!.Sequence.ShouldBe(5);

        // Replica A commits a caller-managed transaction whose audit entry has sequence 1: that must not pull the shared anchor back.
        await using var conn = new NpgsqlConnection(csA);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await replicaA.RecordAuditEventInternalAsync(NewEntry(99), CancellationToken.None, tx);
        await tx.CommitAsync();
        replicaA.OnTransactionCommitted(tx);

        shared.Load()!.Sequence.ShouldBe(5);
    }

    private static AuditLogEntry NewEntry(int i) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        EventType = $"EVENT_{i}",
        ActorSid = new Sid($"S-1-5-21-ACTOR-{i}"),
        TargetTable = "hr.dbo.salaries",
        Decision = "ALLOW",
        TraceId = $"trace-{i}",
        DetailsJson = "{}"
    };
}
