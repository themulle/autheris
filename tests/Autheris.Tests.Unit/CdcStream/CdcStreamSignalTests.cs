using System.Text.Json;
using Autheris.Application.State;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: data-free wake-up signal (AC-14, D-A12).</summary>
public sealed class CdcStreamSignalTests
{
    private static readonly CdcStreamId Id = new("0123456789abcdef0123456789abcdef");

    [Fact]
    public void PayloadHasNoRowDataTenantOrTable()
    {
        var json = JsonSerializer.Serialize(new CdcStreamAdvanced(Id.Value, 42));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ShouldBe(["HeadOffset", "StreamId"]);
        typeof(CdcStreamAdvanced).GetProperties().Select(p => p.Name).OrderBy(n => n).ShouldBe(["HeadOffset", "StreamId"]);
    }

    [Fact]
    public async Task Notify_PublishesOnTheDedicatedChannel_NotOnTheCdcEventChannel()
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        var signal = new CdcStreamSignal(state, NullLogger<CdcStreamSignal>.Instance);
        await signal.NotifyAdvancedAsync(Id, 7);
        await state.Received(1).PublishEventAsync(CdcStreamSignal.Channel, Arg.Is<CdcStreamAdvanced>(m => m.StreamId == Id.Value && m.HeadOffset == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notify_IsBestEffort_WhenThePublishFails()
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.PublishEventAsync(Arg.Any<string>(), Arg.Any<CdcStreamAdvanced>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("redis down"));
        await new CdcStreamSignal(state, NullLogger<CdcStreamSignal>.Instance).NotifyAdvancedAsync(Id, 1);
    }

    [Fact]
    public async Task Subscribe_DeliversToHandler_AndIgnoresMalformedIds()
    {
        var state = new InMemoryClusterStateProvider();
        var signal = new CdcStreamSignal(state, NullLogger<CdcStreamSignal>.Instance);
        var seen = new List<(string, long)>();
        await using var sub = signal.Subscribe((id, head) => { seen.Add((id.Value, head)); return ValueTask.CompletedTask; });

        await signal.NotifyAdvancedAsync(Id, 5);
        await state.PublishEventAsync(CdcStreamSignal.Channel, new CdcStreamAdvanced("not-an-id", 9));

        seen.ShouldBe([(Id.Value, 5L)]);
    }

    [Fact]
    public async Task Subscribe_HandlerFailure_IsContained()
    {
        var state = new InMemoryClusterStateProvider();
        var signal = new CdcStreamSignal(state, NullLogger<CdcStreamSignal>.Instance);
        await using var sub = signal.Subscribe((_, _) => throw new InvalidOperationException("boom"));
        await signal.NotifyAdvancedAsync(Id, 5);
    }
}
