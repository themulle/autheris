namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;
using Xunit;

/// <summary>
/// INF-03: table components containing ':' must not collide on the L1 consent cache key.
/// </summary>
public sealed class Inf03ConsentCacheKeyAmbiguityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task INF_03_ColonInTableComponents_DoesNotShareL1Entry()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        await using var bus = new InProcessChannelEventBus();
        using var cache = new ConsentCacheService(memoryCache, new EpochValidationService(), bus);
        var user = new Sid("S-1-5-21-INF03-USER");
        var first = new TableIdentifier("a:b", "c", "t");
        var second = new TableIdentifier("a", "b:c", "t");

        await cache.SetCachedDecisionAsync(
            Autheris.Tests.Common.TestTenants.Default, user, first, TableAccessDecision.Allowed(first, new Dictionary<string, ColumnAccessLevel>()), TimeSpan.FromMinutes(5));

        (await cache.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user, first)).ShouldNotBeNull();
        (await cache.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user, second)).ShouldBeNull();
    }
}
