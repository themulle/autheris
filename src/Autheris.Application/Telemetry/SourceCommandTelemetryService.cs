namespace Autheris.Application.Telemetry;

/// <summary>
/// Service implementation of <see cref="ISourceCommandTelemetry"/>.
/// </summary>
public sealed class SourceCommandTelemetryService : ISourceCommandTelemetry
{
    private readonly IDataSourceTelemetryPolicyProvider? _defaultPolicyProvider;
    private readonly IDataSourceLoggers? _dataLoggers;

    public SourceCommandTelemetryService(
        IDataSourceTelemetryPolicyProvider? defaultPolicyProvider = null,
        IDataSourceLoggers? dataLoggers = null)
    {
        _defaultPolicyProvider = defaultPolicyProvider;
        _dataLoggers = dataLoggers;
    }

    public SourceCommandScope Start(
        in SourceCommandDescriptor descriptor,
        IDataSourceTelemetryPolicyProvider? policyProvider = null)
    {
        return SourceCommandTelemetry.StartCore(descriptor, policyProvider ?? _defaultPolicyProvider, _dataLoggers);
    }
}
