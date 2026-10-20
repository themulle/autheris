namespace Autheris.Domain.Plugins.Auth;

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class AuthPluginDiagnostics
{
    public const string ActivitySourceName = "Autheris.Plugins.Auth";
    public const string MeterName = "Autheris.Plugins.Auth";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Histogram<double> InvocationDuration =
        Meter.CreateHistogram<double>(
            "autheris_auth_plugin_duration_seconds",
            unit: "s",
            description: "Duration of auth plugin invocations in seconds");

    public static readonly Counter<long> InvocationsTotal =
        Meter.CreateCounter<long>(
            "autheris_auth_plugin_invocations_total",
            description: "Total number of auth plugin invocations");

    public static readonly Counter<long> SecurityAlertsTotal =
        Meter.CreateCounter<long>(
            "autheris_auth_plugin_security_alerts_total",
            description: "Total number of auth plugin security alerts triggered");

    public static void RecordInvocation(string pluginName, string extensionPoint, string outcome, double durationSeconds, string? tenant = null)
    {
        var tags = new TagList
        {
            { "plugin", pluginName },
            { "extension_point", extensionPoint },
            { "outcome", outcome }
        };

        if (!string.IsNullOrEmpty(tenant))
        {
            tags.Add("tenant", tenant);
        }

        InvocationDuration.Record(durationSeconds, tags);
        InvocationsTotal.Add(1, tags);
    }

    public static void RecordSecurityAlert(string alertType, string pluginName)
    {
        SecurityAlertsTotal.Add(1, new KeyValuePair<string, object?>("alert_type", alertType), new KeyValuePair<string, object?>("plugin", pluginName));
    }
}
