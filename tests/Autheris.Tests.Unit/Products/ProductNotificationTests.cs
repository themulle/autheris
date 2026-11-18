using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Products;
using Autheris.Domain.Audit;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductNotificationTests
{
    private readonly ProductHarness _h = new();

    private List<ConsentEventWrite> Events() =>
        _h.Outbox.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IConsentEventOutbox.AppendAsync))
            .Select(c => (ConsentEventWrite)c.GetArguments()[0]!).ToList();

    [Fact]
    public async Task Base_owners_notified_on_create_change_retire()
    {
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Orders, ProductHarness.Customers];
        _h.Owned[ProductHarness.Carol.Value] = [ProductHarness.Customers]; // another owner of customers: notified, never asked
        var alice = _h.Caller(ProductHarness.Alice);

        await _h.Service.PutAsync(_h.Put(alice, ProductHarness.CustomerOrders));
        Events().Select(e => e.EventType).ShouldBe([ProductEventTypes.Created, ProductEventTypes.Created]);

        await _h.Service.PutAsync(_h.Put(alice, "SELECT id, total FROM crm.public.orders"));
        await _h.Service.RetireAsync(alice, "sales", "orders_view");

        var events = Events();
        events.Select(e => e.EventType).ShouldBe(
            [ProductEventTypes.Created, ProductEventTypes.Created, ProductEventTypes.Changed, ProductEventTypes.Retired], ignoreOrder: false);
        events.ShouldAllBe(e => e.TenantId == ProductHarness.Tenant.Value && e.PayloadJson.Contains("definitionHash"));
        events.ShouldAllBe(e => !e.PayloadJson.Contains("SELECT", StringComparison.OrdinalIgnoreCase), "the payload is thin: no definition text");
        var created = _h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.ProductCreated);
        created.DetailsJson.ShouldContain("\"baseOwnersNotified\":3");
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductRetired);
    }

    [Fact]
    public async Task Settings_only_changes_do_not_notify_and_base_owners_hold_no_veto()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        await _h.Service.PutAsync(_h.Put(admin, ProductHarness.CustomerOrders, ProductHarness.Bob));
        var before = Events().Count;

        await _h.Service.UpdateSettingsAsync(_h.Caller(ProductHarness.Bob), "sales", "orders_view", "text", null);

        Events().Count.ShouldBe(before);
        before.ShouldBe(2, "the admin creation notified the owners of both base objects without waiting for any approval");
    }

    [Fact]
    public async Task A_failing_notification_never_blocks_the_creation()
    {
        _h.Outbox.AppendAsync(default!, default, default).ReturnsForAnyArgs<Task<long>>(_ => throw new InvalidOperationException("outbox down"));
        var admin = _h.Caller(ProductHarness.Admin, admin: true);

        var product = await _h.Service.PutAsync(_h.Put(admin, ProductHarness.CustomerOrders, ProductHarness.Bob));

        product.Status.ShouldBe(DataProductStatus.Active);
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductCreated);
    }
}
