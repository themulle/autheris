using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("Autheris.Tests.Unit")]

namespace Autheris.Application.Services;

public sealed class DeclarativeHttpDataSourceExecutor : IDataSourceExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeclarativeHttpDataSourceExecutor> _logger;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IHostEnvironment? _environment;

    private static readonly HashSet<string> DisallowedForwardHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie", "Host", "Proxy-Authorization",
        "Proxy-Authenticate", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto",
        "X-User-Sid", "X-Tenant-Id", "X-Tenant-ID", "Forwarded", "X-Original-URL",
        "X-Rewrite-URL", "X-Real-IP", "X-Gateway-Identity"
    };

    public const string HttpClientName = "DeclarativeHttp";
    public DataSourceType SupportedType => DataSourceType.HttpDeclarative;

    public DeclarativeHttpDataSourceExecutor(
        IHttpClientFactory httpClientFactory,
        ILogger<DeclarativeHttpDataSourceExecutor> logger,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _secretProvider = secretProvider;
        _environment = environment;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        var descriptor = context.Metadata.HttpEndpoint;
        if (descriptor == null)
        {
            throw new InvalidOperationException(
                $"Table '{context.Metadata.Identifier}' is configured as HttpDeclarative but has no HttpEndpointDescriptor.");
        }

        // Check if this execution is a batch request (e.g. an "ids" or "keys" array parameter)
        if (descriptor.BatchType != HttpBatchType.None &&
            !string.IsNullOrWhiteSpace(descriptor.BatchParamName) &&
            context.Arguments.TryGetValue(descriptor.BatchParamName, out var batchArgVal) &&
            batchArgVal is IEnumerable<object> batchKeys &&
            batchArgVal is not string)
        {
            var keysList = batchKeys.Select(k => k.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (keysList.Count > 0)
            {
                return await ExecuteBatchAsync(descriptor, context, keysList, ct);
            }
        }

        // Check if this execution uses multi-page pagination (R-56)
        if (descriptor.Pagination != null && descriptor.Pagination.Strategy != HttpPaginationStrategy.None)
        {
            return await ExecutePagedRequestsAsync(descriptor, context, ct);
        }

        return await ExecuteSingleRequestAsync(descriptor, context, context.Arguments, ct);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecutePagedRequestsAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        var config = descriptor.Pagination ?? new HttpPaginationConfig();
        var allRows = new List<IReadOnlyDictionary<string, object?>>();
        long totalBytes = 0;
        int pageIndex = config.ZeroIndexedPage ? 0 : 1;
        string? currentCursor = null;
        string? currentNextLink = null;
        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(descriptor.BaseUrl) && Uri.TryCreate(descriptor.BaseUrl, UriKind.Absolute, out var parsedBase))
        {
            baseUri = parsedBase;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        for (int page = 0; page < config.MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            string url;
            if (config.Strategy == HttpPaginationStrategy.NextLinkUrl && !string.IsNullOrWhiteSpace(currentNextLink))
            {
                if (Uri.TryCreate(currentNextLink, UriKind.Absolute, out var absNextUri))
                {
                    if (config.EnforceSameHost && baseUri != null)
                    {
                        if (!string.Equals(absNextUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
                            absNextUri.Scheme != baseUri.Scheme ||
                            absNextUri.Port != baseUri.Port)
                        {
                            throw new SecurityException($"NextLink host spoofing detected: '{absNextUri.Host}' does not match registered host '{baseUri.Host}'.");
                        }
                    }
                    url = absNextUri.ToString();
                }
                else if (baseUri != null)
                {
                    url = new Uri(baseUri, currentNextLink).ToString();
                }
                else
                {
                    url = currentNextLink;
                }
            }
            else
            {
                var pageArgs = new Dictionary<string, object?>(context.Arguments);
                if (config.Strategy == HttpPaginationStrategy.OffsetLimit)
                {
                    var offsetParam = config.PageParamName ?? "offset";
                    var limitParam = config.SizeParamName ?? "limit";
                    int offset = (pageIndex - (config.ZeroIndexedPage ? 0 : 1)) * config.DefaultPageSize;
                    pageArgs[offsetParam] = offset;
                    pageArgs[limitParam] = config.DefaultPageSize;
                }
                else if (config.Strategy == HttpPaginationStrategy.PageNumber)
                {
                    var pageParam = config.PageParamName ?? "page";
                    var sizeParam = config.SizeParamName ?? "size";
                    pageArgs[pageParam] = pageIndex;
                    pageArgs[sizeParam] = config.DefaultPageSize;
                }
                else if (config.Strategy == HttpPaginationStrategy.Cursor && !string.IsNullOrWhiteSpace(currentCursor))
                {
                    var cursorParam = config.PageParamName ?? "cursor";
                    pageArgs[cursorParam] = currentCursor;
                }
                url = BuildUrl(descriptor, pageArgs, context.Principal);
            }

            await ValidateDestinationUrl(url, ct);
            var method = new HttpMethod(descriptor.Method ?? "GET");

            using var request = new HttpRequestMessage(method, url);
            ApplyHeadersAndAuth(request, descriptor, context);

            using var response = await SendWithRedirectProtectionAsync(client, request, descriptor, context, ct);
            response.EnsureSuccessStatusCode();

            var rawBytes = response.Content.Headers.ContentLength ?? 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            totalBytes += rawBytes > 0 ? rawBytes : stream.Length;
            if (totalBytes > config.MaxStagedBytes)
            {
                throw new Autheris.Application.Connectors.ConnectorRowLimitExceededException(
                    context.Metadata.Identifier,
                    config.MaxStagedRows);
            }

            var rows = ExtractRowsFromJson(jsonDoc.RootElement, descriptor.JsonRootPath);
            if (rows.Count == 0)
            {
                break;
            }

            allRows.AddRange(rows);
            if (allRows.Count > config.MaxStagedRows)
            {
                throw new Autheris.Application.Connectors.ConnectorRowLimitExceededException(
                    context.Metadata.Identifier,
                    config.MaxStagedRows);
            }

            // Extract NextLink or Cursor if applicable
            if (config.Strategy == HttpPaginationStrategy.NextLinkUrl)
            {
                var nextLinkPath = config.NextLinkJsonPath ?? "@odata.nextLink";
                currentNextLink = ExtractJsonValue(jsonDoc.RootElement, nextLinkPath);
                if (string.IsNullOrWhiteSpace(currentNextLink))
                {
                    break;
                }
            }
            else if (config.Strategy == HttpPaginationStrategy.Cursor)
            {
                var cursorPath = config.NextCursorJsonPath ?? "next_cursor";
                var nextCursor = ExtractJsonValue(jsonDoc.RootElement, cursorPath);
                if (string.IsNullOrWhiteSpace(nextCursor) || string.Equals(nextCursor, currentCursor, StringComparison.Ordinal))
                {
                    break;
                }
                currentCursor = nextCursor;
            }
            else if (config.Strategy == HttpPaginationStrategy.OffsetLimit || config.Strategy == HttpPaginationStrategy.PageNumber)
            {
                if (rows.Count < config.DefaultPageSize)
                {
                    break; // Last page reached
                }
                pageIndex++;
            }
        }

        return allRows;
    }

    private static string? ExtractJsonValue(JsonElement root, string path)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(path, out var directMatch))
        {
            return directMatch.ValueKind switch
            {
                JsonValueKind.String => directMatch.GetString(),
                JsonValueKind.Number => directMatch.GetRawText(),
                _ => null
            };
        }

        var parts = path.TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var p in parts)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(p, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null
        };
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteBatchAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyList<string> keys,
        CancellationToken ct = default)
    {
        switch (descriptor.BatchType)
        {
            case HttpBatchType.QueryParameterList:
            {
                // Bulk via query param list, e.g. ?ids=1,2,3
                var paramName = descriptor.BatchParamName ?? "ids";
                var joinedKeys = string.Join(",", keys);
                var batchArgs = new Dictionary<string, object?>(context.Arguments)
                {
                    [paramName] = joinedKeys
                };
                return await ExecuteSingleRequestAsync(descriptor, context, batchArgs, ct);
            }

            case HttpBatchType.JsonBodyArray:
            {
                // Bulk via POST body with JSON array
                return await ExecuteJsonArrayBatchAsync(descriptor, context, keys, ct);
            }

            case HttpBatchType.ParallelSingleRequests:
            default:
            {
                // Throttled parallel single requests
                return await ExecuteThrottledParallelRequestsAsync(descriptor, context, keys, ct);
            }
        }
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteSingleRequestAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = descriptor.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(descriptor.Timeout);
        }
        var effectiveCt = timeoutCts?.Token ?? ct;

        var url = BuildUrl(descriptor, arguments, context.Principal);
        await ValidateDestinationUrl(url, effectiveCt);
        var method = new HttpMethod(descriptor.Method ?? "GET");

        using var request = new HttpRequestMessage(method, url);
        ApplyHeadersAndAuth(request, descriptor, context);

        _logger.LogDebug("Executing Declarative HTTP {Method} {Url} for {Table}", method, url, context.Metadata.Identifier);

        using var response = await SendWithRedirectProtectionAsync(client, request, descriptor, context, effectiveCt);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(effectiveCt);
        using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: effectiveCt);

        return ExtractRowsFromJson(jsonDoc.RootElement, descriptor.JsonRootPath);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteJsonArrayBatchAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = descriptor.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(descriptor.Timeout);
        }
        var effectiveCt = timeoutCts?.Token ?? ct;

        var url = BuildUrl(descriptor, context.Arguments, context.Principal);
        await ValidateDestinationUrl(url, effectiveCt);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyHeadersAndAuth(request, descriptor, context);

        var jsonBody = JsonSerializer.Serialize(keys);
        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await SendWithRedirectProtectionAsync(client, request, descriptor, context, effectiveCt);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(effectiveCt);
        using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: effectiveCt);

        return ExtractRowsFromJson(jsonDoc.RootElement, descriptor.JsonRootPath);
    }

    private async Task<HttpResponseMessage> SendWithRedirectProtectionAsync(
        HttpClient client,
        HttpRequestMessage initialRequest,
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        CancellationToken ct)
    {
        var currentRequest = initialRequest;
        var currentUrl = initialRequest.RequestUri?.ToString() ?? string.Empty;
        const int maxRedirects = 3;
        int redirectCount = 0;

        while (true)
        {
            var response = await client.SendAsync(currentRequest, ct);

            if (IsRedirectStatusCode(response.StatusCode) && response.Headers.Location != null)
            {
                if (redirectCount >= maxRedirects)
                {
                    response.Dispose();
                    throw new SecurityException($"Too many HTTP redirects (exceeded limit of {maxRedirects}).");
                }

                redirectCount++;
                var targetUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(new Uri(currentUrl), response.Headers.Location);

                var targetUrl = targetUri.ToString();

                // SEC M-25: credentials (forwarded bearer token, API key, client credential, identity/tenant headers)
                // must never reach another origin. Cross-origin redirects are refused instead of being followed.
                if (!IsSameOrigin(new Uri(currentUrl), targetUri))
                {
                    response.Dispose();
                    throw new SecurityException(
                        $"Cross-origin HTTP redirect from '{new Uri(currentUrl).GetLeftPart(UriPartial.Authority)}' to '{targetUri.GetLeftPart(UriPartial.Authority)}' was blocked.");
                }

                await ValidateDestinationUrl(targetUrl, ct);

                _logger.LogInformation("Following validated HTTP redirect #{Hop} from {Source} to {Target}",
                    redirectCount, currentUrl, targetUrl);

                response.Dispose();

                var newMethod = response.StatusCode == HttpStatusCode.SeeOther ? HttpMethod.Get : currentRequest.Method;
                var newRequest = new HttpRequestMessage(newMethod, targetUrl);
                ApplyHeadersAndAuth(newRequest, descriptor, context);

                currentRequest = newRequest;
                currentUrl = targetUrl;
                continue;
            }

            return response;
        }
    }

    internal static bool IsSameOrigin(Uri source, Uri target) =>
        source.IsAbsoluteUri && target.IsAbsoluteUri &&
        string.Equals(source.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(source.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        source.Port == target.Port;

    private static bool IsRedirectStatusCode(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308;

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteThrottledParallelRequestsAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var maxConcurrency = Math.Max(1, descriptor.MaxConcurrentRequests);
        using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var paramName = descriptor.PrimaryKeyField ?? "id";

        var allRows = new ConcurrentBag<IReadOnlyDictionary<string, object?>>();

        var tasks = keys.Select(async key =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var singleArgs = new Dictionary<string, object?>(context.Arguments)
                {
                    [paramName] = key
                };
                var rows = await ExecuteSingleRequestAsync(descriptor, context, singleArgs, ct);
                foreach (var r in rows)
                {
                    allRows.Add(r);
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        return allRows.ToList();
    }

    internal string BuildUrl(
        HttpEndpointDescriptor descriptor,
        IReadOnlyDictionary<string, object?> arguments,
        ClaimsPrincipal principal)
    {
        var baseUrl = descriptor.BaseUrl.TrimEnd('/');
        var pathTemplate = descriptor.PathTemplate.StartsWith('/')
            ? descriptor.PathTemplate
            : "/" + descriptor.PathTemplate;

        var usedArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedNames = GetReservedParameterNames(descriptor, includeStaticNames: true);
        var pathReservedNames = GetReservedParameterNames(descriptor, includeStaticNames: false);

        // 1. Expand {param} path placeholders
        var expandedPath = pathTemplate;
        foreach (var (k, v) in arguments)
        {
            var placeholder = "{" + k + "}";
            if (expandedPath.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                // SEC M-26: gateway-controlled parameters (tenant etc.) can never be supplied by the caller.
                if (IsReservedParameter(k, pathReservedNames))
                {
                    throw new SecurityException($"Parameter '{k}' is reserved for the gateway and must not be set by the caller.");
                }

                var strVal = v?.ToString() ?? string.Empty;
                // SEC-5: Disallow directory traversal sequences in path placeholder parameters
                if (strVal.Contains("..") || strVal.Contains('/') || strVal.Contains('\\'))
                {
                    throw new System.Security.SecurityException($"Potential path traversal attack in parameter '{k}': path separators and '..' are not allowed.");
                }
                expandedPath = expandedPath.Replace(placeholder, Uri.EscapeDataString(strVal), StringComparison.OrdinalIgnoreCase);
                usedArgs.Add(k);
            }
        }

        // 2. Query parameters for remaining arguments
        var queryParams = new List<string>();
        foreach (var (k, v) in arguments)
        {
            if (!usedArgs.Contains(k) && v != null && v is not IEnumerable<object>)
            {
                // SEC-5 / SEC M-26: only well-formed argument names; reserved (gateway-set) names are dropped.
                if (!IsAllowedArgumentName(k) || IsReservedParameter(k, reservedNames))
                {
                    _logger.LogWarning("Security: Dropped caller argument '{Argument}' (reserved or malformed parameter name).", k);
                    continue;
                }
                queryParams.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v.ToString() ?? string.Empty)}");
            }
        }

        // 3. Tenant ID Pushdown as query parameter (fail-closed without tenant claim, SEC M-26)
        if (!string.IsNullOrWhiteSpace(descriptor.TenantIdQueryParam))
        {
            var tenantClaim = ResolveTenantClaim(principal)
                ?? throw new SecurityException($"The HTTP data source requires a tenant ('{descriptor.TenantIdQueryParam}'), but the caller has no tenant claim.");
            queryParams.Add($"{Uri.EscapeDataString(descriptor.TenantIdQueryParam)}={Uri.EscapeDataString(tenantClaim)}");
        }

        var fullUrl = baseUrl + expandedPath;
        if (queryParams.Count > 0)
        {
            var separator = fullUrl.Contains('?') ? "&" : "?";
            fullUrl += separator + string.Join("&", queryParams);
        }

        return fullUrl;
    }

    private static readonly string[] StaticReservedParameterNames = ["tenant_id", "tid", "tenant", "tenantid", "isAdmin", "role", "roles"];

    private static HashSet<string> GetReservedParameterNames(HttpEndpointDescriptor descriptor, bool includeStaticNames)
    {
        var reserved = includeStaticNames
            ? new HashSet<string>(StaticReservedParameterNames, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(descriptor.TenantIdQueryParam))
        {
            reserved.Add(NormalizeParameterName(descriptor.TenantIdQueryParam));
        }
        if (!string.IsNullOrWhiteSpace(descriptor.TenantIdHeaderName))
        {
            reserved.Add(NormalizeParameterName(descriptor.TenantIdHeaderName));
        }
        if (!string.IsNullOrWhiteSpace(descriptor.ApiKeyHeaderName))
        {
            reserved.Add(NormalizeParameterName(descriptor.ApiKeyHeaderName));
        }
        return reserved;
    }

    private static bool IsReservedParameter(string name, HashSet<string> reservedNames) =>
        reservedNames.Contains(NormalizeParameterName(name));

    private static string NormalizeParameterName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.EndsWith("[]", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^2];
        }
        return trimmed;
    }

    private static bool IsAllowedArgumentName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
            {
                return false;
            }
        }
        return true;
    }

    private static string? ResolveTenantClaim(ClaimsPrincipal principal)
    {
        var tenantClaim = principal.FindFirst("tenant_id")?.Value
                          ?? principal.FindFirst("tid")?.Value
                          ?? principal.FindFirst("tenant")?.Value;
        return string.IsNullOrWhiteSpace(tenantClaim) ? null : tenantClaim;
    }

    internal Task ValidateDestinationUrl(string fullUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri))
        {
            throw new SecurityException($"Invalid destination URL: '{fullUrl}'.");
        }

        bool isDev = _environment?.IsDevelopment() ?? false;
        return ValidateDestinationUrlAsync(uri, isDev, ct);
    }

    public static Task ValidateDestinationUrlAsync(Uri uri, bool isDev, CancellationToken ct = default)
        => EgressUrlPolicy.ValidateResolvedAsync(uri, isDev, ct);

    public static void ValidateUrl(Uri uri)
        => EgressUrlPolicy.ValidateStatic(uri, isDev: false, enforceHttps: false);

    public static bool IsForbiddenMetadataHost(string host)
        => EgressAddressRules.IsForbiddenHost(host);

    public static bool IsRestrictedIp(IPAddress ip)
        => EgressAddressRules.IsAlwaysForbidden(ip) || EgressAddressRules.IsPrivate(ip);

    private void ApplyHeadersAndAuth(
        HttpRequestMessage request,
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context)
    {
        // 1. Forward configured headers from caller (enforcing security denylist)
        if (context.RequestHeaders != null && descriptor.ForwardHeaders.Count > 0)
        {
            foreach (var (targetHeader, sourceHeader) in descriptor.ForwardHeaders)
            {
                if (DisallowedForwardHeaders.Contains(targetHeader) || DisallowedForwardHeaders.Contains(sourceHeader) ||
                    // SEC M-26: gateway-set headers (tenant, API key) can never be supplied via forwarding
                    string.Equals(targetHeader, descriptor.TenantIdHeaderName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetHeader, descriptor.ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Security: Blocked forwarding of sensitive header '{Header}' downstream.", targetHeader);
                    continue;
                }

                if (context.RequestHeaders.TryGetValue(sourceHeader, out var vals) && vals.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation(targetHeader, vals);
                }
            }
        }

        // 2. Tenant ID Header Pushdown - strip any forwarded value first
        if (!string.IsNullOrWhiteSpace(descriptor.TenantIdHeaderName))
        {
            request.Headers.Remove(descriptor.TenantIdHeaderName);
            // SEC M-26: fail-closed - never call a tenant-scoped API without the caller's tenant.
            var tenantClaim = ResolveTenantClaim(context.Principal)
                ?? throw new SecurityException($"The HTTP data source requires a tenant header ('{descriptor.TenantIdHeaderName}'), but the caller has no tenant claim.");
            request.Headers.TryAddWithoutValidation(descriptor.TenantIdHeaderName, tenantClaim);
        }

        // 3. User Identity Header Pushdown (X-User-Sid) - strip any forwarded value first
        request.Headers.Remove("X-User-Sid");
        var userSid = context.Principal.GetUserSid();
        if (userSid != null)
        {
            request.Headers.TryAddWithoutValidation("X-User-Sid", userSid.Value.Value);
        }

        // 4. Authentication Mode
        request.Headers.Remove("Authorization");
        switch (descriptor.AuthMode)
        {
            case HttpAuthMode.ForwardBearerToken:
                if (context.RequestHeaders != null &&
                    context.RequestHeaders.TryGetValue("Authorization", out var authVals) &&
                    authVals.Length > 0 &&
                    !string.IsNullOrWhiteSpace(authVals[0]))
                {
                    var authStr = authVals[0];
                    if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authStr["Bearer ".Length..].Trim());
                    }
                    else
                    {
                        _logger.LogWarning("Security: Rejected forwarding non-Bearer Authorization header to downstream HTTP data source.");
                    }
                }
                break;

            case HttpAuthMode.StaticApiKey:
                if (!string.IsNullOrWhiteSpace(descriptor.ApiKeyHeaderName) &&
                    !string.IsNullOrWhiteSpace(descriptor.ApiKeySecretName))
                {
                    var resolvedKey = ResolveSecretValue(descriptor.ApiKeySecretName);
                    request.Headers.TryAddWithoutValidation(descriptor.ApiKeyHeaderName, resolvedKey);
                }
                break;

            case HttpAuthMode.ClientCredentials:
                if (!string.IsNullOrWhiteSpace(descriptor.ApiKeySecretName))
                {
                    var resolvedToken = ResolveSecretValue(descriptor.ApiKeySecretName);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolvedToken);
                }
                break;

            case HttpAuthMode.None:
            default:
                break;
        }
    }

    private string ResolveSecretValue(string secretRefOrValue)
    {
        // Review G5: the secret reference is never logged or embedded in exception messages (central resolver, description only).
        return Autheris.Application.Security.SecretReferenceResolver.Resolve(
                   _secretProvider,
                   secretRefOrValue,
                   _environment,
                   allowPlaintextInDevelopment: true,
                   _logger,
                   "declarative HTTP API credential")
               ?? throw new System.Security.SecurityException("The declarative HTTP API credential could not be resolved (fail-closed).");
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ExtractRowsFromJson(
        JsonElement root,
        string? jsonRootPath)
    {
        var targetElement = root;

        if (!string.IsNullOrWhiteSpace(jsonRootPath))
        {
            var segments = jsonRootPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (targetElement.ValueKind == JsonValueKind.Object && targetElement.TryGetProperty(segment, out var prop))
                {
                    targetElement = prop;
                }
                else
                {
                    return Array.Empty<IReadOnlyDictionary<string, object?>>();
                }
            }
        }

        var results = new List<IReadOnlyDictionary<string, object?>>();

        if (targetElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in targetElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    results.Add(FlattenJsonObject(item));
                }
            }
        }
        else if (targetElement.ValueKind == JsonValueKind.Object)
        {
            results.Add(FlattenJsonObject(targetElement));
        }

        return results;
    }

    private static IReadOnlyDictionary<string, object?> FlattenJsonObject(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElement(prop.Value);
        }

        return dict;
    }

    private static object? ConvertJsonElement(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.TryGetInt64(out var l) ? l : elem.GetDouble(),
            _ => elem.GetRawText()
        };
    }
}
