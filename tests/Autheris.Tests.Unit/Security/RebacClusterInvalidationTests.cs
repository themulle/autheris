using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// RR-L4-04: A revocation on one replica must invalidate the decision caches of all replicas (shared event bus).
/// </summary>
public sealed class RebacClusterInvalidationTests
{
    [Fact]
    public async Task RR_L4_04_Revocation_OnReplicaA_InvalidatesCachedAllow_OnReplicaB()
    {
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true, CacheTtlSeconds = 3600 } });
        await using var bus = new InProcessChannelEventBus();

        // Shared tuple store (stands in for RedisRebacStore), two evaluators = two replicas
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var replicaA = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance, bus);
        var replicaB = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance, bus);

        var tuple = new RebacTuple("tenant-a", "S-1-5-21-ALICE", "viewer", "table:finance.invoices");
        await store.AddTupleAsync(tuple);
        var check = new RebacCheckRequest("tenant-a", "S-1-5-21-ALICE", "viewer", "table:finance.invoices");

        (await replicaB.CheckAsync(check)).Allowed.ShouldBeTrue(); // B caches ALLOW

        await store.DeleteTupleAsync(tuple);
        await replicaA.InvalidateTenantCacheAsync("tenant-a");                 // revocation handled on A

        var allowedOnB = true;
        try
        {
            await WaitHelper.WaitUntil(async () => !(allowedOnB = (await replicaB.CheckAsync(check)).Allowed), TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // fall through: the assertion below reports the stale ALLOW
        }

        allowedOnB.ShouldBeFalse();
    }
}
