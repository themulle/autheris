namespace Autheris.Api.Hosting.Warmup;

/// <summary>
/// PLAN-OPS-150 WP-A: opt-in measurement of the GraphQL cold path. Bound to <see cref="SectionName"/>. Development only
/// (SEC-OPS150-12); enabling it elsewhere fails the host start.
/// </summary>
public sealed class ColdPathTimingOptions
{
    public const string SectionName = "Gateway:Startup:ColdPathTiming";

    /// <summary>When true, the first operations log per-stage durations (stage names and milliseconds only).</summary>
    public bool Enabled { get; init; }
}
