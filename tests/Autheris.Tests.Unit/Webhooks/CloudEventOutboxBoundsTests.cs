namespace Autheris.Tests.Unit.Webhooks;

using System;
using Autheris.Application.Events.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class CloudEventOutboxBoundsTests
{
    private static CloudEventWebhookSubscription Sub(string id = "sub-1") =>
        new(id, "tenant-a", "https://example.test/hook", "*", [], "secret");

    private static CloudEventEnvelope Env(string id = "cdc-1", string source = "/autheris/cdc/a/b/c", string type = "autheris.cdc.insert", object? data = null) =>
        new("1.0", id, source, type, DateTimeOffset.UtcNow, "application/json", "tenant-a", data ?? new { a = 1 });

    [Fact]
    public void Create_WithinBounds_Succeeds() =>
        CloudEventOutboxMessageFactory.Create(Sub(), Env(), DateTimeOffset.UtcNow, 3, 4096).EventId.ShouldBe("cdc-1");

    [Fact]
    public void Create_OverlongEventId_Throws() =>
        Should.Throw<WebhookOutboxBoundsException>(() =>
            CloudEventOutboxMessageFactory.Create(Sub(), Env(id: new string('x', 129)), DateTimeOffset.UtcNow, 3, 4096));

    [Fact]
    public void Create_OverlongEventSource_Throws() =>
        Should.Throw<WebhookOutboxBoundsException>(() =>
            CloudEventOutboxMessageFactory.Create(Sub(), Env(source: new string('x', 513)), DateTimeOffset.UtcNow, 3, 4096));

    [Fact]
    public void Create_OverlongEventType_Throws() =>
        Should.Throw<WebhookOutboxBoundsException>(() =>
            CloudEventOutboxMessageFactory.Create(Sub(), Env(type: new string('x', 257)), DateTimeOffset.UtcNow, 3, 4096));

    [Fact]
    public void Create_OverlongSubscriptionId_Throws() =>
        Should.Throw<WebhookOutboxBoundsException>(() =>
            CloudEventOutboxMessageFactory.Create(Sub(new string('s', 129)), Env(), DateTimeOffset.UtcNow, 3, 4096));

    [Fact]
    public void Create_PayloadOverMaxBytes_ThrowsAndNeverTruncates() =>
        Should.Throw<WebhookOutboxBoundsException>(() =>
            CloudEventOutboxMessageFactory.Create(Sub(), Env(data: new { big = new string('x', 5000) }), DateTimeOffset.UtcNow, 3, 4096));

    [Fact]
    public void Create_PayloadBoundIsMeasuredInUtf8Bytes() =>
        Should.Throw<WebhookOutboxBoundsException>(() =>
            // 2000 chars x 3 UTF-8 bytes = 6000 bytes > 4096 although < 4096 chars.
            CloudEventOutboxMessageFactory.Create(Sub(), Env(data: new { big = new string('€', 2000) }), DateTimeOffset.UtcNow, 3, 4096));
}
