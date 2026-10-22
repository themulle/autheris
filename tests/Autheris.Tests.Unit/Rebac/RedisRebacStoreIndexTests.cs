namespace Autheris.Tests.Unit.Rebac;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
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

public sealed class RedisRebacStoreIndexTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetByObjectRelation_DoesNotCallHashGetAll()
    {
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var live = JsonSerializer.Serialize(new RebacTuple("t1", "user:alice", "viewer", "doc:1"), Json);
        var field = RedisRebacStore.BuildFieldKey("user:alice", "viewer", "doc:1");

        db.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { field }));
        db.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { live }));

        var store = new RedisRebacStore(mux, Options.Create(new GatewayOptions()), NullLogger<RedisRebacStore>.Instance);

        var result = await store.GetTuplesAsync("t1", user: null, relation: "viewer", obj: "doc:1");

        result.Count.ShouldBe(1);
        result[0].User.ShouldBe("user:alice");

        // INF-15: SMEMBERS and HMGET were used, HashGetAllAsync was NEVER called!
        await db.Received(1).SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.Received(1).HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void FieldKeysWithHashAndAt_DoNotCollide()
    {
        // Tuple 1: user "a#b", relation "c", obj "d"
        var field1 = RedisRebacStore.BuildFieldKey("a#b", "c", "d");

        // Tuple 2: user "a", relation "b#c", obj "d"
        var field2 = RedisRebacStore.BuildFieldKey("a", "b#c", "d");

        // With length-prefixing, they must NOT collide
        field1.ShouldNotBe(field2);
        field1.ShouldBe("3:a#b|1:c|1:d");
        field2.ShouldBe("1:a|3:b#c|1:d");
    }

    [Fact]
    public async Task Delete_RemovesIndexMembers()
    {
        var db = Substitute.For<IDatabase>();
        var tran = Substitute.For<ITransaction>();
        tran.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(Task.FromResult(true));
        tran.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        db.CreateTransaction(Arg.Any<object>()).Returns(tran);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var store = new RedisRebacStore(mux, Options.Create(new GatewayOptions()), NullLogger<RedisRebacStore>.Instance);
        var tuple = new RebacTuple("t1", "user:bob", "editor", "doc:99");

        var deleted = await store.DeleteTupleAsync(tuple);

        deleted.ShouldBeTrue();

        // Index sets are removed inside the transaction
        await tran.Received(1).HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        await tran.Received(2).SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        await tran.Received(1).ExecuteAsync(Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ExpiredTuple_AbsentViaIndexPath()
    {
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var expired = JsonSerializer.Serialize(new RebacTuple("t1", "user:alice", "viewer", "doc:1")
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        }, Json);
        var field = RedisRebacStore.BuildFieldKey("user:alice", "viewer", "doc:1");

        db.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { field }));
        db.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { expired }));

        var store = new RedisRebacStore(mux, Options.Create(new GatewayOptions()), NullLogger<RedisRebacStore>.Instance);

        var result = await store.GetTuplesAsync("t1", user: null, relation: "viewer", obj: "doc:1");

        result.ShouldBeEmpty();
    }
}

public sealed class ZanzibarRebacEvaluatorMemoTests
{
    [Fact]
    public async Task SameLookupWithinCheck_HitsStoreOnce()
    {
        var store = Substitute.For<IRebacStore>();
        var tuple = new RebacTuple("t1", "alice", "viewer", "doc:1");
        store.GetTuplesAsync("t1", "alice", "viewer", "doc:1", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RebacTuple>>([tuple]));

        var options = new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                Enabled = true,
                MaxTraversalDepth = 5,
                CacheTtlSeconds = 0, // Disable L2 cache so memo inside single check is isolated
                PerCheckMemoMaxEntries = 256
            }
        };

        var evaluator = new ZanzibarRebacEvaluator(store, Options.Create(options), NullLogger<ZanzibarRebacEvaluator>.Instance);

        // Run check
        var result = await evaluator.CheckAsync(new RebacCheckRequest("t1", "alice", "viewer", "doc:1"));
        result.Allowed.ShouldBeTrue();

        // Exactly 1 store call was made for this direct lookup during the evaluation
        await store.Received(1).GetTuplesAsync("t1", "alice", "viewer", "doc:1", Arg.Any<CancellationToken>());
    }
}
