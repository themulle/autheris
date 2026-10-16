using Autheris.Application.Governance.Paging;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>WP-SC-10 / SEC-SC-16: keyset cursor over the audit chain on the SQLite governance store.</summary>
[Trait("Category", "Security")]
public sealed class AuditCursorQueryTests : IDisposable
{
    private readonly Autheris.Infrastructure.Persistence.SqliteGovernanceRepository _repo =
        GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>());

    public void Dispose() => _repo.Dispose();

    private static AuditLogEntry E(int i, string tenant = "t1", string table = "default.sales.orders", string actor = "S-1-5-21-A") => new()
    {
        TenantId = new TenantId(tenant),
        EventType = AuditEventTypes.AuditConfigChanged,
        ActorSid = new Sid(actor),
        TargetTable = table,
        Decision = "ALLOW",
        TraceId = $"trace-{i}"
    };

    [Fact]
    public async Task SEC_SC_16_Pages_WalkTheTenantChainNewestFirst_WithoutDuplicatesOrGaps()
    {
        await _repo.RecordAuditEventsAsync(Enumerable.Range(0, 25).Select(i => E(i)).ToList());
        await _repo.RecordAuditEventsAsync(Enumerable.Range(100, 5).Select(i => E(i, tenant: "t2")).ToList());

        var seen = new List<long>();
        long? before = null;
        int pages = 0;
        do
        {
            var page = await _repo.QueryAuditLogPageAsync(new AuditLogPageQuery(new TenantId("t1"), BeforeSequence: before, PageSize: 10));
            seen.AddRange(page.Items.Select(e => e.ChainSequence!.Value));
            before = page.NextBeforeSequence;
            pages++;
        }
        while (before != null);

        pages.ShouldBe(3);
        seen.Count.ShouldBe(25);
        seen.ShouldBe(seen.OrderByDescending(s => s).ToList());
        seen.Distinct().Count().ShouldBe(25);
    }

    [Fact]
    public async Task SEC_SC_16_Pages_NeverCrossTheTenant_UnlessNoTenantIsGiven()
    {
        await _repo.RecordAuditEventsAsync([E(1, "t1"), E(2, "t2")]);

        var t1 = await _repo.QueryAuditLogPageAsync(new AuditLogPageQuery(new TenantId("t1")));
        t1.Items.ShouldHaveSingleItem().TenantId.Value.ShouldBe("t1");

        var all = await _repo.QueryAuditLogPageAsync(new AuditLogPageQuery(null));
        all.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SEC_SC_16_Pages_FilterByTableActorAndSince_Together()
    {
        await _repo.RecordAuditEventsAsync([
            E(1, table: "default.sales.orders", actor: "S-1-5-21-A"),
            E(2, table: "DEFAULT.SALES.ORDERS", actor: "S-1-5-21-B"),
            E(3, table: "default.sales.items", actor: "S-1-5-21-A")]);

        var byTable = await _repo.QueryAuditLogPageAsync(new AuditLogPageQuery(new TenantId("t1"), TargetTable: "default.sales.orders"));
        byTable.Items.Count.ShouldBe(2);

        var byBoth = await _repo.QueryAuditLogPageAsync(new AuditLogPageQuery(new TenantId("t1"), TargetTable: "default.sales.orders", Actor: new Sid("S-1-5-21-A")));
        byBoth.Items.ShouldHaveSingleItem();

        var future = await _repo.QueryAuditLogPageAsync(new AuditLogPageQuery(new TenantId("t1"), Since: DateTimeOffset.UtcNow.AddDays(1)));
        future.Items.ShouldBeEmpty();
        future.NextBeforeSequence.ShouldBeNull();
    }

    [Fact]
    public async Task SEC_SC_16_LastPage_HasNoCursor_AndAnExactFitDoesNotInventOne()
    {
        await _repo.RecordAuditEventsAsync(Enumerable.Range(0, 10).Select(i => E(i)).ToList());

        var exact = await _repo.QueryAuditLogPageAsync(new AuditLogPageQuery(new TenantId("t1"), PageSize: 10));

        exact.Items.Count.ShouldBe(10);
        exact.NextBeforeSequence.ShouldBeNull();
    }
}
