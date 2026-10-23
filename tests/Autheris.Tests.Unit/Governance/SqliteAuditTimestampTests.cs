using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>PERS-01: audit timestamps are stored and compared as UTC strings, whatever offset the caller uses (SQLite).</summary>
[Trait("Category", "Security")]
public sealed class SqliteAuditTimestampTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-a");
    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());

    public void Dispose() => _repo.Dispose();

    private static AuditLogEntry Entry(string table, DateTimeOffset occurredAt) => new()
    {
        TenantId = Tenant,
        OccurredAt = occurredAt,
        EventType = "TEST_EVENT",
        ActorSid = new Sid("S-1-5-21-AUDITOR"),
        TargetTable = table,
        Decision = "ALLOW",
        TraceId = Guid.NewGuid().ToString("N"),
        DetailsJson = "{}"
    };

    [Fact]
    public async Task AuditPage_SinceWithPositiveOffset_IncludesLaterUtcEntries()
    {
        var table = "audit_" + Guid.NewGuid().ToString("N")[..8];
        // 08:00Z (written with a +02:00 offset) and 11:00Z (written as UTC).
        await _repo.RecordAuditEventAsync(Entry(table, new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(2))));
        await _repo.RecordAuditEventAsync(Entry(table, new DateTimeOffset(2026, 10, 22, 11, 0, 0, TimeSpan.Zero)));
        // 09:00Z expressed with a +05:00 offset.
        var since = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(5));

        var result = await _repo.QueryAuditLogsAsync(targetTable: table, since: since);

        result.Count.ShouldBe(1);
        result[0].OccurredAt.ToUniversalTime().Hour.ShouldBe(11);
    }

    [Fact]
    public async Task AuditPage_SinceWithNegativeOffset_ExcludesEarlierEntries()
    {
        var table = "audit_" + Guid.NewGuid().ToString("N")[..8];
        await _repo.RecordAuditEventAsync(Entry(table, new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero)));
        await _repo.RecordAuditEventAsync(Entry(table, new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)));
        // 09:00Z expressed with a -08:00 offset.
        var since = new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.FromHours(-8));

        var result = await _repo.QueryAuditLogsAsync(targetTable: table, since: since);

        result.Count.ShouldBe(1);
        result[0].OccurredAt.ToUniversalTime().Hour.ShouldBe(12);
    }

    [Fact]
    public async Task Write_NonUtcOccurredAt_ChainVerifies()
    {
        var table = "audit_" + Guid.NewGuid().ToString("N")[..8];
        await _repo.RecordAuditEventAsync(Entry(table, new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(2))));
        await _repo.RecordAuditEventAsync(Entry(table, new DateTimeOffset(2026, 10, 22, 11, 0, 0, TimeSpan.FromHours(-7))));

        (await _repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }
}
