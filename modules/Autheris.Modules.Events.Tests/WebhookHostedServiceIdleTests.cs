namespace Autheris.Modules.Events.Tests;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The module registers its workers unconditionally; they read the host feature flags at run time and idle while disabled.</summary>
public sealed class WebhookHostedServiceIdleTests
{
    private static WebhookDeliveryHostedService Delivery(bool webhooksEnabled, bool cdcEnabled, IWebhookOutbox outbox)
    {
        var webhooks = new WebhooksOptions { Enabled = webhooksEnabled, PollIntervalSeconds = 1 };
        var options = Options.Create(new GatewayOptions { Webhooks = webhooks });
        var policy = new WebhookRetryPolicy(webhooks, TimeProvider.System);
        var client = new WebhookDeliveryClient(new HttpClient(), options, policy, NullLogger<WebhookDeliveryClient>.Instance);
        var resolver = new WebhookSubscriptionResolver(options, new ServiceCollection().BuildServiceProvider());
        return new WebhookDeliveryHostedService(
            outbox, client, resolver, policy, options, NullLogger<WebhookDeliveryHostedService>.Instance,
            cdcWebhooks: Options.Create(new CdcWebhooksOptions { Enabled = cdcEnabled }));
    }

    [Fact]
    public async Task Delivery_worker_idles_when_webhooks_and_cdc_webhooks_disabled()
    {
        var outbox = Substitute.For<IWebhookOutbox>();
        using var service = Delivery(webhooksEnabled: false, cdcEnabled: false, outbox);

        await service.StartAsync(CancellationToken.None);

        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        service.ExecuteTask.IsCompletedSuccessfully.ShouldBeTrue();
        await outbox.DidNotReceiveWithAnyArgs().ClaimBatchAsync(default, default, default, default!, default);
        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Delivery_worker_runs_when_either_feature_is_enabled(bool webhooksEnabled, bool cdcEnabled)
    {
        var outbox = Substitute.For<IWebhookOutbox>();
        using var service = Delivery(webhooksEnabled, cdcEnabled, outbox);

        await service.StartAsync(CancellationToken.None);

        await Task.Delay(200);
        service.ExecuteTask!.IsCompleted.ShouldBeFalse();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bridge_idles_when_webhooks_disabled_and_subscribes_when_enabled()
    {
        var disabledBus = Substitute.For<IGatewayEventBus>();
        var disabled = new WebhookEventBridgeHostedService(
            disabledBus,
            Substitute.For<IWebhookDispatcherService>(),
            NullLogger<WebhookEventBridgeHostedService>.Instance,
            Options.Create(new GatewayOptions { Webhooks = new WebhooksOptions { Enabled = false } }));
        await ((IHostedService)disabled).StartAsync(CancellationToken.None);
        disabledBus.ReceivedCalls().ShouldBeEmpty();

        var enabledBus = Substitute.For<IGatewayEventBus>();
        var enabled = new WebhookEventBridgeHostedService(
            enabledBus,
            Substitute.For<IWebhookDispatcherService>(),
            NullLogger<WebhookEventBridgeHostedService>.Instance,
            Options.Create(new GatewayOptions { Webhooks = new WebhooksOptions { Enabled = true } }));
        await ((IHostedService)enabled).StartAsync(CancellationToken.None);
        enabledBus.ReceivedCalls().ShouldNotBeEmpty();
    }
}
