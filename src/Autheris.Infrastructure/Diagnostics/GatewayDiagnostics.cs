namespace Autheris.Infrastructure.Diagnostics;

// Forward to Domain.Diagnostics for backwards compatibility
public static class GatewayDiagnostics
{
    public const string ActivitySourceName = Autheris.Domain.Diagnostics.GatewayDiagnostics.ActivitySourceName;
    public const string MeterName = Autheris.Domain.Diagnostics.GatewayDiagnostics.MeterName;

    public static System.Diagnostics.ActivitySource Source => Autheris.Domain.Diagnostics.GatewayDiagnostics.Source;
    public static System.Diagnostics.Metrics.Meter Meter => Autheris.Domain.Diagnostics.GatewayDiagnostics.Meter;

    public static System.Diagnostics.Metrics.Counter<long> ForbiddenRequestsCounter => Autheris.Domain.Diagnostics.GatewayDiagnostics.ForbiddenRequestsCounter;
    public static System.Diagnostics.Metrics.Counter<long> QueryTooComplexCounter => Autheris.Domain.Diagnostics.GatewayDiagnostics.QueryTooComplexCounter;
    public static System.Diagnostics.Metrics.Counter<long> CrossTenantMismatchCounter => Autheris.Domain.Diagnostics.GatewayDiagnostics.CrossTenantMismatchCounter;
    public static System.Diagnostics.Metrics.Histogram<double> PolicyEvaluationDuration => Autheris.Domain.Diagnostics.GatewayDiagnostics.PolicyEvaluationDuration;
    public static System.Diagnostics.Metrics.Histogram<double> LineageTraversalDuration => Autheris.Domain.Diagnostics.GatewayDiagnostics.LineageTraversalDuration;

    public static void SetSafeTag(System.Diagnostics.Activity? activity, string spanName, string key, object? value)
        => Autheris.Domain.Diagnostics.GatewayDiagnostics.SetSafeTag(activity, spanName, key, value);
}
