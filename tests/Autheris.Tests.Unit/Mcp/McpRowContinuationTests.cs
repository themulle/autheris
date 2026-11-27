namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

public sealed class McpRowContinuationTests
{
    [Fact]
    public void Offer_WithoutScope_IsNoOp()
    {
        Should.NotThrow(() => McpRowContinuation.Offer(_ => "t"));

        using var scope = McpRowContinuation.Begin();
        scope.ResumeAfter.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Offer_IsSetOnce_LaterOfferIgnored()
    {
        using var scope = McpRowContinuation.Begin();

        McpRowContinuation.Offer(null);
        McpRowContinuation.Offer(_ => "first");
        McpRowContinuation.Offer(_ => "second");

        scope.ResumeAfter!(1).ShouldBe("first");
    }

    [Fact]
    public void Dispose_RestoresPreviousScope()
    {
        using var outer = McpRowContinuation.Begin();
        var inner = McpRowContinuation.Begin();
        McpRowContinuation.Offer(_ => "inner");
        inner.Dispose();
        inner.Dispose();

        McpRowContinuation.Offer(_ => "outer");

        outer.ResumeAfter!(1).ShouldBe("outer");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConcurrentScopes_DoNotLeakResumeAcrossCalls()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            using var scope = McpRowContinuation.Begin();
            await Task.Yield();
            McpRowContinuation.Offer(_ => $"call-{i}");
            await Task.Yield();
            return (Expected: $"call-{i}", Actual: scope.ResumeAfter!(1));
        })));

        results.ShouldAllBe(r => r.Expected == r.Actual);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Scope_ClearedAfterExecutorThrows()
    {
        using (var scope = McpRowContinuation.Begin())
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                McpRowContinuation.Offer(_ => "leak");
                await Task.Yield();
                throw new InvalidOperationException("executor failed");
            });
        }

        McpRowContinuation.Offer(_ => "after");
        using var next = McpRowContinuation.Begin();
        next.ResumeAfter.ShouldBeNull();
    }

    [Fact]
    public void ReportPageSizeCapped_WithoutScope_IsNoOp()
    {
        Should.NotThrow(() => McpRowContinuation.ReportPageSizeCapped());

        using var scope = McpRowContinuation.Begin();
        scope.PageSizeCapped.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReportPageSizeCapped_IsReadByScope()
    {
        using var scope = McpRowContinuation.Begin();
        scope.PageSizeCapped.ShouldBeFalse();

        await Task.Yield();
        McpRowContinuation.ReportPageSizeCapped();

        scope.PageSizeCapped.ShouldBeTrue();
        using var other = McpRowContinuation.Begin();
        other.PageSizeCapped.ShouldBeFalse();
    }
}
