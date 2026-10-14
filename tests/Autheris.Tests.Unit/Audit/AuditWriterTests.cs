using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

[Trait("Category", "Security")]
public sealed class AuditWriterTests
{
    private static AuditLogEntry Entry(string eventType = AuditEventTypes.AuditConfigChanged) => new()
    {
        EventType = eventType,
        ActorSid = new Sid("S-1-5-21-ADMIN"),
        TargetTable = "governance",
        Decision = "ALLOW",
        TraceId = "trace-1"
    };

    private static (AuditWriter Writer, IAuditLogRepository Repo) Create()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        return (new AuditWriter(repo, NullLogger<AuditWriter>.Instance), repo);
    }

    [Fact]
    public async Task RecordRequired_WhenRepositoryThrows_ThrowsAuditUnavailable()
    {
        var (writer, repo) = Create();
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        var ex = await Should.ThrowAsync<AuditUnavailableException>(() => writer.RecordRequiredAsync(Entry()));
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordRequired_WhenRepositoryThrowsAuditUnavailable_PropagatesSameException()
    {
        var (writer, repo) = Create();
        var latch = new AuditUnavailableException("latched");
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(latch);

        (await Should.ThrowAsync<AuditUnavailableException>(() => writer.RecordRequiredAsync(Entry()))).ShouldBeSameAs(latch);
    }

    [Fact]
    public async Task RecordRequired_OnCancellation_Rethrows()
    {
        var (writer, repo) = Create();
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => writer.RecordRequiredAsync(Entry()));
    }

    [Fact]
    public async Task RecordBestEffort_WhenRepositoryThrows_DoesNotThrow()
    {
        var (writer, repo) = Create();
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => writer.RecordBestEffortAsync(Entry()));
    }

    [Fact]
    public async Task Record_DispatchesByPolicyTable()
    {
        var (writer, repo) = Create();
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        await Should.ThrowAsync<AuditUnavailableException>(() => writer.RecordAsync(Entry(AuditEventTypes.AuditConfigChanged)));
        await Should.NotThrowAsync(() => writer.RecordAsync(Entry(AuditEventTypes.CatalogRead)));
    }

    [Fact]
    public async Task RebacGate_OnCancellation_Rethrows()
    {
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.IsEnabled.Returns(true);
        evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<RebacCheckResult>>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await RebacTableGate.IsAllowedAsync(evaluator, TenantId.LegacySingleTenant, new Sid("S-1"), new TableIdentifier("d", "s", "t"), CancellationToken.None));
    }

    [Fact]
    public async Task RebacGate_OnDependencyFailure_StillDenies()
    {
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.IsEnabled.Returns(true);
        evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<RebacCheckResult>>(_ => throw new InvalidOperationException("boom"));

        (await RebacTableGate.IsAllowedAsync(evaluator, TenantId.LegacySingleTenant, new Sid("S-1"), new TableIdentifier("d", "s", "t"), CancellationToken.None))
            .ShouldBeFalse();
    }
}
