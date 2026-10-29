namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Options;

public sealed class DatasourceTestingService : IDatasourceTestingService
{
    private static readonly HashSet<string> ForbiddenProbeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Transfer-Encoding", "Connection"
    };

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<DatasourceTestingService> _logger;
    private readonly ISqlConnectionFactory? _sqlConnectionFactory;
    private readonly IOptions<GatewayOptions>? _gateway;
    private readonly ISqlSourceDescriber? _sqlSourceDescriber;

    public DatasourceTestingService(
        ITableMetadataRepository metadataRepository,
        IHttpClientFactory httpClientFactory,
        IAuditLogRepository auditLogRepository,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null,
        ILogger<DatasourceTestingService>? logger = null,
        ISqlConnectionFactory? sqlConnectionFactory = null,
        IOptions<GatewayOptions>? gateway = null,
        ISqlSourceDescriber? sqlSourceDescriber = null)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
        _gateway = gateway;
        _sqlSourceDescriber = sqlSourceDescriber;
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _secretProvider = secretProvider;
        _environment = environment;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DatasourceTestingService>.Instance;
    }

    public static bool IsValidDatasourceId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
        {
            return false;
        }

        if (id.IndexOfAny(['\0', '\r', '\n', '/', '\\']) >= 0 || id.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return id.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '#' or '$');
    }

    public async Task<DatasourceTestResult> TestDatasourceAsync(
        string datasourceId,
        DatasourceTestRequest request,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasourceId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ct.ThrowIfCancellationRequested();

        if (!IsValidDatasourceId(datasourceId))
        {
            throw new ArgumentException("Invalid datasource identifier format.", nameof(datasourceId));
        }

        // 1. Resolve Datasource Metadata
        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        TableMetadata? matchedTable = null;

        foreach (var t in allTables)
        {
            var domain = t.Identifier.Domain;
            var endpoint = t.Table.HttpEndpoint;
            var compositeKey = endpoint != null && !string.IsNullOrWhiteSpace(endpoint.BaseUrl)
                ? $"{domain}:{endpoint.BaseUrl}"
                : $"{domain}:{t.Table.DataSourceType}";

            if (string.Equals(t.Identifier.ToString(), datasourceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(compositeKey, datasourceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Identifier.TableName, datasourceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(domain, datasourceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(endpoint?.Name, datasourceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(endpoint?.Id.ToString(), datasourceId, StringComparison.OrdinalIgnoreCase))
            {
                matchedTable = t;
                break;
            }
        }

        if (matchedTable != null)
        {
            var dsType = matchedTable.Table.DataSourceType;

            if (dsType == DataSourceType.HttpDeclarative || dsType == DataSourceType.HttpPlugin)
            {
                return await TestHttpDatasourceAsync(datasourceId, matchedTable, request, user, ct).ConfigureAwait(false);
            }

            // Fallback for SQL or other types
            return await TestSqlDatasourceAsync(datasourceId, matchedTable, request, user, ct).ConfigureAwait(false);
        }

        // 2. Pre-flight connectivity testing when catalog tables have not yet been populated
        if (_gateway?.Value.DataSources?.Connections is { } connections)
        {
            DataSourceConnectionOptions? configuredConnection = null;
            if (connections.TryGetValue(datasourceId, out configuredConnection))
            {
            }
            else
            {
                foreach (var (key, conn) in connections)
                {
                    if (string.Equals(key, datasourceId, StringComparison.OrdinalIgnoreCase))
                    {
                        configuredConnection = conn;
                        break;
                    }
                }
            }

            if (configuredConnection != null)
            {
                return await TestSqlDatasourceAsync(datasourceId, null, request, user, ct).ConfigureAwait(false);
            }
        }

        throw new KeyNotFoundException($"Datasource '{datasourceId}' was not found in catalog or configuration.");
    }

    private async Task<DatasourceTestResult> TestHttpDatasourceAsync(
        string datasourceId,
        TableMetadata table,
        DatasourceTestRequest request,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var endpoint = table.Table.HttpEndpoint;
        if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.BaseUrl))
        {
            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: table.Table.DataSourceType,
                IsSuccess: false,
                HttpStatusCode: null,
                LatencyMs: 0,
                ErrorMessage: "Datasource has no configured HTTP endpoint or BaseUrl.",
                Diagnostics: new DatasourceDiagnostics(
                    TargetHost: string.Empty,
                    TargetPort: 0,
                    DnsResolutionSuccess: false,
                    TlsHandshakeSuccess: false,
                    TlsProtocolVersion: null,
                    AuthHeaderApplied: false,
                    SecretResolutionStatus: "None",
                    ProbeDetails: new Dictionary<string, string>()));
        }

        if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: table.Table.DataSourceType,
                IsSuccess: false,
                HttpStatusCode: null,
                LatencyMs: 0,
                ErrorMessage: $"Datasource BaseUrl '{endpoint.BaseUrl}' is not a valid absolute URI.",
                Diagnostics: new DatasourceDiagnostics(
                    TargetHost: string.Empty,
                    TargetPort: 0,
                    DnsResolutionSuccess: false,
                    TlsHandshakeSuccess: false,
                    TlsProtocolVersion: null,
                    AuthHeaderApplied: false,
                    SecretResolutionStatus: "None",
                    ProbeDetails: new Dictionary<string, string>()));
        }

        var relativePath = request.RelativeProbePath ?? endpoint.PathTemplate ?? "/";
        // SR-M-14: fail closed on anything that could re-target the probe (and the vaulted API key) to another
        // scheme/host/port: backslashes, schemes, network-path references, userinfo, control characters, traversal.
        if (!OutboundPathPolicy.TryBuild(baseUri, relativePath, preserveBasePath: false, allowDotSegments: false, out var probeUri, out var pathError))
        {
            var ssrfError = "Invalid relative probe path. Absolute URLs, backslashes, network-path references and path traversal sequences are rejected.";
            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: table.Table.DataSourceType,
                IsSuccess: false,
                HttpStatusCode: null,
                LatencyMs: 0,
                ErrorMessage: ssrfError,
                Diagnostics: new DatasourceDiagnostics(
                    TargetHost: baseUri.Host,
                    TargetPort: baseUri.Port,
                    DnsResolutionSuccess: false,
                    TlsHandshakeSuccess: false,
                    TlsProtocolVersion: null,
                    AuthHeaderApplied: false,
                    SecretResolutionStatus: "Rejected",
                    ProbeDetails: new Dictionary<string, string> { ["Error"] = ssrfError, ["Reason"] = pathError ?? string.Empty }));
        }

        bool isDev = _environment?.IsDevelopment() ?? false;

        // SSRF Check
        try
        {
            await EgressUrlPolicy.ValidateResolvedAsync(probeUri, isDev, ct).ConfigureAwait(false);
        }
        catch (SecurityException secEx)
        {
            _logger.LogWarning(secEx, "SSRF attempt blocked for datasource test on {Uri}", probeUri);
            var redactedMsg = SecretScrubber.Redact(secEx.Message);
            await AppendAuditAsync(datasourceId, user, isSuccess: false, redactedMsg, ct).ConfigureAwait(false);
            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: table.Table.DataSourceType,
                IsSuccess: false,
                HttpStatusCode: null,
                LatencyMs: 0,
                ErrorMessage: redactedMsg,
                Diagnostics: new DatasourceDiagnostics(
                    TargetHost: probeUri.Host,
                    TargetPort: probeUri.Port,
                    DnsResolutionSuccess: false,
                    TlsHandshakeSuccess: false,
                    TlsProtocolVersion: null,
                    AuthHeaderApplied: false,
                    SecretResolutionStatus: "BlockedBySsrfFilter",
                    ProbeDetails: new Dictionary<string, string> { ["SsrfBlocked"] = "true" }));
        }

        var client = _httpClientFactory.CreateClient("DeclarativeHttp");
        var timeout = request.Timeout ?? TimeSpan.FromSeconds(5);
        if (timeout > TimeSpan.FromSeconds(15))
        {
            timeout = TimeSpan.FromSeconds(15);
        }

        bool authApplied = false;
        string secretStatus = "None";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Head, probeUri);

            // Apply auth
            if (endpoint.AuthMode == HttpAuthMode.StaticApiKey &&
                !string.IsNullOrWhiteSpace(endpoint.ApiKeyHeaderName) &&
                !string.IsNullOrWhiteSpace(endpoint.ApiKeySecretName))
            {
                if (!Autheris.Application.Security.DatasourceSecretRefPolicy.IsInDatasourceNamespace(endpoint.ApiKeySecretName))
                {
                    // SR-H-03: never resolve references outside the datasource namespace (config keys, env vars, files).
                    secretStatus = "SecretRefRejected";
                }
                else if (_secretProvider != null)
                {
                    try
                    {
                        var bytes = _secretProvider.GetSecretBytes(endpoint.ApiKeySecretName);
                        if (bytes != null && bytes.Length > 0)
                        {
                            var keyStr = Encoding.UTF8.GetString(bytes).Trim();
                            reqMsg.Headers.TryAddWithoutValidation(endpoint.ApiKeyHeaderName, keyStr);
                            authApplied = true;
                            secretStatus = "Resolved";
                        }
                        else
                        {
                            secretStatus = "SecretEmptyOrNotFound";
                        }
                    }
                    catch
                    {
                        secretStatus = "SecretNotFound";
                    }
                }
            }

            if (request.AdditionalHeaders != null)
            {
                foreach (var (k, v) in request.AdditionalHeaders)
                {
                    if (string.IsNullOrWhiteSpace(k) || ForbiddenProbeHeaders.Contains(k))
                    {
                        continue;
                    }

                    // CRLF injection prevention (SEC-MED-08)
                    if (k.Contains('\r') || k.Contains('\n') || (v != null && (v.Contains('\r') || v.Contains('\n'))))
                    {
                        continue;
                    }

                    reqMsg.Headers.TryAddWithoutValidation(k, v);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Connection probe timed out after {timeout.TotalSeconds} seconds.");
            }

            // Fallback if HEAD is 405 Method Not Allowed
            if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                using var getMsg = new HttpRequestMessage(HttpMethod.Get, probeUri);
                getMsg.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                if (authApplied && endpoint.ApiKeyHeaderName != null)
                {
                    reqMsg.Headers.TryGetValues(endpoint.ApiKeyHeaderName, out var vals);
                    if (vals != null)
                    {
                        getMsg.Headers.TryAddWithoutValidation(endpoint.ApiKeyHeaderName, vals);
                    }
                }
                response = await client.SendAsync(getMsg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }

            sw.Stop();
            int statusCode = (int)response.StatusCode;
            bool isSuccess = response.IsSuccessStatusCode;

            var details = new Dictionary<string, string>
            {
                ["StatusCode"] = statusCode.ToString(),
                ["Method"] = response.RequestMessage?.Method.Method ?? "HEAD",
                ["Scheme"] = probeUri.Scheme
            };

            var diagnostics = new DatasourceDiagnostics(
                TargetHost: probeUri.Host,
                TargetPort: probeUri.Port,
                DnsResolutionSuccess: true,
                TlsHandshakeSuccess: probeUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase),
                TlsProtocolVersion: probeUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "TLS 1.2/1.3" : null,
                AuthHeaderApplied: authApplied,
                SecretResolutionStatus: secretStatus,
                ProbeDetails: details);

            string? errorMsg = isSuccess ? null : $"Target returned HTTP {statusCode} ({response.ReasonPhrase})";

            await AppendAuditAsync(datasourceId, user, isSuccess, errorMsg, ct).ConfigureAwait(false);

            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: table.Table.DataSourceType,
                IsSuccess: isSuccess,
                HttpStatusCode: statusCode,
                LatencyMs: sw.ElapsedMilliseconds,
                ErrorMessage: errorMsg != null ? SecretScrubber.Redact(errorMsg) : null,
                Diagnostics: diagnostics);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            var timeoutMsg = $"Connection probe timed out after {timeout.TotalSeconds}s.";
            await AppendAuditAsync(datasourceId, user, isSuccess: false, timeoutMsg, ct).ConfigureAwait(false);

            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: table.Table.DataSourceType,
                IsSuccess: false,
                HttpStatusCode: 504,
                LatencyMs: sw.ElapsedMilliseconds,
                ErrorMessage: timeoutMsg,
                Diagnostics: new DatasourceDiagnostics(
                    TargetHost: probeUri.Host,
                    TargetPort: probeUri.Port,
                    DnsResolutionSuccess: true,
                    TlsHandshakeSuccess: false,
                    TlsProtocolVersion: null,
                    AuthHeaderApplied: authApplied,
                    SecretResolutionStatus: secretStatus,
                    ProbeDetails: new Dictionary<string, string> { ["Timeout"] = $"{timeout.TotalSeconds}s" }));
        }
        catch (Exception ex)
        {
            sw.Stop();
            var sanitized = SecretScrubber.Redact(ex.Message);
            await AppendAuditAsync(datasourceId, user, isSuccess: false, sanitized, ct).ConfigureAwait(false);

            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: table.Table.DataSourceType,
                IsSuccess: false,
                HttpStatusCode: null,
                LatencyMs: sw.ElapsedMilliseconds,
                ErrorMessage: sanitized,
                Diagnostics: new DatasourceDiagnostics(
                    TargetHost: probeUri.Host,
                    TargetPort: probeUri.Port,
                    DnsResolutionSuccess: false,
                    TlsHandshakeSuccess: false,
                    TlsProtocolVersion: null,
                    AuthHeaderApplied: authApplied,
                    SecretResolutionStatus: secretStatus,
                    ProbeDetails: new Dictionary<string, string> { ["Exception"] = ex.GetType().Name }));
        }
    }

    private async Task<DatasourceTestResult> TestSqlDatasourceAsync(
        string datasourceId,
        TableMetadata? table,
        DatasourceTestRequest request,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        string? sourceName = table?.Table.SourceName;
        DataSourceConnectionOptions? connection = null;
        if (_gateway?.Value.DataSources?.Connections is { } connections)
        {
            if (!string.IsNullOrWhiteSpace(sourceName) && connections.TryGetValue(sourceName, out connection))
            {
            }
            else if (connections.TryGetValue(datasourceId, out connection))
            {
            }
            else
            {
                foreach (var (key, conn) in connections)
                {
                    if (string.Equals(key, datasourceId, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(sourceName) && string.Equals(key, sourceName, StringComparison.OrdinalIgnoreCase)))
                    {
                        connection = conn;
                        break;
                    }
                }
            }
        }

        var dsType = table?.Table.DataSourceType ?? DataSourceType.Sql;

        if (_sqlConnectionFactory is null || connection is null)
        {
            const string notConfigured = "The data source has no configured connection; nothing was tested.";
            await AppendAuditAsync(datasourceId, user, isSuccess: false, notConfigured, ct).ConfigureAwait(false);
            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: dsType,
                IsSuccess: false,
                HttpStatusCode: null,
                LatencyMs: 0,
                ErrorMessage: notConfigured,
                Diagnostics: new DatasourceDiagnostics(
                    TargetHost: string.Empty,
                    TargetPort: 0,
                    DnsResolutionSuccess: false,
                    TlsHandshakeSuccess: false,
                    TlsProtocolVersion: null,
                    AuthHeaderApplied: false,
                    SecretResolutionStatus: "Unknown",
                    ProbeDetails: new Dictionary<string, string> { ["Category"] = "NotConfigured" }));
        }

        var description = _sqlSourceDescriber?.Describe(connection)
            ?? new SqlSourceDescription(string.Empty, null, connection.Provider, "Unknown");
        var details = new Dictionary<string, string>
        {
            ["Dialect"] = description.Dialect,
            ["AuthPath"] = description.AuthPath
        };
        if (description.LoginName is not null)
        {
            details["LoginName"] = description.LoginName;
        }

        var timeout = request.Timeout ?? TimeSpan.FromSeconds(5);
        if (timeout > TimeSpan.FromSeconds(15))
        {
            timeout = TimeSpan.FromSeconds(15);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var opened = await _sqlConnectionFactory.CreateOpenConnectionAsync(connection, cts.Token).ConfigureAwait(false);
            sw.Stop();
            await AppendAuditAsync(datasourceId, user, isSuccess: true, null, ct).ConfigureAwait(false);

            return new DatasourceTestResult(
                DatasourceId: datasourceId,
                Type: dsType,
                IsSuccess: true,
                HttpStatusCode: null,
                LatencyMs: Math.Max(1, sw.ElapsedMilliseconds),
                ErrorMessage: null,
                Diagnostics: BuildSqlDiagnostics(description, connected: true, details));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            string timeoutMsg = $"Connection probe timed out after {timeout.TotalSeconds}s.";
            details["Category"] = "Timeout";
            await AppendAuditAsync(datasourceId, user, isSuccess: false, timeoutMsg, ct).ConfigureAwait(false);
            return SqlFailure(datasourceId, dsType, sw, timeoutMsg, description, details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            // Classified exceptions carry a credential-free message; any other driver message is redacted and only the type is reported.
            var diagnostic = FindDiagnostic(ex);
            string message;
            if (diagnostic is Exception diagnosticException)
            {
                message = SecretScrubber.Redact(diagnosticException.Message);
                details["Category"] = diagnostic.CategoryName;
                foreach (var (key, value) in diagnostic.DiagnosticFields)
                {
                    details[key] = value;
                }
            }
            else
            {
                message = SecretScrubber.Redact(ex.Message);
                details["Category"] = "ConnectionFailed";
                details["Exception"] = ex.GetType().Name;
            }

            // The id comes from the route: strip line breaks so it cannot forge log entries (CodeQL log injection).
            var safeDatasourceId = datasourceId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogWarning("Datasource test failed for {DatasourceId} ({Category}, {ExceptionType}).", safeDatasourceId, details["Category"], ex.GetType().Name);
            await AppendAuditAsync(datasourceId, user, isSuccess: false, message, ct).ConfigureAwait(false);
            return SqlFailure(datasourceId, dsType, sw, message, description, details);
        }
    }

    private static ISqlSourceDiagnosticException? FindDiagnostic(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ISqlSourceDiagnosticException diagnostic)
            {
                return diagnostic;
            }
        }

        return null;
    }

    private static DatasourceDiagnostics BuildSqlDiagnostics(SqlSourceDescription description, bool connected, Dictionary<string, string> details) =>
        new(
            TargetHost: description.Host,
            TargetPort: description.Port ?? 0,
            DnsResolutionSuccess: connected,
            TlsHandshakeSuccess: false, // not observable through the driver abstraction; reported as unknown (false, no protocol version)
            TlsProtocolVersion: null,
            AuthHeaderApplied: false,
            SecretResolutionStatus: connected ? "Resolved" : "Unknown",
            ProbeDetails: details);

    private static DatasourceTestResult SqlFailure(
        string datasourceId, DataSourceType dsType, Stopwatch sw, string message, SqlSourceDescription description, Dictionary<string, string> details) =>
        new(
            DatasourceId: datasourceId,
            Type: dsType,
            IsSuccess: false,
            HttpStatusCode: null,
            LatencyMs: sw.ElapsedMilliseconds,
            ErrorMessage: message,
            Diagnostics: BuildSqlDiagnostics(description, connected: false, details));

    private async Task AppendAuditAsync(string datasourceId, ClaimsPrincipal user, bool isSuccess, string? error, CancellationToken ct)
    {
        try
        {
            // DT-SC-03: the audit belongs to the caller's resolved tenant; a caller without a valid tenant claim gets no audit row (never a "default" bucket).
            if (Autheris.Domain.Tenancy.TenantClaimReader.Read(user) is not { Kind: Autheris.Domain.Tenancy.ClaimTenantKind.Valid } callerTenant)
            {
                _logger.LogWarning("Datasource testing audit skipped for {DatasourceId}: the caller has no valid tenant claim.", datasourceId.Replace("\r", string.Empty).Replace("\n", string.Empty));
                return;
            }

            var sidStr = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "system";

            var entry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                TenantId = callerTenant.Tenant,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = AuditEventTypes.DatasourceTested,
                ActorSid = new Sid(sidStr),
                TargetTable = $"datasource:{datasourceId}",
                Decision = isSuccess ? "ALLOW" : "DENY",
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    DatasourceId = datasourceId,
                    Success = isSuccess,
                    Error = error
                })
            };

            await _auditLogRepository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append datasource testing audit log for {DatasourceId}", datasourceId.Replace("\r", string.Empty).Replace("\n", string.Empty));
        }
    }
}
