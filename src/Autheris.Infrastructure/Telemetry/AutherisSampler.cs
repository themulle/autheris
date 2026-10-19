namespace Autheris.Infrastructure.Telemetry;

using System;
using System.Diagnostics;
using OpenTelemetry.Trace;

/// <summary>
/// Custom head sampler enforcing SEC-OT06-09:
/// Remote parent sampled flag is ignored unless TrustParentSampling is explicitly configured.
/// Ratio sampling is applied to roots and untrusted remote parents.
/// </summary>
public sealed class AutherisSampler : Sampler
{
    private readonly double _ratio;
    private readonly bool _trustParentSampling;
    private readonly TraceIdRatioBasedSampler _ratioSampler;

    public AutherisSampler(double ratio, bool trustParentSampling)
    {
        _ratio = Math.Clamp(ratio, 0.0, 1.0);
        _trustParentSampling = trustParentSampling;
        _ratioSampler = new TraceIdRatioBasedSampler(_ratio);
        Description = $"AutherisSampler{{ratio={_ratio}, trustParentSampling={_trustParentSampling}}}";
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var parentContext = samplingParameters.ParentContext;

        if (parentContext != default && parentContext.TraceId != default)
        {
            if (!parentContext.IsRemote)
            {
                return parentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? new SamplingResult(SamplingDecision.RecordAndSample)
                    : new SamplingResult(SamplingDecision.Drop);
            }

            if (_trustParentSampling)
            {
                return parentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? new SamplingResult(SamplingDecision.RecordAndSample)
                    : new SamplingResult(SamplingDecision.Drop);
            }
        }

        return _ratioSampler.ShouldSample(in samplingParameters);
    }
}
