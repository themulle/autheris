using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

[Trait("Category", "Security")]
public sealed class MigrationPurgeTests
{
    [Fact]
    public async Task Startup_writes_purge_audit()
    {
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
        var time = new FakeTimeProvider(now);
        var audit = new InMemoryAuditWriter();

        var store = Substitute.For<MigrationPurgeStore>();
        IReadOnlyList<MigrationPurgeReportEntry> unaudited =
        [
            new("0041:CONSENTS", MigrationPurgeStore.PermissionRequestsPurgeStep, "CONSENTS", 14, null, now.AddMinutes(-5), null),
            new("0041:CONSENT_REQUESTS", MigrationPurgeStore.PermissionRequestsPurgeStep, "CONSENT_REQUESTS", 3, null, now.AddMinutes(-5), null)
        ];
        IReadOnlyList<GrantRecordHeadArchiveEntry> archivedHeads =
        [
            new(MigrationPurgeStore.PermissionRequestsPurgeStep, "tenant-a", 7, new string('a', 64), now.AddMinutes(-5))
        ];

        var markedAudited = false;
        store.GetUnauditedPurgeReportsAsync(MigrationPurgeStore.PermissionRequestsPurgeStep, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(markedAudited ? Array.Empty<MigrationPurgeReportEntry>() : unaudited));
        store.GetArchivedHeadsAsync(MigrationPurgeStore.PermissionRequestsPurgeStep, Arg.Any<CancellationToken>())
            .Returns(archivedHeads);
        store.When(s => s.MarkPurgeReportsAuditedAsync(MigrationPurgeStore.PermissionRequestsPurgeStep, now, Arg.Any<CancellationToken>()))
            .Do(_ => markedAudited = true);

        var service = new MigrationPurgeAuditHostedService(store, audit, timeProvider: time);
        await service.StartAsync(CancellationToken.None);

        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.MigrationGrantsPurged);
        entry.TenantId.ShouldBe(TenantId.Platform);
        using var doc = JsonDocument.Parse(entry.DetailsJson);
        doc.RootElement.GetProperty("Step").GetString().ShouldBe(MigrationPurgeStore.PermissionRequestsPurgeStep);
        doc.RootElement.GetProperty("ArchivedHeadsCount").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("ArchivedHeadHash").GetString().ShouldBe(MigrationPurgeStore.ComputeArchivedHeadDigest(archivedHeads));
        doc.RootElement.GetProperty("Tables").GetProperty("CONSENTS").GetInt64().ShouldBe(14);
        doc.RootElement.GetProperty("Tables").GetProperty("CONSENT_REQUESTS").GetInt64().ShouldBe(3);

        // Subsequent start is a no-op once marked audited.
        await service.StartAsync(CancellationToken.None);
        audit.Entries.Count.ShouldBe(1);
    }
}
