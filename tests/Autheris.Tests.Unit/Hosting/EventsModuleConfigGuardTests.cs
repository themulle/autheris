namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Hosting;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

/// <summary>Configuration guards of the events module: removed keys, early key pass with the module enabled, webhook subscriptions.</summary>
[Trait("Category", "Security")]
public sealed class EventsModuleConfigGuardTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Theory]
    [InlineData("Gateway:CdcWebhooks:Enabled")]
    [InlineData("Gateway:CdcWebhooks:MaxSubscriptionsPerTenant")]
    [InlineData("Gateway:CdcWebhooks")]
    public void Removed_cdc_webhooks_and_openlineage_keys_fail_with_new_path_message(string key)
    {
        // The CdcWebhooks half; the OpenLineage keys move with the lineage module.
        var config = Config((key, "true"));

        var early = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(config));
        early.Message.ShouldContain("Gateway:CdcWebhooks");
        early.Message.ShouldContain("Gateway:Events:CdcWebhooks");

        // The removed-key message wins even when the events module is loaded (the old section is never accepted).
        var loaded = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.Validate(config, ["events"], ["Gateway:Events"]));
        loaded.Message.ShouldContain("Gateway:Events:CdcWebhooks");
        loaded.Message.ShouldNotContain("true");
    }

    [Fact]
    public void Events_keys_pass_the_early_pass_only_when_the_events_module_is_enabled()
    {
        var withModule = Config(
            ("Gateway:Modules:Enabled:0", "events"),
            ("Gateway:Events:CdcWebhooks:Enabled", "true"),
            ("Gateway:Events:CdcWebhooks:MaxSubscriptionsPerTenant", "5"));
        Should.NotThrow(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(withModule));

        var withoutModule = Config(("Gateway:Events:CdcWebhooks:Enabled", "true"));
        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(withoutModule));
        ex.Message.ShouldContain("module events is not installed");

        // Enabling another module does not accept the events keys.
        var otherModule = Config(
            ("Gateway:Modules:Enabled:0", "lakehouse"),
            ("Gateway:Events:CdcWebhooks:Enabled", "true"));
        Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(otherModule));
    }

    [Fact]
    public void Early_pass_with_the_module_enabled_still_rejects_unknown_core_keys()
    {
        var config = Config(("Gateway:Modules:Enabled:0", "events"), ("Gateway:NoSuchSection:Value", "x"));

        Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(config))
            .Message.ShouldContain("Gateway:NoSuchSection");
    }

    [Fact]
    public void Webhook_subscriptions_without_events_module_fail_startup()
    {
        var options = new GatewayOptions
        {
            Webhooks = new WebhooksOptions { Subscriptions = [new WebhookSubscription { Id = "hook", TargetUrl = "https://93.184.216.34/h" }] }
        };

        var without = new ModuleLoadResult([]);
        var ex = Should.Throw<InvalidOperationException>(() =>
            GatewayServiceCollectionExtensions.ThrowIfWebhookSubscriptionsWithoutEventsModule(options, without));
        ex.Message.ShouldContain("Gateway:Webhooks:Subscriptions");
        ex.Message.ShouldContain("events");

        // No subscriptions: nothing to deliver, no module needed.
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ThrowIfWebhookSubscriptionsWithoutEventsModule(new GatewayOptions(), without));
    }
}
