namespace Autheris.Api.Telemetry;

using System;
using System.Diagnostics.Metrics;

/// <summary>
/// Source and meter registry defining all permitted telemetry producers.
/// Enforces D-5.
/// </summary>
public static class AutherisTelemetrySources
{
    public const string Core = "Autheris.Core";
    public const string SqlCompiler = "Autheris.SqlCompiler";
    public const string Mcp = "Autheris.Mcp";
    public const string AsyncJobs = "Autheris.AsyncJobs";
    public const string GovernanceClassification = "Autheris.Governance.Classification";

    public static readonly string[] AllActivitySources =
    [
        Core,
        SqlCompiler,
        Mcp,
        GovernanceClassification
    ];

    public static readonly string[] AllMeters =
    [
        Core,
        SqlCompiler,
        Mcp,
        AsyncJobs,
        GovernanceClassification
    ];

    public static bool IsExposedMeter(Instrument instrument)
    {
        if (instrument == null)
        {
            return false;
        }

        var meterName = instrument.Meter.Name;
        foreach (var name in AllMeters)
        {
            if (string.Equals(meterName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return meterName.StartsWith("Autheris.", StringComparison.OrdinalIgnoreCase);
    }
}
