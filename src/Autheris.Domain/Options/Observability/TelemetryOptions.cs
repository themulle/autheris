namespace Autheris.Domain.Options.Observability;

using System;
using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Diagnostics;

public sealed class TelemetryOptions
{
    public const string SectionName = "Gateway:Telemetry";

    public bool Enabled { get; init; } = true;

    [Required]
    public string ServiceName { get; init; } = "autheris";

    public string? ServiceNamespace { get; init; }

    public string? Environment { get; init; }

    public bool IncludeTenant { get; init; } = true;

    public OtlpOptions Otlp { get; init; } = new();

    public SamplingOptions Sampling { get; init; } = new();

    public PropagationOptions Propagation { get; init; } = new();

    public RedactionOptions Redaction { get; init; } = new();

    public MetricsOptions Metrics { get; init; } = new();

    public IdentityOptions Identity { get; init; } = new();

    public DataSourceTelemetryDefaults DataSourceDefaults { get; init; } = new();
}

public sealed class OtlpOptions
{
    public string? Endpoint { get; init; }

    public bool Traces { get; init; } = true;

    public bool Metrics { get; init; } = true;

    public bool Logs { get; init; } = true;

    public OtlpProtocol Protocol { get; init; } = OtlpProtocol.Grpc;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool AllowInsecureTransport { get; init; }

    public bool UseSystemProxy { get; init; }

    public string? ClientCertificatePath { get; init; }

    public string? ClientKeyPath { get; init; }
}

public sealed class SamplingOptions
{
    public double Ratio { get; init; } = 1.0;

    public bool TrustParentSampling { get; init; }
}

public sealed class PropagationOptions
{
    public bool Enabled { get; init; } = true;
}

public sealed class RedactionOptions
{
    public bool AllowStatementText { get; init; }

    public int AttributeValueLengthLimit { get; init; } = 1024;
}

public sealed class MetricsOptions
{
    public bool IncludeTenant { get; init; }

    public int DefaultCardinalityLimit { get; init; } = 2000;

    public int TenantCardinalityLimit { get; init; } = 500;

    public int DataSourceCardinalityLimit { get; init; } = 200;
}

public sealed class IdentityOptions
{
    public TelemetryIdentityMode Mode { get; init; } = TelemetryIdentityMode.None;
}

public sealed class DataSourceTelemetryDefaults
{
    public SpanDetail DefaultSpanDetail { get; init; } = SpanDetail.Basic;

    public StatementTextMode DefaultStatementText { get; init; } = StatementTextMode.None;

    public long SlowQueryThresholdMilliseconds { get; init; } = 500;

    public string MinimumLogLevel { get; init; } = "Information";
}
