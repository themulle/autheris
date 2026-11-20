#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Modules.Events.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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
/// The events module delivers only what the host enforcer lets through: with the module registered, the CDC stream port and the CDC
/// webhook delivery service decide through the single host <see cref="IStreamRlsPolicyEnforcer"/> (consent, row filter, masking).
/// </summary>
[Trait("Category", "Security")]
public sealed class EventsModuleGovernanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static IStreamRlsPolicyEnforcer RealEnforcer(bool withConsent)
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
        IReadOnlyList<Consent> consents = withConsent
            ?
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
            ]
            : [];
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(consents));

        var epoch = new Autheris.Infrastructure.Cache.EpochValidationService();
        var cache = new Autheris.Infrastructure.Cache.ConsentCacheService(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            epoch, new Autheris.Infrastructure.Messaging.InProcessChannelEventBus());
        var masking = new Autheris.Application.Services.ColumnMaskingProvider(Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "Test-Vault-Key" }
        }));
        var gate = Substitute.For<Autheris.Application.DataSources.IDataSourceTransportGate>();
        return new StreamRlsPolicyEnforcer(
            Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AllowAll(), metadataRepo, masking, epoch, NullLogger<StreamRlsPolicyEnforcer>.Instance, consentRepo,
            new Autheris.Application.Services.ConsentResolutionService(), cache, transportGate: gate);
    }

    private static async Task<Host> LoadModuleOverHost(bool withConsent)
    {
        var store = new InMemoryCloudEventSubscriptionStore();
        var outbox = new RecordingOutbox();
        var enforcer = RealEnforcer(withConsent);
        var audit = Substitute.For<IAuditWriter>();
        audit.RecordBestEffortAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        // Host ports first (the host registers them; the module only resolves them).
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddSingleton<ICdcEventChannel>(new SilentChannel());
        services.AddSingleton<ICloudEventSubscriptionStore>(store);
        services.AddSingleton<ICloudEventTransformer, CloudEventTransformer>();
        services.AddSingleton<IWebhookOutbox>(outbox);
        services.AddSingleton(audit);
        services.AddScoped(_ => enforcer);
        services.AddSingleton(Substitute.For<IWebhookSecretProtector>());
        services.AddSingleton(Substitute.For<Autheris.Application.Events.IGatewayEventBus>());
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        // Host webhook primitives the delivery worker needs (registered by the host in production).
        var webhooks = new WebhooksOptions();
        services.AddSingleton(new WebhookRetryPolicy(webhooks, TimeProvider.System));
        services.AddHttpClient<WebhookDeliveryClient>();
        services.AddEventsModule(new ConfigurationBuilder().Build());

        await store.RegisterSubscriptionAsync(new CloudEventWebhookSubscription(
            "sub-1", "tenant-a", "https://example.test/h", "*", [], "enc:v1:x",
            CreatedBySid: "S-1-5-21-CREATOR", CreatedByGroupSids: ["G-1"], CreatedByRoles: ["Analyst"], CreatedAt: Now));
        return new Host(services.BuildServiceProvider(), outbox, store);
    }

    private static CdcEvent Event() =>
        new("pg_orders_1", Orders, CdcOperation.Insert, "tenant-a", null,
            new Dictionary<string, object?> { ["id"] = 7, ["name"] = "visible", ["ssn"] = "123-45-6789" }, Now);

    [Fact]
    public async Task Cdc_webhook_delivery_applies_row_filter_and_masking_when_loaded_as_module()
    {
        var allowed = await LoadModuleOverHost(withConsent: true);
        var service = allowed.Provider.GetServices<IHostedService>().OfType<CdcWebhookDeliveryService>().Single();
        await service.ProcessEventAsync(Event(), default);

        var row = allowed.Outbox.Rows.ShouldHaveSingleItem();
        row.PayloadJson.ShouldContain("visible");
        row.PayloadJson.ShouldNotContain("123-45-6789");

        // Without the creator's access the row never reaches the outbox.
        var denied = await LoadModuleOverHost(withConsent: false);
        await denied.Provider.GetServices<IHostedService>().OfType<CdcWebhookDeliveryService>().Single().ProcessEventAsync(Event(), default);
        denied.Outbox.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cdc_stream_applies_row_filter_and_masking_when_loaded_as_module()
    {
        var host = await LoadModuleOverHost(withConsent: true);
        var subscription = (await host.Store.ListSubscriptionsAsync("tenant-a")).Single();
        var principal = CdcSubscriptionPrincipal.TryBuild(subscription, new FakeTimeProvider(Now), new CdcWebhooksOptions()).ShouldNotBeNull();

        using var scope = host.Provider.CreateScope();
        var enforcer = scope.ServiceProvider.GetRequiredService<IStreamRlsPolicyEnforcer>();
        var decision = await enforcer.EvaluateAndMaskAsync(Event(), principal, default);

        decision.IsAllowed.ShouldBeTrue();
        decision.MaskedPayload!.ContainsKey("ssn").ShouldBeTrue();
        decision.MaskedPayload["ssn"]?.ToString().ShouldNotBe("123-45-6789");
        // The module registers no enforcer of its own: the host instance is the only one.
        host.Provider.GetServices<IStreamRlsPolicyEnforcer>().Count().ShouldBe(1);

        var denied = await LoadModuleOverHost(withConsent: false);
        using var deniedScope = denied.Provider.CreateScope();
        var deniedDecision = await deniedScope.ServiceProvider.GetRequiredService<IStreamRlsPolicyEnforcer>()
            .EvaluateAndMaskAsync(Event(), principal, default);
        deniedDecision.IsAllowed.ShouldBeFalse();
    }

    private sealed class RecordingOutbox : IWebhookOutbox
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

    private sealed class SilentChannel : ICdcEventChannel
    {
        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed record Host(ServiceProvider Provider, RecordingOutbox Outbox, InMemoryCloudEventSubscriptionStore Store);
}
