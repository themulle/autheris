namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Request to test connectivity and authentication against a registered or pending datasource.
/// </summary>
public sealed record DatasourceTestRequest(
    string? RelativeProbePath = null,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? AdditionalHeaders = null);

/// <summary>
/// Diagnostics information collected during the datasource connectivity probe.
/// </summary>
public sealed record DatasourceDiagnostics(
    string TargetHost,
    int TargetPort,
    bool DnsResolutionSuccess,
    bool TlsHandshakeSuccess,
    string? TlsProtocolVersion,
    bool AuthHeaderApplied,
    string SecretResolutionStatus,
    IReadOnlyDictionary<string, string> ProbeDetails);

/// <summary>
/// Result of the datasource connectivity probe.
/// </summary>
public sealed record DatasourceTestResult(
    string DatasourceId,
    DataSourceType Type,
    bool IsSuccess,
    int? HttpStatusCode,
    long LatencyMs,
    string? ErrorMessage,
    DatasourceDiagnostics Diagnostics);
