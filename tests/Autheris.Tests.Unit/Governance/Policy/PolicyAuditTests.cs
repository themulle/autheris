using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Policy;

[Trait("Category", "Security")]
public sealed class PolicyAuditTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid GovActor = new("S-1-5-21-1001-2002-3003-10");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakePolicyRepo(StoredTenantAccessPolicy initial) : IAccessPolicySettingsRepository
    {
        public StoredTenantAccessPolicy? Stored { get; private set; } = initial;
        public List<(TenantAccessPolicyUpdate Settings, int ExpectedVersion)> Upserts { get; } = [];

        public Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult(Stored is not null && Stored.Tenant == tenant ? Stored : null);

        public Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default)
        {
            Upserts.Add((settings, expectedVersion));
            Stored = new StoredTenantAccessPolicy(tenant, settings, expectedVersion + 1, actor.Value, T0.AddMinutes(5));
            return Task.FromResult(new AccessPolicyWriteResult(AccessPolicyWriteStatus.Updated, Stored));
        }
    }

    [Fact]
    public async Task Before_after_without_secrets()
    {
        var repo = new FakePolicyRepo(new StoredTenantAccessPolicy(
            TenantA,
            new TenantAccessPolicyUpdate(MaxSelfGrantDays: 180, ServeImportSamples: true),
            Version: 1,
            UpdatedBy: GovActor.Value,
            UpdatedAt: T0));
        var audit = new InMemoryAuditWriter();

        var provider = new AccessPolicySettingsProvider(
            repo,
            Options.Create(new AccessPolicyOptions()),
            audit,
            new FakeTimeProvider(T0.AddMinutes(5)));

        var updated = await provider.UpdateAsync(
            TenantA,
            new TenantAccessPolicyUpdate(MaxSelfGrantDays: 90, ServeImportSamples: false),
            expectedVersion: 1,
            GovActor);

        updated.Status.ShouldBe(AccessPolicyUpdateStatus.Updated);
        var recorded = audit.Entries.ShouldHaveSingleItem();
        recorded.EventType.ShouldBe(AuditEventTypes.PolicyChanged);
        recorded.TenantId.ShouldBe(TenantA);
        recorded.ActorSid.ShouldBe(GovActor);

        using var doc = JsonDocument.Parse(recorded.DetailsJson);
        doc.RootElement.GetProperty("before").GetProperty("maxSelfGrantDays").GetInt32().ShouldBe(180);
        doc.RootElement.GetProperty("after").GetProperty("maxSelfGrantDays").GetInt32().ShouldBe(90);
        doc.RootElement.GetProperty("before").GetProperty("serveImportSamples").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("after").GetProperty("serveImportSamples").GetBoolean().ShouldBeFalse();
        recorded.DetailsJson.ShouldNotContain("secret", Case.Insensitive);
        recorded.DetailsJson.ShouldNotContain("webhook", Case.Insensitive);
        recorded.DetailsJson.ShouldNotContain("password", Case.Insensitive);
    }

    [Fact]
    public async Task Policy_update_compensates_and_throws_when_required_audit_fails()
    {
        var before = new StoredTenantAccessPolicy(TenantA, new TenantAccessPolicyUpdate(MaxSelfGrantDays: 180), 1, GovActor.Value, T0);
        var repo = new FakePolicyRepo(before);
        var audit = new InMemoryAuditWriter(failure: new InvalidOperationException("audit store offline"));

        var provider = new AccessPolicySettingsProvider(repo, Options.Create(new AccessPolicyOptions()), audit, new FakeTimeProvider(T0));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.UpdateAsync(TenantA, new TenantAccessPolicyUpdate(MaxSelfGrantDays: 60), 1, GovActor));

        repo.Upserts.Count.ShouldBe(2);
        repo.Upserts[1].ExpectedVersion.ShouldBe(2);
        repo.Upserts[1].Settings.ShouldBe(before.Settings);
    }
}
