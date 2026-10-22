namespace Autheris.Tests.Unit.Databricks;

using System;
using Autheris.Infrastructure.Databricks;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class DatabricksTokenCacheBoundTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExpiredEntries_AreRemoved_NotOnlySkipped()
    {
        var fakeTime = new FakeTimeProvider(InitialTime);
        using var cache = new DatabricksTokenCache(maxEntries: 100, timeProvider: fakeTime);

        var expiresAt = InitialTime.AddMinutes(5);
        cache.Set("adb.corp.net", "client-1", "user-hash-1", "token-abc-123", expiresAt);

        cache.EntryCount.ShouldBe(1);

        // Advance clock past expiration
        fakeTime.Advance(TimeSpan.FromMinutes(6));

        // Read attempt: since it's expired, TryGet returns false and entry is evicted
        var found = cache.TryGet("adb.corp.net", "client-1", "user-hash-1", fakeTime.GetUtcNow(), out var token);
        found.ShouldBeFalse();
        token.ShouldBeEmpty();

        // Expired entry is removed from cache, not just skipped
        cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    public void EntryCount_NeverExceedsSizeLimit()
    {
        var fakeTime = new FakeTimeProvider(InitialTime);
        const int sizeLimit = 10;
        using var cache = new DatabricksTokenCache(maxEntries: sizeLimit, timeProvider: fakeTime);

        // Add 25 entries into a cache capped at 10
        for (int i = 0; i < 25; i++)
        {
            cache.Set("adb.corp.net", "client-1", $"user-hash-{i}", $"token-{i}", InitialTime.AddHours(1));
            cache.EntryCount.ShouldBeLessThanOrEqualTo(sizeLimit);
        }

        cache.EntryCount.ShouldBeLessThanOrEqualTo(sizeLimit);
    }

    [Fact]
    public void Evict_RemovesAllEntriesForHostAndClient()
    {
        var fakeTime = new FakeTimeProvider(InitialTime);
        using var cache = new DatabricksTokenCache(maxEntries: 50, timeProvider: fakeTime);

        // Add several user entries for (adb.corp.net, client-1)
        cache.Set("adb.corp.net", "client-1", "user-1", "token-1", InitialTime.AddHours(1));
        cache.Set("adb.corp.net", "client-1", "user-2", "token-2", InitialTime.AddHours(1));
        cache.Set("adb.corp.net", "client-1", "user-3", "token-3", InitialTime.AddHours(1));

        // Add an entry for a different client
        cache.Set("adb.corp.net", "client-2", "user-1", "token-other", InitialTime.AddHours(1));

        cache.EntryCount.ShouldBe(4);

        // Evict all tokens for (adb.corp.net, client-1)
        cache.Evict("adb.corp.net", "client-1");

        cache.TryGet("adb.corp.net", "client-1", "user-1", fakeTime.GetUtcNow(), out _).ShouldBeFalse();
        cache.TryGet("adb.corp.net", "client-1", "user-2", fakeTime.GetUtcNow(), out _).ShouldBeFalse();
        cache.TryGet("adb.corp.net", "client-1", "user-3", fakeTime.GetUtcNow(), out _).ShouldBeFalse();

        // client-2 is unaffected
        cache.TryGet("adb.corp.net", "client-2", "user-1", fakeTime.GetUtcNow(), out var otherToken).ShouldBeTrue();
        otherToken.ShouldBe("token-other");

        cache.EntryCount.ShouldBe(1);
    }
}
