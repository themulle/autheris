namespace Autheris.Modules.Events.Tests;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The module registers its workers unconditionally; they read the host feature flags at run time and idle while disabled.</summary>
public sealed class WebhookHostedServiceIdleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static DeliveryHarness Delivery(bool webhooksEnabled, bool cdcEnabled, IWebhookOutbox outbox, TimeProvider? timeProvider = null)
    {
        var effectiveTime = timeProvider ?? TimeProvider.System;
        var webhooks = new WebhooksOptions { Enabled = webhooksEnabled, PollIntervalSeconds = 1 };
        var options = Options.Create(new GatewayOptions { Webhooks = webhooks });
        var policy = new WebhookRetryPolicy(webhooks, effectiveTime);
        var httpClient = new HttpClient();
        var client = new WebhookDeliveryClient(httpClient, options, policy, NullLogger<WebhookDeliveryClient>.Instance);
        var provider = new ServiceCollection().BuildServiceProvider();
        var resolver = new WebhookSubscriptionResolver(options, provider);
        var service = new WebhookDeliveryHostedService(
            outbox, client, resolver, policy, options, NullLogger<WebhookDeliveryHostedService>.Instance,
            timeProvider: effectiveTime,
            cdcWebhooks: Options.Create(new CdcWebhooksOptions { Enabled = cdcEnabled }));
        return new DeliveryHarness(service, httpClient, provider);
    }

    [Fact]
    public async Task Delivery_worker_idles_when_webhooks_and_cdc_webhooks_disabled()
    {
        var outbox = Substitute.For<IWebhookOutbox>();
        using var harness = Delivery(webhooksEnabled: false, cdcEnabled: false, outbox);
        var service = harness.Service;

        await service.StartAsync(CancellationToken.None);

        await service.ExecuteTask!.WaitAsync(TestTimeout);
        service.ExecuteTask.IsCompletedSuccessfully.ShouldBeTrue();
        await outbox.DidNotReceiveWithAnyArgs().ClaimBatchAsync(default, default, default, default!, default);
        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Delivery_worker_runs_when_either_feature_is_enabled(bool webhooksEnabled, bool cdcEnabled)
    {
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = Substitute.For<IWebhookOutbox>();
        outbox.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                claimed.TrySetResult();
                return Task.FromResult<IReadOnlyList<WebhookOutboxMessage>>([]);
            });

        var time = new SignalingTimeProvider();
        using var harness = Delivery(webhooksEnabled, cdcEnabled, outbox, time);
        var service = harness.Service;

        await service.StartAsync(CancellationToken.None);
        await time.NextTimerAsync();
        time.Advance(TimeSpan.FromSeconds(1));

        await claimed.Task.WaitAsync(TestTimeout);
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

    private sealed class DeliveryHarness(WebhookDeliveryHostedService service, HttpClient httpClient, ServiceProvider provider) : IDisposable
    {
        public WebhookDeliveryHostedService Service => service;

        public void Dispose()
        {
            service.Dispose();
            httpClient.Dispose();
            provider.Dispose();
        }
    }

    private sealed class SignalingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _created.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task NextTimerAsync()
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            await _created.Reader.ReadAsync(cts.Token);
        }
    }
}
