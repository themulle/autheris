namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>Configured subscription ids must be stable: queued rows reference them across restarts and nodes.</summary>
public sealed class WebhookSubscriptionValidationTests
{
    private static GatewayOptions Options(bool enabled, params WebhookSubscription[] subscriptions) => new()
    {
        Webhooks = new WebhooksOptions { Enabled = enabled, Subscriptions = [.. subscriptions] }
    };

    private static WebhookSubscription Sub(string id) => new() { Id = id, TargetUrl = "https://hooks.example.com/in" };

    [Fact]
    public void SubscriptionId_HasNoGeneratedDefault() => new WebhookSubscription().Id.ShouldBe(string.Empty);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyId_FailsStartup(string id) =>
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateWebhookSubscriptions(Options(true, Sub(id))));

    [Fact]
    public void DuplicateId_FailsStartup() =>
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateWebhookSubscriptions(Options(true, Sub("a"), Sub("a"))));

    [Fact]
    public void TooLongId_FailsStartup() =>
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateWebhookSubscriptions(Options(true, Sub(new string('x', 129)))));

    [Fact]
    public void ValidIds_Pass()
    {
        GatewayStartupValidator.ValidateWebhookSubscriptions(Options(true, Sub("a"), Sub("b"), Sub(new string('x', 128))));
    }

    [Fact]
    public void WhenWebhooksDisabled_NothingIsChecked()
    {
        GatewayStartupValidator.ValidateWebhookSubscriptions(Options(false, Sub("")));
    }

    [Fact]
    public void ErrorMessage_NeverEchoesTargetUrl()
    {
        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateWebhookSubscriptions(
            Options(true, new WebhookSubscription { Id = "", TargetUrl = "https://hooks.example.com/in?token=TOPSECRET", Secret = "SECRETVALUE" })));
        ex.Message.ShouldNotContain("TOPSECRET");
        ex.Message.ShouldNotContain("SECRETVALUE");
    }

    [Fact]
    public void PollInterval_IsClamped()
    {
        new WebhooksOptions { PollIntervalSeconds = 0 }.EffectivePollIntervalSeconds.ShouldBe(1);
        new WebhooksOptions { PollIntervalSeconds = 100000 }.EffectivePollIntervalSeconds.ShouldBe(300);
    }
}
