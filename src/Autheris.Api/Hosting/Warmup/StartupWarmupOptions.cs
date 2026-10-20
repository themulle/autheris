namespace Autheris.Api.Hosting.Warmup;

/// <summary>PLAN-OPS-150 (D-150-8): best-effort warm-up settings. Bound to <see cref="SectionName"/>.</summary>
public sealed class StartupWarmupOptions
{
    public const string SectionName = "Gateway:Startup:Warmup";

    /// <summary>Configurable step names (A-150-9, PLAN-PERF-254). The GraphQL executor is never a configurable step.</summary>
    public const string McpSerializationStep = "McpSerialization";
    public const string DataSourceConnectionStep = "DataSourceConnection";
    public const string DiscoveryDocumentStep = "DiscoveryDocument";

    /// <summary>Disables the best-effort steps only; the executor gate stays evaluated (SEC-OPS150-01).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Global budget of the best-effort steps in seconds (1-300).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>When false the best-effort steps run in the background without delaying readiness.</summary>
    public bool RequireForReadiness { get; init; } = true;

    public Dictionary<string, StartupWarmupStepOptions> Steps { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StartupWarmupStepOptions
{
    public bool Enabled { get; init; } = true;
}
