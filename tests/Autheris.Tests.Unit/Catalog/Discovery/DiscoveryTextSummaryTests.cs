namespace Autheris.Tests.Unit.Catalog.Discovery;

using Autheris.Application.Catalog.Discovery;
using Shouldly;
using Xunit;

public sealed class DiscoveryTextSummaryTests
{
    [Fact]
    public void Summarize_Null_ReturnsNull() =>
        DiscoveryTextSanitizer.Summarize(null).ShouldBeNull();

    [Fact]
    public void Summarize_ShortText_IsReturnedUnchanged() =>
        DiscoveryTextSanitizer.Summarize("Customer master data").ShouldBe("Customer master data");

    [Fact]
    public void Summarize_MultipleSentences_ReturnsFirstSentence() =>
        DiscoveryTextSanitizer.Summarize("First sentence. Second sentence with detail.").ShouldBe("First sentence.");

    [Fact]
    public void Summarize_LongText_IsCutAndMarked()
    {
        var text = new string('a', 1000);

        var summary = DiscoveryTextSanitizer.Summarize(text, 100);

        summary.ShouldNotBeNull();
        summary.ShouldEndWith(DiscoveryTextSanitizer.SummaryCutMarker);
        summary.Length.ShouldBe(100);
    }

    [Fact]
    public void Summarize_NeverSplitsASurrogatePair()
    {
        var text = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 50));

        var summary = DiscoveryTextSanitizer.Summarize(text, 10);

        summary.ShouldNotBeNull();
        summary.ShouldEndWith(DiscoveryTextSanitizer.SummaryCutMarker);
        summary[..^1].Length.ShouldBe(18);
        char.IsHighSurrogate(summary[^2]).ShouldBeFalse();
    }
}
