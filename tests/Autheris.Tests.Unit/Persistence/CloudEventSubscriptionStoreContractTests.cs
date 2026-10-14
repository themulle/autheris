namespace Autheris.Tests.Unit.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// WP-ARC-A-03: dialect-neutral contract of <see cref="ICloudEventSubscriptionStore"/>. SQLite runs in the unit project; the
/// PostgreSQL and SQL Server subclasses live in the integration project (this file is linked there). A subclass returns
/// <c>null</c> from <see cref="OpenAsync"/> when its database is unavailable. Calling <see cref="OpenAsync"/> twice yields two
/// store instances over the SAME database (restart simulation).
/// </summary>
public abstract class CloudEventSubscriptionStoreContractTests : IAsyncLifetime
{
    protected const int Quota = 5;
    private readonly List<object> _disposables = [];

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            if (d is IAsyncDisposable ad) await ad.DisposeAsync();
            else if (d is IDisposable sd) sd.Dispose();
        }
    }

    /// <summary>Opens a store (per-tenant quota <see cref="Quota"/>); <c>null</c> when the backend is unavailable.</summary>
    protected abstract Task<ICloudEventSubscriptionStore?> OpenAsync();

    protected T Track<T>(T disposable) where T : notnull
    {
        _disposables.Add(disposable);
        return disposable;
    }

    protected static CloudEventWebhookSubscription Sub(
        string id, string tenant = "tenant-a", string filter = "*", CdcOperation[]? ops = null, bool enabled = true) =>
        new(id, tenant, "https://example.test/hook?token=abc", filter, ops ?? [], "enc:v1:abc",
            IsEnabled: enabled,
            CustomHeaders: new Dictionary<string, string> { ["X-Custom"] = "v" },
            CreatedBySid: "S-1-5-21-9", CreatedByGroupSids: ["G1", "G2"], CreatedByRoles: ["Admin"],
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));

    [Fact]
    public async Task Register_ThenGet_RoundTripsAllFields()
    {
        if (await OpenAsync() is not { } store) return;

        await store.RegisterSubscriptionAsync(Sub("s1", ops: [CdcOperation.Insert, CdcOperation.Delete]), Quota);
        var s = await store.GetSubscriptionAsync("tenant-a", "s1");

        s.ShouldNotBeNull();
        s.TargetUrl.ShouldBe("https://example.test/hook?token=abc");
        s.HmacSecret.ShouldBe("enc:v1:abc");
        s.FilterOperations.ShouldBe([CdcOperation.Insert, CdcOperation.Delete], ignoreOrder: true);
        s.CustomHeaders!["X-Custom"].ShouldBe("v");
        s.CreatedBySid.ShouldBe("S-1-5-21-9");
        s.CreatedByGroupSids.ShouldBe(["G1", "G2"], ignoreOrder: true);
        s.CreatedByRoles.ShouldBe(["Admin"]);
        s.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));
        s.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Get_WithWrongTenant_ReturnsNull()
    {
        if (await OpenAsync() is not { } store) return;

        await store.RegisterSubscriptionAsync(Sub("s1", "tenant-a"), Quota);

        (await store.GetSubscriptionAsync("tenant-b", "s1")).ShouldBeNull();
    }

    [Fact]
    public async Task Remove_IsTenantScoped()
    {
        if (await OpenAsync() is not { } store) return;

        await store.RegisterSubscriptionAsync(Sub("s1", "tenant-a"), Quota);

        (await store.RemoveSubscriptionAsync("tenant-b", "s1")).ShouldBeFalse();
        (await store.GetSubscriptionAsync("tenant-a", "s1")).ShouldNotBeNull();
        (await store.RemoveSubscriptionAsync("tenant-a", "s1")).ShouldBeTrue();
        (await store.RemoveSubscriptionAsync("tenant-a", "s1")).ShouldBeFalse();
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnTenant()
    {
        if (await OpenAsync() is not { } store) return;

        await store.RegisterSubscriptionAsync(Sub("a1", "tenant-a"), Quota);
        await store.RegisterSubscriptionAsync(Sub("a2", "tenant-a", enabled: false), Quota);
        await store.RegisterSubscriptionAsync(Sub("b1", "tenant-b"), Quota);

        (await store.ListSubscriptionsAsync("tenant-a")).Select(x => x.Id).ShouldBe(["a1", "a2"], ignoreOrder: true);
        (await store.ListSubscriptionsAsync("tenant-c")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Register_DuplicateId_DoesNotOverwrite_AndThrows()
    {
        if (await OpenAsync() is not { } store) return;

        await store.RegisterSubscriptionAsync(Sub("same", "tenant-a"), Quota);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await store.RegisterSubscriptionAsync(Sub("same", "tenant-b") with { TargetUrl = "https://evil.test/" }, Quota));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await store.RegisterSubscriptionAsync(Sub("same", "tenant-a") with { TargetUrl = "https://evil.test/" }, Quota));

        (await store.GetSubscriptionAsync("tenant-a", "same"))!.TargetUrl.ShouldBe("https://example.test/hook?token=abc");
        (await store.GetSubscriptionAsync("tenant-b", "same")).ShouldBeNull();
    }

    [Fact]
    public async Task Register_BeyondQuota_Throws_AndOtherTenantsAreUnaffected()
    {
        if (await OpenAsync() is not { } store) return;

        for (var i = 0; i < Quota; i++)
        {
            await store.RegisterSubscriptionAsync(Sub($"a{i}", "tenant-a"), Quota);
        }

        await Should.ThrowAsync<InvalidOperationException>(async () => await store.RegisterSubscriptionAsync(Sub("overflow", "tenant-a"), Quota));
        await store.RegisterSubscriptionAsync(Sub("b0", "tenant-b"), Quota);
        (await store.ListSubscriptionsAsync("tenant-a")).Count.ShouldBe(Quota);
    }

    [Fact]
    public async Task Register_ParallelAboveQuota_NeverExceedsQuota()
    {
        if (await OpenAsync() is not { } store) return;

        var tasks = Enumerable.Range(0, Quota * 4).Select(async i =>
        {
            try
            {
                await store.RegisterSubscriptionAsync(Sub($"p{i}", "tenant-a"), Quota);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(x => x).ShouldBe(Quota);
        (await store.ListSubscriptionsAsync("tenant-a")).Count.ShouldBe(Quota);
    }

    [Fact]
    public async Task GetSubscriptions_FiltersByTableOperationEnabledAndTenant()
    {
        if (await OpenAsync() is not { } store) return;

        await store.RegisterSubscriptionAsync(Sub("all", "tenant-a", "*"), Quota);
        await store.RegisterSubscriptionAsync(Sub("orders", "tenant-a", "orders", [CdcOperation.Insert]), Quota);
        await store.RegisterSubscriptionAsync(Sub("qualified", "tenant-a", "sales.public.orders"), Quota);
        await store.RegisterSubscriptionAsync(Sub("other", "tenant-a", "invoices"), Quota);
        await store.RegisterSubscriptionAsync(Sub("off", "tenant-a", "*", enabled: false), Quota);
        await store.RegisterSubscriptionAsync(Sub("foreign", "tenant-b", "*"), Quota);
        var table = new TableIdentifier("sales", "public", "orders");

        var insert = await store.GetSubscriptionsAsync("tenant-a", table, CdcOperation.Insert);
        var update = await store.GetSubscriptionsAsync("tenant-a", table, CdcOperation.Update);
        var byName = await store.GetSubscriptionsAsync("tenant-a", "orders", CdcOperation.Insert);

        insert.Select(x => x.Id).ShouldBe(["all", "orders", "qualified"], ignoreOrder: true);
        update.Select(x => x.Id).ShouldBe(["all", "qualified"], ignoreOrder: true);
        // A bare table name is not a parsable identifier: plain case-insensitive name comparison only (no qualified filters).
        byName.Select(x => x.Id).ShouldBe(["all", "orders"], ignoreOrder: true);
    }

    [Fact]
    public async Task Subscriptions_SurviveRestart_NewInstanceSeesRows()
    {
        if (await OpenAsync() is not { } first) return;
        await first.RegisterSubscriptionAsync(Sub("persisted", "tenant-a"), Quota);

        var second = await OpenAsync();

        second.ShouldNotBeNull();
        (await second.GetSubscriptionAsync("tenant-a", "persisted")).ShouldNotBeNull();
    }
}
