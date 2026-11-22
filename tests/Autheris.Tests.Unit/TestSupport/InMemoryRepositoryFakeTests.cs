using Shouldly;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;

namespace Autheris.Tests.Unit.TestSupport;

public sealed class InMemoryRepositoryFakeTests
{
    [Fact]
    public async Task Consent_fake_tenant_overloads_require_a_tenant()
    {
        var repo = new InMemoryConsentRepository();
        var table = default(TableIdentifier);

        await Should.ThrowAsync<ArgumentException>(() =>
            repo.GetActiveConsentsForSubjectsAsync([], table, DateTimeOffset.UtcNow, null));
        await Should.ThrowAsync<ArgumentException>(() =>
            repo.GetAllActiveConsentsForSubjectsAsync([], null, null, null));
        (await repo.GetAllActiveConsentsForSubjectsAsync([], null, null, Autheris.Tests.Common.TestTenants.Default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Audit_fake_lists_newest_insertion_first_and_clamps_limit()
    {
        var repo = new InMemoryAuditLogRepository();
        await repo.RecordAuditEventAsync(new AuditLogEntry { EventType = "A" });
        await repo.RecordAuditEventAsync(new AuditLogEntry { EventType = "B" });

        var rows = await repo.GetAuditLogEntriesAsync(limit: 0);

        rows.Select(e => e.EventType).ShouldBe(["B"]);
    }
}
