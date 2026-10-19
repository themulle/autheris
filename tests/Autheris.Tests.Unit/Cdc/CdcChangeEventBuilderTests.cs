namespace Autheris.Tests.Unit.Cdc;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Extensions.Cdc;
using Shouldly;
using Xunit;
using static CdcProducerHarness;

/// <summary>Tenant attribution of every image (PLAN-CDC-99 D-A6, SEC-CDC99-01, SEC-CDC99-03) at production time.</summary>
public sealed class CdcChangeEventBuilderTests
{
    private static async Task<CdcProducerHarness> TenantedAsync(TableMetadata? metadata = null)
    {
        var h = new CdcProducerHarness(metadata: metadata ?? Meta(columns: ["Id", "Name", "TenantId"]));
        await h.SeedWatermarkAsync(5);
        return h;
    }

    [Fact]
    public async Task TenantColumn_AttributesTheRowToItsTenant_ByteExact()
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(Change(10, "I", 1, tenant: "Acme"));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var e = h.InnerStore.Only.Events.Single();
        e.TenantScope.ShouldBe(CdcTenantScope.Row);
        e.TenantId.ShouldBe("Acme"); // not lower-cased: byte-exact (SEC-CDC99-03)
        e.PriorTenantId.ShouldBeNull();
    }

    [Fact]
    public async Task RowMovesTenant_EventBelongsToTheNewTenant_AndNamesThePriorTenant()
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(Change(10, "I", 1, "r", tenant: "acme"));
        await h.Poller.PollTableChangesAsync(TableId, 1);
        h.Source.Changes.Clear();
        h.Source.Changes.Add(Change(11, "U", 1, "r", tenant: "globex"));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var moved = h.InnerStore.Only.Events.Last();
        moved.TenantId.ShouldBe("globex");
        moved.PriorTenantId.ShouldBe("acme"); // SEC-CDC99-01: the before-image belongs to another tenant
        (await h.ReadChangesAsync()).Last().Payload.Before!["TenantId"].ShouldBe("acme");
    }

    [Fact]
    public async Task UpdateWithinOneTenant_HasNoPriorTenant()
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(Change(10, "I", 1, "a", tenant: "acme"));
        await h.Poller.PollTableChangesAsync(TableId, 1);
        h.Source.Changes.Clear();
        h.Source.Changes.Add(Change(11, "U", 1, "b", tenant: "acme"));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        h.InnerStore.Only.Events.Last().PriorTenantId.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad tenant!")]
    [InlineData("tenant.with.dots")]
    public async Task NullOrMalformedTenantValue_IsUnresolved(string? tenant)
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(new FakeChangeSource.Change(10, "I", new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "n", ["TenantId"] = tenant }));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var e = h.InnerStore.Only.Events.Single();
        e.TenantScope.ShouldBe(CdcTenantScope.Unresolved);
        e.TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task BeforeImageOfUnknownTenant_IsDropped_NeverExposed()
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(new FakeChangeSource.Change(10, "I", new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "secret", ["TenantId"] = null }));
        await h.Poller.PollTableChangesAsync(TableId, 1);
        h.Source.Changes.Clear();
        h.Source.Changes.Add(Change(11, "U", 1, "n", tenant: "acme"));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var (record, payload) = (await h.ReadChangesAsync()).Last();
        record.TenantScope.ShouldBe(CdcTenantScope.Row);
        payload.Before.ShouldBeNull();
        payload.BeforeUnknown.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteTenant_IsTheLastImagesTenant()
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(Change(10, "I", 1, "r", tenant: "acme"));
        await h.Poller.PollTableChangesAsync(TableId, 1);
        h.Source.Changes.Clear();
        h.Source.Changes.Add(new FakeChangeSource.Change(11, "D", new Dictionary<string, object?> { ["Id"] = 1 }));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var del = h.InnerStore.Only.Events.Last();
        del.TenantScope.ShouldBe(CdcTenantScope.Row);
        del.TenantId.ShouldBe("acme");
    }

    [Fact]
    public async Task DeleteWithoutImage_OnATenantedTable_IsUnresolved_FailClosed()
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(new FakeChangeSource.Change(11, "D", new Dictionary<string, object?> { ["Id"] = 9 }));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        h.InnerStore.Only.Events.Single().TenantScope.ShouldBe(CdcTenantScope.Unresolved);
    }

    [Fact]
    public async Task DeleteWithoutImage_TenantInThePrimaryKey_IsResolvedFromTheKey()
    {
        var h = await TenantedAsync(Meta(columns: ["TenantId", "Id", "Name"], pk: ["TenantId", "Id"]));
        h.Source.ChangeTableKey = ["TenantId", "Id"];
        h.Source.Changes.Add(new FakeChangeSource.Change(11, "D", new Dictionary<string, object?> { ["TenantId"] = "acme", ["Id"] = 9 }));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var del = h.InnerStore.Only.Events.Single();
        del.TenantScope.ShouldBe(CdcTenantScope.Row);
        del.TenantId.ShouldBe("acme");
    }

    [Fact]
    public async Task TableWithoutTenantColumn_IsSharedOrOwned()
    {
        var shared = new CdcProducerHarness();
        await shared.SeedWatermarkAsync(5);
        shared.Source.Changes.Add(Change(10, "I", 1));
        await shared.Poller.PollTableChangesAsync(TableId, 1);
        shared.InnerStore.Only.Events.Single().TenantScope.ShouldBe(CdcTenantScope.Shared);
        shared.InnerStore.Only.Events.Single().TenantId.ShouldBeNull();

        var owned = new CdcProducerHarness(metadata: Meta(owner: "acme"));
        await owned.SeedWatermarkAsync(5);
        owned.Source.Changes.Add(Change(10, "I", 1));
        await owned.Poller.PollTableChangesAsync(TableId, 1);
        owned.InnerStore.Only.Events.Single().TenantScope.ShouldBe(CdcTenantScope.Owner);
        owned.InnerStore.Only.Events.Single().TenantId.ShouldBe("acme");
    }

    [Fact]
    public async Task EventAad_BindsThePositionAndTheTenant()
    {
        var h = await TenantedAsync();
        h.Source.Changes.Add(Change(10, "I", 1, tenant: "acme"));
        await h.Poller.PollTableChangesAsync(TableId, 1);
        var e = h.InnerStore.Only.Events.Single();

        await Should.ThrowAsync<System.Security.Cryptography.CryptographicException>(async () =>
            await h.Crypto.UnprotectEventAsync(e with { Offset = 2 }));
        await Should.ThrowAsync<System.Security.Cryptography.CryptographicException>(async () =>
            await h.Crypto.UnprotectEventAsync(e with { TenantId = "globex" }));
    }
}
