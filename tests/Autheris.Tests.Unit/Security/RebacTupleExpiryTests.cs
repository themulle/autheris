namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Rebac;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>WP-SC-16 / SEC-SC-20: a ReBAC tuple past its ExpiresAt is absent for the stores and the evaluator (fail closed).</summary>
public sealed class RebacTupleExpiryTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly InMemoryRebacStore _store = new(NullLogger<InMemoryRebacStore>.Instance);
    private readonly IOptions<GatewayOptions> _options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true, CacheTtlSeconds = 60 } });

    private ZanzibarRebacEvaluator Evaluator(Autheris.Application.Security.Rebac.Interfaces.IRebacStore? store = null) =>
        new(store ?? _store, _options, NullLogger<ZanzibarRebacEvaluator>.Instance);

    [Fact]
    public async Task SEC_SC_20_InMemoryStore_DoesNotReturnExpiredTuples()
    {
        await _store.AddTupleAsync(new RebacTuple("t", "a", "viewer", "doc:1") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await _store.AddTupleAsync(new RebacTuple("t", "b", "viewer", "doc:1") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        await _store.AddTupleAsync(new RebacTuple("t", "c", "viewer", "doc:1"));

        var tuples = await _store.GetTuplesAsync("t", obj: "doc:1");

        tuples.Select(x => x.User).ShouldBe(["b", "c"], ignoreOrder: true);
    }

    [Fact]
    public async Task SEC_SC_20_Evaluator_DeniesAnExpiredTuple_EvenWhenTheStoreReturnsIt()
    {
        var expired = new RebacTuple("t", "a", "viewer", "doc:1") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var leaky = Substitute.For<Autheris.Application.Security.Rebac.Interfaces.IRebacStore>();
        leaky.GetTuplesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RebacTuple>>([expired]));

        (await Evaluator(leaky).CheckAsync(new RebacCheckRequest("t", "a", "viewer", "doc:1"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_SC_20_Evaluator_PositiveDecisionIsNotCachedPastTheTupleExpiry()
    {
        await _store.AddTupleAsync(new RebacTuple("t", "a", "viewer", "doc:1") { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(400) });
        var evaluator = Evaluator();
        var request = new RebacCheckRequest("t", "a", "viewer", "doc:1");

        (await evaluator.CheckAsync(request)).Allowed.ShouldBeTrue();
        await Task.Delay(600);

        (await evaluator.CheckAsync(request)).Allowed.ShouldBeFalse("the 60 s decision cache must not outlive the tuple");
    }

    [Fact]
    public async Task SEC_SC_20_RedisStore_DoesNotReturnExpiredTuples()
    {
        var expired = System.Text.Json.JsonSerializer.Serialize(new RebacTuple("t", "a", "viewer", "doc:1") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, Json);
        var live = System.Text.Json.JsonSerializer.Serialize(new RebacTuple("t", "b", "viewer", "doc:1") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }, Json);
        var db = Substitute.For<IDatabase>();
        db.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns([new HashEntry("a", expired), new HashEntry("b", live)]);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        var store = new RedisRebacStore(mux, Options.Create(new GatewayOptions()), NullLogger<RedisRebacStore>.Instance);

        var tuples = await store.GetTuplesAsync("t");

        tuples.Select(x => x.User).ShouldBe(["b"]);
    }
}
