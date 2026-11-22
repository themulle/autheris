#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Modules.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// The CDC REST transport (events module) and the GraphQL subscription (core) read the same host enforcer, so one principal sees the
/// same filtered and masked payload on both. Runs without containers.
/// </summary>
[Trait("Category", "Security")]
public sealed class EventsModuleParityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static IStreamRlsPolicyEnforcer HostEnforcer()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { SourceName = Orders.Domain, SchemaName = Orders.Schema, TableName = Orders.TableName },
            Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "name" }, new TableColumn { ColumnName = "ssn" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "REDACT" } }
        }));
        var consentRepo = Substitute.For<IConsentRepository>();
        IReadOnlyList<Consent> consents =
        [
            new Consent
            {
                TableIdentifier = Orders, Effect = ConsentEffect.Allow, GranteeType = GranteeType.User,
                GranteeSid = new Sid("S-1-5-21-CREATOR"), TenantId = new TenantId("tenant-a"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules =
                [
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Mask }
                ]
            }
        ];
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(consents));
        var pdp = Substitute.For<IAccessDecisionPoint>();
        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(TableAccessDecision.Allowed(
                ci.Arg<AccessRequest>().Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        var epoch = new Autheris.Infrastructure.Cache.EpochValidationService();
        var cache = new Autheris.Infrastructure.Cache.ConsentCacheService(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            epoch, new Autheris.Infrastructure.Messaging.InProcessChannelEventBus());
        var masking = new Autheris.Application.Services.ColumnMaskingProvider(Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "Test-Vault-Key" }
        }));
        return new StreamRlsPolicyEnforcer(
            pdp, metadataRepo, masking, epoch, NullLogger<StreamRlsPolicyEnforcer>.Instance, consentRepo,
            new Autheris.Application.Services.ConsentResolutionService(), cache,
            Autheris.Tests.Common.Fakes.NullClientIpResolver.Instance,
            Options.Create(new GatewayOptions()),
            Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance,
            Autheris.Tests.Common.Fakes.DisabledCallerAttributeResolver.Instance,
            TimeProvider.System,
            Substitute.For<Autheris.Application.DataSources.IDataSourceTransportGate>());
    }

    private static CdcEvent Event() =>
        new("pg_orders_1", Orders, CdcOperation.Insert, "tenant-a", null,
            new Dictionary<string, object?> { ["id"] = 7, ["name"] = "visible", ["ssn"] = "123-45-6789" }, Now);

    [Fact]
    public async Task Cdc_rest_and_graphql_subscription_apply_identical_rls_and_masking_with_events_module_loaded()
    {
        var enforcer = HostEnforcer();
        var store = new InMemoryCloudEventSubscriptionStore();
        var outbox = new Outbox();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddSingleton<ICdcEventChannel>(new Channel());
        services.AddSingleton<ICloudEventSubscriptionStore>(store);
        services.AddSingleton<ICloudEventTransformer, CloudEventTransformer>();
        services.AddSingleton<IWebhookOutbox>(outbox);
        services.AddSingleton(Substitute.For<IAuditWriter>());
        services.AddSingleton(Substitute.For<IWebhookSecretProtector>());
        services.AddSingleton(Substitute.For<Autheris.Application.Events.IGatewayEventBus>());
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton(new WebhookRetryPolicy(new WebhooksOptions(), TimeProvider.System));
        services.AddHttpClient<WebhookDeliveryClient>();
        services.AddScoped(_ => enforcer);
        services.AddEventsModule(new ConfigurationBuilder().Build());
        var subscription = new CloudEventWebhookSubscription(
            "sub-1", "tenant-a", "https://example.test/h", "*", [], "enc:v1:x",
            CreatedBySid: "S-1-5-21-CREATOR", CreatedByGroupSids: ["G-1"], CreatedByRoles: ["Analyst"], CreatedAt: Now);
        await store.RegisterSubscriptionAsync(subscription);
        using var provider = services.BuildServiceProvider();

        // REST module side: the delivery service enqueues what the enforcer lets through.
        await provider.GetServices<IHostedService>().OfType<CdcWebhookDeliveryService>().Single().ProcessEventAsync(Event(), default);
        var restPayload = JsonDocument.Parse(outbox.Rows.ShouldHaveSingleItem().PayloadJson).RootElement.GetProperty("data");

        // Core subscription side: the same principal against the same host enforcer.
        var principal = CdcSubscriptionPrincipal.TryBuild(subscription, new FakeTimeProvider(Now), new CdcWebhooksOptions()).ShouldNotBeNull();
        using var scope = provider.CreateScope();
        var decision = await scope.ServiceProvider.GetRequiredService<IStreamRlsPolicyEnforcer>().EvaluateAndMaskAsync(Event(), principal, default);
        decision.IsAllowed.ShouldBeTrue();

        foreach (var (column, value) in decision.MaskedPayload!)
        {
            restPayload.TryGetProperty(column, out var restValue).ShouldBeTrue(column);
            restValue.ToString().ShouldBe(value?.ToString() ?? string.Empty, column);
        }

        restPayload.GetProperty("ssn").ToString().ShouldNotBe("123-45-6789");
    }

    [Fact]
    public void Cdc_graphql_subscription_unchanged_without_events_module()
    {
        // The core substrate (event source, enforcer, channel) has no dependency on the module assembly.
        foreach (var type in new[] { typeof(CdcStreamEventSource), typeof(StreamRlsPolicyEnforcer), typeof(CdcStreamCompatFeed) })
        {
            type.Assembly.GetReferencedAssemblies().Select(a => a.Name).ShouldNotContain("Autheris.Modules.Events");
        }

        typeof(CdcStreamEventSource).GetConstructors().SelectMany(c => c.GetParameters())
            .ShouldNotContain(p => p.ParameterType.Assembly == typeof(EventsModule).Assembly);
    }

    private sealed class Outbox : IWebhookOutbox
    {
        public ConcurrentQueue<WebhookOutboxMessage> Rows { get; } = new();

        public Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default)
        {
            Rows.Enqueue(message);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(string id, string leaseOwner, int statusCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RescheduleAsync(string id, string leaseOwner, DateTimeOffset nextAttemptAt, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeadLetterAsync(string id, string leaseOwner, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountPendingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Channel : ICdcEventChannel
    {
        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
