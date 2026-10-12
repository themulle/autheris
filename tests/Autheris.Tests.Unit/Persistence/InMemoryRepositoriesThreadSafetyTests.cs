namespace Autheris.Tests.Unit.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.SchemaRegistry;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

public sealed class InMemoryRepositoriesThreadSafetyTests
{
    [Fact]
    public async Task DbtProposalRepository_ConcurrentUpdates_AreThreadSafe()
    {
        var repo = new InMemoryDbtProposalRepository();
        var proposal = new DbtMetadataProposal(
            Id: Guid.NewGuid(),
            Table: new TableIdentifier("lakehouse", "dbo", "orders"),
            ColumnName: "order_id",
            SuggestedRuleType: "REDACT",
            SuggestedSensitivity: "HIGH",
            SuggestedOwnerTeam: "security",
            SourceDbtTag: "pii",
            Status: DbtProposalStatus.PendingReview,
            CreatedAt: DateTimeOffset.UtcNow);

        await repo.AddProposalAsync(proposal);

        const int iterations = 100;
        await Parallel.ForEachAsync(Enumerable.Range(0, iterations), async (i, ct) =>
        {
            await repo.UpdateProposalStatusAsync(
                proposal.Id,
                DbtProposalStatus.Approved,
                $"reviewer_{i}",
                ct);
        });

        var final = await repo.GetProposalByIdAsync(proposal.Id);
        final.ShouldNotBeNull();
        final.Status.ShouldBe(DbtProposalStatus.Approved);
        final.ReviewedBy.ShouldStartWith("reviewer_");
    }

    [Fact]
    public async Task CloudEventSubscriptionStore_ConcurrentRegistrations_EnforcesQuotaUnderConcurrency()
    {
        var store = new InMemoryCloudEventSubscriptionStore();
        const string tenantId = "tenant_concurrency";
        const int totalAttempts = 150; // Quota is 100

        var successfulRegistrations = 0;
        var rejectedRegistrations = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, totalAttempts), async (i, ct) =>
        {
            var sub = new CloudEventWebhookSubscription(
                Id: $"sub_{i}",
                TenantId: tenantId,
                TargetUrl: "https://example.com/webhook",
                FilterTable: "orders",
                FilterOperations: [CdcOperation.Insert],
                HmacSecret: "super-secret-hmac-key",
                IsEnabled: true);

            try
            {
                await store.RegisterSubscriptionAsync(sub, ct);
                Interlocked.Increment(ref successfulRegistrations);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref rejectedRegistrations);
            }
        });

        successfulRegistrations.ShouldBe(100);
        rejectedRegistrations.ShouldBe(50);

        var list = await store.ListSubscriptionsAsync(tenantId);
        list.Count.ShouldBe(100);
    }

    [Fact]
    public async Task SchemaRegistryRepository_ConcurrentWritesAndReads_AreConsistent()
    {
        var repo = new InMemorySchemaRegistryRepository();
        const string serviceName = "pricing-service";
        const int iterations = 50;

        await Parallel.ForEachAsync(Enumerable.Range(0, iterations), async (i, ct) =>
        {
            await repo.SaveSchemaAsync(new RegisteredSchema
            {
                ServiceName = serviceName,
                Version = $"v1.{i}",
                Sdl = $"type Query {{ price_{i}: Int }}",
                IsActive = true
            }, ct);

            var latest = await repo.GetLatestAsync(serviceName, ct);
            latest.ShouldNotBeNull();
            latest.ServiceName.ShouldBe(serviceName);
        });

        var history = await repo.GetHistoryAsync(serviceName);
        history.Count.ShouldBe(iterations);

        // Exactly one schema should be active in history
        history.Count(s => s.IsActive).ShouldBe(1);
    }

    [Fact]
    public async Task AccessProfileRepository_ConcurrentUpsertAndRead_IsThreadSafe()
    {
        var repo = new InMemoryAccessProfileRepository();
        var tenant = new TenantId("tenant-x");
        const int iterations = 100;

        await Parallel.ForEachAsync(Enumerable.Range(0, iterations), async (i, ct) =>
        {
            var profile = new AccessProfile
            {
                ProfileId = $"profile_{i}",
                Name = $"Profile {i}",
                TenantId = tenant,
                ValidTo = DateTimeOffset.UtcNow.AddHours(1),
                AssignedSubjects = [$"user_{i}"],
                TargetTables = ["*.*"]
            };

            await repo.UpsertProfileAsync(profile, ct);

            var retrieved = await repo.GetProfileAsync(tenant, $"profile_{i}", ct);
            retrieved.ShouldNotBeNull();
            retrieved.ProfileId.ShouldBe($"profile_{i}");
        });

        var all = await repo.GetAllProfilesAsync(tenant);
        all.Count.ShouldBe(iterations);
    }
}
