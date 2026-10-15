namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebhookSubscriptionResolverTests
{
    private static WebhookOutboxMessage Row(string tenant = "t1", string sub = "s1") =>
        CloudEventOutboxMessageFactory.Create(
            new CloudEventWebhookSubscription(sub, tenant, "https://e.test", "*", [], "x"),
            new CloudEventEnvelope("1.0", "cdc-1", "/src", "autheris.cdc.insert", DateTimeOffset.UtcNow, "application/json", tenant, new { a = 1 }),
            DateTimeOffset.UtcNow, 3);

    private static async Task<(WebhookSubscriptionResolver Resolver, IWebhookSecretProtector Protector)> Setup(string storedSecret, bool withProtector = true)
    {
        var store = new InMemoryCloudEventSubscriptionStore();
        await store.RegisterSubscriptionAsync(new CloudEventWebhookSubscription("s1", "t1", "https://e.test/h", "*", [], storedSecret));
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.Unprotect("t1", "s1", "enc:v1:abc").Returns("plain-secret");
        var services = new ServiceCollection();
        services.AddSingleton<ICloudEventSubscriptionStore>(store);
        if (withProtector)
        {
            services.AddSingleton(protector);
        }

        return (new WebhookSubscriptionResolver(Options.Create(new GatewayOptions()), services.BuildServiceProvider()), protector);
    }

    [Fact]
    public async Task CloudEvent_ProtectedSecret_IsUnprotectedForSigning()
    {
        var (resolver, _) = await Setup("enc:v1:abc");

        var target = await resolver.ResolveAsync(Row(), default);

        target.ShouldNotBeNull();
        target.Secret.ShouldBe("plain-secret");
    }

    [Fact]
    public async Task CloudEvent_WrongTenant_ReturnsNull()
    {
        var (resolver, _) = await Setup("enc:v1:abc");

        (await resolver.ResolveAsync(Row(tenant: "t2"), default)).ShouldBeNull();
    }

    [Fact]
    public async Task CloudEvent_UnprotectFailure_Throws_NeverSignsWithCiphertext()
    {
        var (resolver, protector) = await Setup("enc:v1:abc");
        protector.Unprotect(default!, default!, default!).ReturnsForAnyArgs(_ => throw new WebhookSecretUnavailableException());

        await Should.ThrowAsync<WebhookSecretUnavailableException>(async () => await resolver.ResolveAsync(Row(), default));
    }

    [Fact]
    public async Task CloudEvent_ProtectedSecretWithoutProtector_Throws()
    {
        var (resolver, _) = await Setup("enc:v1:abc", withProtector: false);

        await Should.ThrowAsync<WebhookSecretUnavailableException>(async () => await resolver.ResolveAsync(Row(), default));
    }

    [Fact]
    public async Task CloudEvent_EmptySecret_ResolvesToEmpty()
    {
        var (resolver, _) = await Setup(string.Empty);

        (await resolver.ResolveAsync(Row(), default))!.Secret.ShouldBeEmpty();
    }
}
