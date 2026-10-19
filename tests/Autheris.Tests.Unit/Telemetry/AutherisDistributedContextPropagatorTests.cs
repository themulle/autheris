namespace Autheris.Tests.Unit.Telemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autheris.Infrastructure.Telemetry;
using Shouldly;
using Xunit;

public sealed class AutherisDistributedContextPropagatorTests
{
    private static readonly DistributedContextPropagator Propagator = AutherisDistributedContextPropagator.Instance;

    private static readonly DistributedContextPropagator.PropagatorGetterCallback DictGetter =
        (object? c, string key, out string? value, out IEnumerable<string>? values) =>
        {
            values = null;
            ((Dictionary<string, string>)c!).TryGetValue(key, out value);
        };

    [Fact]
    public void Fields_ContainsOnlyTraceParent()
    {
        Propagator.Fields.ShouldBe(["traceparent"]);
    }

    [Fact]
    public void Inject_InjectsTraceParentHeader_WithoutTracestateOrBaggage()
    {
        using var source = new ActivitySource("Autheris.Test.Propagator");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("test-span")!;
        activity.ShouldNotBeNull();

        var carrier = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Propagator.Inject(activity, carrier, (c, key, value) => ((Dictionary<string, string>)c!)[key] = value);

        carrier.ShouldContainKey("traceparent");
        carrier["traceparent"].ShouldBe($"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-01");
        carrier.ShouldNotContainKey("tracestate");
        carrier.ShouldNotContainKey("baggage");
    }

    [Fact]
    public void ExtractTraceIdAndState_ValidTraceParent_ExtractsSuccessfully()
    {
        var carrier = new Dictionary<string, string>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["tracestate"] = "rojo=1,congo=2"
        };

        Propagator.ExtractTraceIdAndState(
            carrier,
            DictGetter,
            out var traceParent,
            out var traceState);

        traceParent.ShouldBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        traceState.ShouldBe("rojo=1,congo=2");
    }

    [Theory]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")] // All-zero trace ID
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")] // All-zero span ID
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")] // Unsupported version
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e473-00f067aa0ba902b7-01")]  // Too short
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-extra")] // Too long
    [InlineData("not-a-valid-traceparent")]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractTraceIdAndState_InvalidTraceParent_DropsHeader(string invalidTraceParent)
    {
        var carrier = new Dictionary<string, string>
        {
            ["traceparent"] = invalidTraceParent
        };

        Propagator.ExtractTraceIdAndState(
            carrier,
            DictGetter,
            out var traceParent,
            out var traceState);

        traceParent.ShouldBeNull();
        traceState.ShouldBeNull();
    }

    [Fact]
    public void ExtractTraceIdAndState_TraceStateTooLongOrTooManyMembers_DropsTraceState()
    {
        // > 512 bytes
        var longState = new string('a', 513);
        var carrierLong = new Dictionary<string, string>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["tracestate"] = longState
        };

        Propagator.ExtractTraceIdAndState(
            carrierLong,
            DictGetter,
            out var traceParent1,
            out var traceState1);

        traceParent1.ShouldBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        traceState1.ShouldBeNull();

        // > 32 members
        var members = new List<string>();
        for (var i = 0; i < 33; i++)
        {
            members.Add($"k{i}=v{i}");
        }
        var manyMembersState = string.Join(",", members);

        var carrierMany = new Dictionary<string, string>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["tracestate"] = manyMembersState
        };

        Propagator.ExtractTraceIdAndState(
            carrierMany,
            DictGetter,
            out var traceParent2,
            out var traceState2);

        traceParent2.ShouldBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        traceState2.ShouldBeNull();
    }

    [Fact]
    public void ExtractBaggage_AlwaysReturnsNull_DroppingBaggage()
    {
        var carrier = new Dictionary<string, string>
        {
            ["baggage"] = "userId=alice,role=admin"
        };

        var baggage = Propagator.ExtractBaggage(carrier, DictGetter);

        baggage.ShouldBeNull();
    }
}
