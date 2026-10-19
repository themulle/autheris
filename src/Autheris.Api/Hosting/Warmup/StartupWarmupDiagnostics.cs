namespace Autheris.Api.Hosting.Warmup;

using System.Diagnostics.Metrics;
using Autheris.Domain.Diagnostics;

/// <summary>PLAN-OPS-150 (D-150-9): warm-up instruments on the shared gateway meter. No request, query, rate-limit or audit counter.</summary>
public static class StartupWarmupDiagnostics
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Skipped = "skipped";

    public static readonly Histogram<double> Duration =
        GatewayDiagnostics.Meter.CreateHistogram<double>("autheris.startup.warmup.duration", unit: "s", description: "Total duration of the best-effort startup warm-up");

    public static readonly Histogram<double> StepDuration =
        GatewayDiagnostics.Meter.CreateHistogram<double>("autheris.startup.warmup.step.duration", unit: "s", description: "Duration of one startup warm-up step by outcome");

    public static void RecordStep(string step, string outcome, double seconds) =>
        StepDuration.Record(seconds, new KeyValuePair<string, object?>("step", step), new KeyValuePair<string, object?>("outcome", outcome));
}
