using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ICloudEventSubscriptionStore</c> (outbound CloudEvent webhook subscriptions) across PostgreSQL and SQL Server engines.
/// Every read and write is tenant scoped; a duplicate id never overwrites a row (even across tenants) and the per-tenant quota is enforced under parallel registration.
/// </summary>
public abstract class CloudEventSubscriptionContract : GovernanceContractBase
{
    private const int Quota = 5;

    private ICloudEventSubscriptionStore Subscriptions => Store.CloudEventSubscriptions;

    private CloudEventWebhookSubscription Sub(
        string id, string? tenant = null, string filter = "*", CdcOperation[]? ops = null, bool enabled = true) =>
        new(id, tenant ?? Store.TenantA.Value, "https://example.test/hook?token=abc", filter, ops ?? [], "enc:v1:abc",
            IsEnabled: enabled,
            CustomHeaders: new Dictionary<string, string> { ["X-Custom"] = "v" },
            CreatedBySid: "S-1-5-21-9", CreatedByGroupSids: ["G1", "G2"], CreatedByRoles: ["Admin"],
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));

    private async Task RegisterAsync(CloudEventWebhookSubscription s) => await Subscriptions.RegisterSubscriptionAsync(s, Quota);

    [RequiresContainerRuntimeFact]
    public async Task Register_ThenGet_RoundTripsAllFields()
    {
        if (!Store.IsAvailable) return;

        await RegisterAsync(Sub("s1", ops: [CdcOperation.Insert, CdcOperation.Delete]));
        var s = await Subscriptions.GetSubscriptionAsync(Store.TenantA.Value, "s1");

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

    [RequiresContainerRuntimeFact]
    public async Task Register_BeyondQuota_Throws_AndOtherTenantsAreUnaffected()
    {
        if (!Store.IsAvailable) return;
        for (var i = 0; i < Quota; i++)
        {
            await RegisterAsync(Sub($"a{i}"));
        }

        await Should.ThrowAsync<InvalidOperationException>(async () => await RegisterAsync(Sub("overflow")));
        await RegisterAsync(Sub("b0", Store.TenantB.Value));

        (await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value)).Count.ShouldBe(Quota);
    }

    [RequiresContainerRuntimeFact]
    public async Task Register_ParallelAboveQuota_NeverExceedsQuota()
    {
        if (!Store.IsAvailable) return;

        var tasks = Enumerable.Range(0, Quota * 4).Select(async i =>
        {
            try
            {
                await RegisterAsync(Sub($"p{i}"));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(x => x).ShouldBe(Quota);
        (await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value)).Count.ShouldBe(Quota);
    }

    [RequiresContainerRuntimeFact]
    public async Task GetSubscriptions_FiltersByTableOperationEnabledAndTenant()
    {
        if (!Store.IsAvailable) return;
        await RegisterAsync(Sub("all", filter: "*"));
        await RegisterAsync(Sub("orders", filter: "orders", ops: [CdcOperation.Insert]));
        await RegisterAsync(Sub("qualified", filter: "sales.public.orders"));
        await RegisterAsync(Sub("other", filter: "invoices"));
        await RegisterAsync(Sub("off", filter: "*", enabled: false));
        await RegisterAsync(Sub("foreign", Store.TenantB.Value, "*"));
        var table = new TableIdentifier("sales", "public", "orders");

        var insert = await Subscriptions.GetSubscriptionsAsync(Store.TenantA.Value, table, CdcOperation.Insert);
        var update = await Subscriptions.GetSubscriptionsAsync(Store.TenantA.Value, table, CdcOperation.Update);
        var byName = await Subscriptions.GetSubscriptionsAsync(Store.TenantA.Value, "orders", CdcOperation.Insert);

        insert.Select(x => x.Id).ShouldBe(["all", "orders", "qualified"], ignoreOrder: true);
        update.Select(x => x.Id).ShouldBe(["all", "qualified"], ignoreOrder: true);
        // A bare table name is not a parsable identifier: plain case-insensitive name comparison only (no qualified filters).
        byName.Select(x => x.Id).ShouldBe(["all", "orders"], ignoreOrder: true);
    }

    [RequiresContainerRuntimeFact]
    public async Task Subscriptions_SurviveRestart_NewInstanceSeesRows()
    {
        if (!Store.IsAvailable) return;
        await RegisterAsync(Sub("persisted"));

        await using var second = await Store.CreateAdditionalStoreAsync();

        (await second.CloudEventSubscriptions.GetSubscriptionAsync(Store.TenantA.Value, "persisted")).ShouldNotBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task MalformedJsonRow_IsSkipped_OtherRowsStillLoad()
    {
        if (!Store.IsAvailable) return;
        await RegisterAsync(Sub("good"));
        await Store.ExecuteNonQueryAsync(
            "INSERT INTO CLOUDEVENT_SUBSCRIPTIONS (id, tenant_id, target_url, filter_table, filter_operations, is_enabled, created_ms) VALUES (@id, @tenant, @url, @filter, @ops, @enabled, @created)",
            ("@id", "bad"), ("@tenant", Store.TenantA.Value), ("@url", "https://e.test"), ("@filter", "*"), ("@ops", "{not json"), ("@enabled", true), ("@created", 1L));

        var list = await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value);

        list.Select(x => x.Id).ShouldBe(["good"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await RegisterAsync(Sub("a1"));
        await RegisterAsync(Sub("a2", enabled: false));
        await RegisterAsync(Sub("b1", Store.TenantB.Value));

        (await Subscriptions.GetSubscriptionAsync(Store.TenantB.Value, "a1")).ShouldBeNull();
        (await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value)).Select(x => x.Id).ShouldBe(["a1", "a2"], ignoreOrder: true);
        (await Subscriptions.ListSubscriptionsAsync("tenant-c")).ShouldBeEmpty();
        (await Subscriptions.GetSubscriptionsAsync("tenant-c", "orders", CdcOperation.Insert)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await RegisterAsync(Sub("s1"));

        (await Subscriptions.RemoveSubscriptionAsync(Store.TenantB.Value, "s1")).ShouldBeFalse();
        (await Subscriptions.GetSubscriptionAsync(Store.TenantA.Value, "s1")).ShouldNotBeNull();
        (await Subscriptions.RemoveSubscriptionAsync(Store.TenantA.Value, "s1")).ShouldBeTrue();
        (await Subscriptions.RemoveSubscriptionAsync(Store.TenantA.Value, "s1")).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(async () => await RegisterAsync(Sub("x", " ")));
        await Should.ThrowAsync<ArgumentException>(async () => await Subscriptions.GetSubscriptionAsync(" ", "x"));
        await Should.ThrowAsync<ArgumentException>(async () => await Subscriptions.ListSubscriptionsAsync(string.Empty));
        await Should.ThrowAsync<ArgumentException>(async () => await Subscriptions.RemoveSubscriptionAsync(" ", "x"));
        await Should.ThrowAsync<ArgumentException>(async () => await Subscriptions.GetSubscriptionsAsync(" ", "orders", CdcOperation.Insert));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        // A subscription is never overwritten: the second registration of an id fails and leaves the first row unchanged.
        await RegisterAsync(Sub("same"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await RegisterAsync(Sub("same") with { TargetUrl = "https://evil.test/" }));

        var rows = await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value);
        rows.ShouldHaveSingleItem().TargetUrl.ShouldBe("https://example.test/hook?token=abc");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        await RegisterAsync(Sub("same"));

        await Should.ThrowAsync<InvalidOperationException>(async () => await RegisterAsync(Sub("same", Store.TenantB.Value) with { TargetUrl = "https://evil.test/" }));
        await Should.ThrowAsync<InvalidOperationException>(async () => await RegisterAsync(Sub("same") with { TargetUrl = "https://evil.test/" }));

        (await Subscriptions.GetSubscriptionAsync(Store.TenantA.Value, "same"))!.TargetUrl.ShouldBe("https://example.test/hook?token=abc");
        (await Subscriptions.GetSubscriptionAsync(Store.TenantB.Value, "same")).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var t = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        await RegisterAsync(Sub("c") with { CreatedAt = t.AddSeconds(3) });
        await RegisterAsync(Sub("b") with { CreatedAt = t.AddSeconds(1) });
        await RegisterAsync(Sub("a") with { CreatedAt = t.AddSeconds(1) });

        // Ordered by creation time, then id.
        (await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value)).Select(x => x.Id).ShouldBe(["a", "b", "c"]);
        (await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value)).Select(x => x.Id).ShouldBe(["a", "b", "c"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(async () => await RegisterAsync(
            Sub("r") with { CreatedByRoles = Enumerable.Range(0, 300).Select(i => "r" + i).ToList() }));
        await Should.ThrowAsync<ArgumentException>(async () => await RegisterAsync(
            Sub("h") with { CustomHeaders = new Dictionary<string, string> { ["X"] = new string('v', 40000) } }));

        (await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        await RegisterAsync(Sub("gone"));
        await RegisterAsync(Sub("off", enabled: false));

        (await Subscriptions.RemoveSubscriptionAsync(Store.TenantA.Value, "gone")).ShouldBeTrue();

        (await Subscriptions.GetSubscriptionAsync(Store.TenantA.Value, "gone")).ShouldBeNull();
        (await Subscriptions.ListSubscriptionsAsync(Store.TenantA.Value)).Select(x => x.Id).ShouldBe(["off"]);
        // A disabled subscription is never matched for delivery.
        (await Subscriptions.GetSubscriptionsAsync(Store.TenantA.Value, "orders", CdcOperation.Insert)).ShouldBeEmpty();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCloudEventSubscriptionContractTests(GovernancePostgresFixture fixture) : CloudEventSubscriptionContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "cloud_event_subscription_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCloudEventSubscriptionContractTests(GovernanceSqlServerFixture fixture) : CloudEventSubscriptionContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "cloud_event_subscription_contract");
}
