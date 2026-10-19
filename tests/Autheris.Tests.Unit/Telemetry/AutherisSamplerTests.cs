namespace Autheris.Tests.Unit.Telemetry;

using System;
using System.Diagnostics;
using Autheris.Infrastructure.Telemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

public sealed class AutherisSamplerTests
{
    private static readonly ActivityTraceId SampleTraceId = ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736".AsSpan());
    private static readonly ActivitySpanId SampleSpanId = ActivitySpanId.CreateFromString("00f067aa0ba902b7".AsSpan());

    [Fact]
    public void ShouldSample_LocalParentSampled_ReturnsRecordAndSample()
    {
        var sampler = new AutherisSampler(ratio: 0.0, trustParentSampling: false);
        var parentContext = new ActivityContext(SampleTraceId, SampleSpanId, ActivityTraceFlags.Recorded, isRemote: false);
        var parameters = new SamplingParameters(parentContext, SampleTraceId, "test-op", ActivityKind.Server);

        var result = sampler.ShouldSample(parameters);

        result.Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void ShouldSample_LocalParentNotSampled_ReturnsDrop()
    {
        var sampler = new AutherisSampler(ratio: 1.0, trustParentSampling: false);
        var parentContext = new ActivityContext(SampleTraceId, SampleSpanId, ActivityTraceFlags.None, isRemote: false);
        var parameters = new SamplingParameters(parentContext, SampleTraceId, "test-op", ActivityKind.Server);

        var result = sampler.ShouldSample(parameters);

        result.Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Fact]
    public void ShouldSample_UntrustedRemoteParentSampled_AppliesRatioInsteadOfTrustingParent()
    {
        // With ratio 0.0 and trustParentSampling=false, even if remote parent has Recorded flag, it is dropped.
        var sampler = new AutherisSampler(ratio: 0.0, trustParentSampling: false);
        var parentContext = new ActivityContext(SampleTraceId, SampleSpanId, ActivityTraceFlags.Recorded, isRemote: true);
        var parameters = new SamplingParameters(parentContext, SampleTraceId, "test-op", ActivityKind.Server);

        var result = sampler.ShouldSample(parameters);

        result.Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Fact]
    public void ShouldSample_TrustedRemoteParentSampled_HonorsParentSampling()
    {
        var sampler = new AutherisSampler(ratio: 0.0, trustParentSampling: true);
        var parentContext = new ActivityContext(SampleTraceId, SampleSpanId, ActivityTraceFlags.Recorded, isRemote: true);
        var parameters = new SamplingParameters(parentContext, SampleTraceId, "test-op", ActivityKind.Server);

        var result = sampler.ShouldSample(parameters);

        result.Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void ShouldSample_TrustedRemoteParentNotSampled_HonorsParentDrop()
    {
        var sampler = new AutherisSampler(ratio: 1.0, trustParentSampling: true);
        var parentContext = new ActivityContext(SampleTraceId, SampleSpanId, ActivityTraceFlags.None, isRemote: true);
        var parameters = new SamplingParameters(parentContext, SampleTraceId, "test-op", ActivityKind.Server);

        var result = sampler.ShouldSample(parameters);

        result.Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Fact]
    public void ShouldSample_RootTrace_AppliesRatioSampler()
    {
        var samplerAlways = new AutherisSampler(ratio: 1.0, trustParentSampling: false);
        var samplerNever = new AutherisSampler(ratio: 0.0, trustParentSampling: false);
        var parameters = new SamplingParameters(default, SampleTraceId, "test-op", ActivityKind.Server);

        samplerAlways.ShouldSample(parameters).Decision.ShouldBe(SamplingDecision.RecordAndSample);
        samplerNever.ShouldSample(parameters).Decision.ShouldBe(SamplingDecision.Drop);
    }
}
