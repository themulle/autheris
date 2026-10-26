namespace Autheris.Tests.Unit.Governance;

using Autheris.Application.Governance.Policies;
using Shouldly;
using Xunit;

// The cache is process-wide static state; the strict cap assertions need exclusive access.
[Collection("PatternMatcherCache")]
public sealed class PatternMatcherCacheTests
{
    [Fact]
    public void ManyDistinctPatterns_CacheStaysWithinCap()
    {
        for (var i = 0; i < PatternMatcher.MaxCachedPatterns * 2 + 5; i++)
        {
            PatternMatcher.CompilePattern($"cap-test-{i}-*");
            PatternMatcher.CachedPatternCount.ShouldBeLessThanOrEqualTo(PatternMatcher.MaxCachedPatterns);
        }
    }

    [Fact]
    public void AfterEviction_StillMatches()
    {
        for (var i = 0; i < PatternMatcher.MaxCachedPatterns + 1; i++)
        {
            PatternMatcher.CompilePattern($"evict-test-{i}");
        }

        PatternMatcher.IsMatch("evict-test-*", "evict-test-7").ShouldBeTrue();
        PatternMatcher.IsMatch("evict-test-0", "evict-test-1").ShouldBeFalse();
        PatternMatcher.CachedPatternCount.ShouldBeLessThanOrEqualTo(PatternMatcher.MaxCachedPatterns);
    }
}

[CollectionDefinition("PatternMatcherCache", DisableParallelization = true)]
public sealed class PatternMatcherCacheCollection;
