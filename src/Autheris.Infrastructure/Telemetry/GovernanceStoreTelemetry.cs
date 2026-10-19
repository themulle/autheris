namespace Autheris.Infrastructure.Telemetry;

using System;
using System.Diagnostics;
using Autheris.Domain.Diagnostics;

/// <summary>
/// Telemetry helper for governance database repository operations.
/// </summary>
public static class GovernanceStoreTelemetry
{
    private static readonly ActivitySource Source = new("Autheris.Core");

    public static GovernanceStoreScope Start(string store, string operation)
    {
        var spanName = $"autheris.governance_store {operation}";
        Activity? activity = null;

        if (Source.HasListeners())
        {
            activity = Source.StartActivity(spanName, ActivityKind.Internal);
            if (activity != null && activity.IsAllDataRequested)
            {
                activity.SetTag(TelemetryAttributes.AutherisStore, store);
                activity.SetTag("autheris.operation", operation);
            }
        }

        return new GovernanceStoreScope(activity);
    }
}

public readonly struct GovernanceStoreScope : IDisposable
{
    private readonly Activity? _activity;

    public GovernanceStoreScope(Activity? activity)
    {
        _activity = activity;
    }

    public void Fail(Exception exception)
    {
        if (_activity != null && exception != null)
        {
            var errorType = TelemetryExceptionPolicy.ErrorType(exception);
            _activity.SetStatus(ActivityStatusCode.Error, errorType);
            _activity.SetTag(TelemetryAttributes.ErrorType, errorType);
        }
    }

    public void Dispose()
    {
        _activity?.Dispose();
    }
}
