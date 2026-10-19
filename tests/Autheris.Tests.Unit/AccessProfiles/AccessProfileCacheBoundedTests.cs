namespace Autheris.Tests.Unit.AccessProfiles;

using System.Threading.Tasks;
using Autheris.Application.Policy.Services;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Autheris.Application.State;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>WP-PF-06: entry sizes use the same unit (bytes) as the shared L1 <c>SizeLimit</c>, so the cache really is bounded.</summary>
public sealed class AccessProfileCacheBoundedTests
{
    [Fact]
    public async Task ManySubjects_StayWithinTheByteSizeLimit_OfTheSharedMemoryCache()
    {
        const long limitBytes = 64 * 1024;
        var l1 = new MemoryCache(new MemoryCacheOptions { SizeLimit = limitBytes, TrackStatistics = true });
        var cache = new AccessProfileCache(
            new InMemoryAccessProfileRepository(),
            new InMemoryClusterStateProvider(),
            l1,
            logger: NullLogger<AccessProfileCache>.Instance);

        for (var i = 0; i < 2_000; i++)
        {
            await cache.GetProfilesAsync(new TenantId("t"), "subject-" + i);
        }

        l1.GetCurrentStatistics()!.CurrentEstimatedSize!.Value.ShouldBeLessThanOrEqualTo(limitBytes);
        l1.Count.ShouldBeLessThan(2_000);
    }
}
