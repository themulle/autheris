namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Phase-5 review findings of WP-ARC-A-03 (M1, M2 and the minors).</summary>
[Trait("Category", "Security")]
public sealed class CdcReviewFindingsTests
{
    // ---------- M1: registration-time flag is the single source ----------

    private static DefaultHttpContext GuardCtx(bool? flag, bool optionsEnabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<CdcWebhooksOptions>>(Options.Create(new CdcWebhooksOptions { Enabled = optionsEnabled }));
        if (flag is { } f)
        {
            services.AddSingleton(new CdcWebhooksFeatureFlag(f));
        }

        var store = Substitute.For<ICloudEventSubscriptionStore>();
        store.ListSubscriptionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>([]));
        services.AddSingleton(store);
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "GovernanceAdmin"), new Claim("tenant_id", "t")], "Test", ClaimTypes.Name, ClaimTypes.Role)),
            RequestServices = services.BuildServiceProvider()
        };
    }

    [Fact]
    public void Guard_RegistrationFlagOff_Wins_OverLateOptionsOverride() =>
        StreamingCdcEndpoints.GuardCdcWebhooksDisabled(GuardCtx(flag: false, optionsEnabled: true)).ShouldNotBeNull();

    [Fact]
    public void Guard_RegistrationFlagOn_Wins_OverLateOptionsOverride() =>
        StreamingCdcEndpoints.GuardCdcWebhooksDisabled(GuardCtx(flag: true, optionsEnabled: false)).ShouldBeNull();

    [Fact]
    public void Guard_WithoutRegisteredFlag_FallsBackToOptions()
    {
        StreamingCdcEndpoints.GuardCdcWebhooksDisabled(GuardCtx(flag: null, optionsEnabled: true)).ShouldBeNull();
        StreamingCdcEndpoints.GuardCdcWebhooksDisabled(GuardCtx(flag: null, optionsEnabled: false)).ShouldNotBeNull();
    }

    // ---------- M2: tenant-bound, canonical event ids ----------

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private const string Body = """{"op":"c","source":{"schema":"public","table":"orders","name":"srv","ts_ms":1760000000000,"lsn":4242},"after":{"id":1,"v":"x"}}""";

    [Fact]
    public void IdenticalBodies_FromTwoTenants_YieldTwoDistinctEventIds()
    {
        var a = DebeziumCdcParser.Parse(Body, "tenant-a", null);
        var b = DebeziumCdcParser.Parse(Body, "tenant-b", null);

        a.EventId.ShouldNotBe(b.EventId);
        a.EventId.ShouldStartWith("dbz-");
    }

    [Fact]
    public void ReformattedJson_OfTheSameEvent_YieldsTheSameId()
    {
        var pretty = JsonSerializer.Serialize(JsonDocument.Parse(Body).RootElement, Indented);

        DebeziumCdcParser.Parse(pretty, "t", null).EventId.ShouldBe(DebeziumCdcParser.Parse(Body, "t", null).EventId);
    }

    [Fact]
    public void DifferentLsn_YieldsDifferentId() =>
        DebeziumCdcParser.Parse(Body.Replace("4242", "4243"), "t", null).EventId.ShouldNotBe(DebeziumCdcParser.Parse(Body, "t", null).EventId);

    [Fact]
    public void WithoutSourcePosition_ReformattedJsonStillYieldsTheSameId()
    {
        const string noPos = """{"op":"c","source":{"schema":"public","table":"orders","name":"srv"},"after":{"id":1}}""";
        var spaced = """{ "op" : "c", "source" : {"schema":"public","table":"orders","name":"srv"}, "after":{ "id":1 } }""";

        DebeziumCdcParser.Parse(spaced, "t", null).EventId.ShouldBe(DebeziumCdcParser.Parse(noPos, "t", null).EventId);
    }

    [Fact]
    public void WebhookEventId_IsTenantBound()
    {
        var ev = new CdcEvent("same", new TableIdentifier("d", "s", "t"), CdcOperation.Insert, "tenant-a", null, new Dictionary<string, object?> { ["a"] = 1 }, DateTimeOffset.UtcNow);

        CdcWebhookEventId.From(ev).ShouldNotBe(CdcWebhookEventId.From(ev with { TenantId = "tenant-b" }));
        CdcWebhookEventId.From(ev).ShouldBe(CdcWebhookEventId.From(ev));
    }

    // ---------- Minor 4/5: resolver fail-closed and custom header protection ----------

    [Fact]
    public async Task Resolver_UnprefixedSecretFromDurableStore_FailsClosed()
    {
        var sub = new CloudEventWebhookSubscription("s1", "t1", "https://e.test", "*", [], "plaintext-secret");
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        store.GetSubscriptionAsync("t1", "s1", Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<CloudEventWebhookSubscription?>(sub));
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var resolver = new WebhookSubscriptionResolver(Options.Create(new GatewayOptions()), services);
        var row = CloudEventOutboxMessageFactory.Create(sub, new CloudEventEnvelope("1.0", "cdc-1", "/s", "t", DateTimeOffset.UtcNow, "application/json", "t1", new { }), DateTimeOffset.UtcNow, 3);

        await Should.ThrowAsync<WebhookSecretUnavailableException>(async () => await resolver.ResolveAsync(row, default));
    }

    [Fact]
    public async Task Resolver_ProtectedCustomHeaders_AreUnprotected()
    {
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.Unprotect("t1", "s1", "enc:v1:hdr").Returns("""{"X-Api-Key":"k123"}""");
        var sub = new CloudEventWebhookSubscription("s1", "t1", "https://e.test", "*", [], "",
            CustomHeaders: new Dictionary<string, string> { [CdcCustomHeaders.ProtectedKey] = "enc:v1:hdr" });
        var store = new InMemoryCloudEventSubscriptionStore();
        await store.RegisterSubscriptionAsync(sub);
        var services = new ServiceCollection().AddSingleton<ICloudEventSubscriptionStore>(store).AddSingleton(protector).BuildServiceProvider();
        var resolver = new WebhookSubscriptionResolver(Options.Create(new GatewayOptions()), services);
        var row = CloudEventOutboxMessageFactory.Create(sub, new CloudEventEnvelope("1.0", "cdc-1", "/s", "t", DateTimeOffset.UtcNow, "application/json", "t1", new { }), DateTimeOffset.UtcNow, 3);

        var target = await resolver.ResolveAsync(row, default);

        target!.CustomHeaders!["X-Api-Key"].ShouldBe("k123");
    }
}
