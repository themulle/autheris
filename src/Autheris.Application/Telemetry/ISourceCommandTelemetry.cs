namespace Autheris.Application.Telemetry;

/// <summary>
/// Data-path telemetry hook producing client spans and metrics for database operations.
/// </summary>
public interface ISourceCommandTelemetry
{
    /// <summary>
    /// Begins a database source command telemetry scope.
    /// </summary>
    SourceCommandScope Start(
        in SourceCommandDescriptor descriptor,
        IDataSourceTelemetryPolicyProvider? policyProvider = null);
}
