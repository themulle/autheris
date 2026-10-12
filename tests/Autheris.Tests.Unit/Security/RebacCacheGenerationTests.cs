namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class RebacCacheGenerationTests
{
    private readonly InMemoryRebacStore _store = new(NullLogger<InMemoryRebacStore>.Instance);
    private readonly IOptions<GatewayOptions> _options = Options.Create(new GatewayOptions
    {
        Rebac = new RebacOptions { Enabled = true, CacheTtlSeconds = 60 }
    });

    [Fact]
    public async Task EventBus_ConnectionRestored_EvictsDecisionCache()
    {
        var bus = Substitute.For<IEventBus>();

        var evaluator = new ZanzibarRebacEvaluator(
            _store,
            _options,
            NullLogger<ZanzibarRebacEvaluator>.Instance,
            eventBus: bus);

        await _store.AddTupleAsync(new RebacTuple("tenant1", "user:alice", "viewer", "doc:1"));

        var request = new RebacCheckRequest("tenant1", "user:alice", "viewer", "doc:1");

        // First check populates cache
        var res1 = await evaluator.CheckAsync(request);
        res1.Allowed.ShouldBeTrue();

        // Mutate store directly without invalidation:
        await _store.DeleteTupleAsync(new RebacTuple("tenant1", "user:alice", "viewer", "doc:1"));

        // Second check hits cache (still allowed because cached)
        var res2 = await evaluator.CheckAsync(request);
        res2.Allowed.ShouldBeTrue();

        // Fire ConnectionRestored event on event bus
        bus.ConnectionRestored += Raise.Event<Action>();

        // Third check must re-query store because cache was cleared on reconnect (now denied because deleted)
        var res3 = await evaluator.CheckAsync(request);
        res3.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidateTenantCache_IncrementsGeneration_AndPublishesInvalidation()
    {
        var bus = Substitute.For<IEventBus>();

        var evaluator = new ZanzibarRebacEvaluator(
            _store,
            _options,
            NullLogger<ZanzibarRebacEvaluator>.Instance,
            eventBus: bus);

        await evaluator.InvalidateTenantCacheAsync("tenant1");

        // Should increment broker generations
        await bus.Received(1).IncrementCounterAsync(ZanzibarRebacEvaluator.GetTenantGenerationKey("tenant1"), Arg.Any<CancellationToken>());
        await bus.Received(1).IncrementCounterAsync(ZanzibarRebacEvaluator.GenerationKey, Arg.Any<CancellationToken>());

        // Should publish to invalidation channel
        await bus.Received(1).PublishAsync(ZanzibarRebacEvaluator.InvalidationChannel, "tenant1", Arg.Any<CancellationToken>());
    }
}
