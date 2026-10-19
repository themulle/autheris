namespace Autheris.Infrastructure.Databricks;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Databricks token provider supporting OAuth M2M, PAT, and OAuth 2.0 On-Behalf-Of (OBO)
/// delegation for Unity Catalog native access enforcement.
/// Enforces fail-closed semantics (SEC-OBO238-01: zero fallback to service principal), user cache isolation (SEC-OBO238-03),
/// and zero token leakage in logs/exceptions (SEC-OBO238-02).
/// </summary>
public sealed class DatabricksOboTokenProvider : IDatabricksCredentialProvider, IDelegatedCredentialProvider, IDelegationCapability
{
    private const int MaxTokenResponseBytes = 64 * 1024;

    private readonly HttpClient _http;
    private readonly IOptions<GatewayOptions>? _gatewayOptions;
    private readonly IKeyVaultSecretProvider? _secrets;
    private readonly IHostEnvironment? _environment;
    private readonly DatabricksTokenCache _tokenCache;
    private readonly ILogger<DatabricksOboTokenProvider>? _logger;
    private readonly TimeProvider _time;

    public DatabricksOboTokenProvider(
        HttpClient http,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IKeyVaultSecretProvider? secrets = null,
        IHostEnvironment? environment = null,
        DatabricksTokenCache? tokenCache = null,
        ILogger<DatabricksOboTokenProvider>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _gatewayOptions = gatewayOptions;
        _secrets = secrets;
        _environment = environment;
        _tokenCache = tokenCache ?? new DatabricksTokenCache();
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlySet<DelegationKind> SupportedDelegationKinds { get; } =
        new HashSet<DelegationKind> { DelegationKind.TokenDelegation };

    public bool SourceEnforcesRowSecurity => true;
    public bool SourceEnforcesMasking => true;
    public bool SupportsCorrelationTag => true;

    public async Task<string> AcquireTokenAsync(DatabricksDataSourceOptions db, DelegationContext? context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var auth = db.Auth;
        if (auth.Mode == DatabricksAuthMode.Pat)
        {
            if (_environment?.IsDevelopment() == false)
            {
                throw new DatabricksProviderException(DatabricksErrorCodes.Configuration, "Auth.Mode Pat is allowed in Development only.");
            }

            return SecretReferenceResolver.Resolve(_secrets, auth.PatRef, _environment, true, _logger, "Databricks PAT")
                   ?? throw new DatabricksProviderException(DatabricksErrorCodes.Configuration, "The Databricks PAT reference is missing.");
        }

        if (auth.Mode == DatabricksAuthMode.OAuthObo)
        {
            return await AcquireOboTokenAsync(db, context, ct).ConfigureAwait(false);
        }

        // Default: OAuthM2M
        return await AcquireM2MTokenAsync(db, ct).ConfigureAwait(false);
    }

    public Task<string> GetCredentialAsync(string dataSourceName, DelegationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSourceName);
        ArgumentNullException.ThrowIfNull(context);

        if (_gatewayOptions?.Value.DataSources.Connections is not { } connections ||
            !connections.TryGetValue(dataSourceName, out var connection) ||
            connection.Databricks is null)
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.Configuration, $"Data source '{dataSourceName}' is not a configured Databricks data source.");
        }

        return AcquireTokenAsync(connection.Databricks, context, ct);
    }

    private async Task<string> AcquireOboTokenAsync(DatabricksDataSourceOptions db, DelegationContext? context, CancellationToken ct)
    {
        var obo = db.Auth.Obo ?? throw new DatabricksProviderException(
            DatabricksErrorCodes.Configuration,
            "OAuthObo mode requires Auth.Obo configuration.");

        if (string.IsNullOrWhiteSpace(obo.ClientId))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Configuration,
                "Auth.Obo.ClientId is required for OAuthObo mode.");
        }

        // SEC-OBO238-01: Zero fallback policy! Missing caller token MUST fail closed.
        if (string.IsNullOrWhiteSpace(context?.CallerToken))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.AuthFailed,
                "Databricks OBO authentication failed: caller token assertion is missing or empty.");
        }

        var rawToken = context.CallerToken.Trim();
        if (rawToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            rawToken = rawToken["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrEmpty(rawToken))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.AuthFailed,
                "Databricks OBO authentication failed: caller token assertion is missing or empty.");
        }

        var now = _time.GetUtcNow();

        // Check if caller token assertion is already expired
        var callerExp = ExtractJwtExpiry(rawToken);
        if (callerExp is { } exp && exp <= now)
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.AuthFailed,
                "Databricks OBO authentication failed: caller token assertion is expired.");
        }

        // Partition cache by SHA-256 hash of caller assertion (SEC-OBO238-03)
        var assertionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        if (_tokenCache.TryGet(db.Host, obo.ClientId, assertionHash, now, out var cached))
        {
            return cached;
        }

        var secret = SecretReferenceResolver.Resolve(_secrets, obo.ClientSecretRef, _environment, true, _logger, "Databricks OBO client secret");
        if (string.IsNullOrWhiteSpace(secret) && string.IsNullOrWhiteSpace(obo.ClientCertificateRef))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Configuration,
                "Databricks OBO client credentials (secret or certificate) could not be resolved.");
        }

        string authority = obo.Authority ?? string.Empty;
        if (string.IsNullOrWhiteSpace(authority))
        {
            var tenant = !string.IsNullOrWhiteSpace(obo.TenantId) ? obo.TenantId : "common";
            authority = $"https://login.microsoftonline.com/{tenant}";
        }

        authority = authority.TrimEnd('/');
        var tokenEndpoint = new Uri($"{authority}/oauth2/v2.0/token");

        var scopes = !string.IsNullOrWhiteSpace(obo.Scopes)
            ? obo.Scopes
            : "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";

        var formParams = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["client_id"] = obo.ClientId,
            ["client_secret"] = secret ?? string.Empty,
            ["assertion"] = rawToken,
            ["scope"] = scopes,
            ["requested_token_use"] = "on_behalf_of"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formParams)
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, MaxTokenResponseBytes, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string? oauthError = null;
            try
            {
                using var errorDoc = JsonDocument.Parse(bytes);
                if (errorDoc.RootElement.TryGetProperty("error", out var errEl))
                {
                    oauthError = errEl.GetString();
                }
            }
            catch (JsonException)
            {
                // Non-JSON error body
            }

            // SEC-OBO238-02: Never log or bubble raw tokens or assertions
            _logger?.LogWarning("Databricks OBO token exchange failed with HTTP {Status} (error: {OAuthError}).",
                (int)response.StatusCode, oauthError ?? "unknown");

            throw new DatabricksProviderException(
                DatabricksErrorCodes.AuthFailed,
                $"Databricks OBO authentication failed ({oauthError ?? "HTTP " + (int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl) ||
            tokenEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(tokenEl.GetString()))
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.AuthFailed, "Databricks OBO authentication returned no access token.");
        }

        var accessToken = tokenEl.GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var seconds) ? seconds : 300;

        // TTL is min(downstream_token_exp, caller_assertion_exp) clamped to safe window
        var downstreamExp = now.AddSeconds(expiresIn);
        var effectiveLimit = callerExp is { } cExp && cExp < downstreamExp ? cExp : downstreamExp;
        var effectiveExp = effectiveLimit.AddSeconds(-60);
        if (effectiveExp <= now.AddSeconds(30))
        {
            effectiveExp = now.AddSeconds(30);
        }

        _tokenCache.Set(db.Host, obo.ClientId, assertionHash, accessToken, effectiveExp);
        return accessToken;
    }

    private async Task<string> AcquireM2MTokenAsync(DatabricksDataSourceOptions db, CancellationToken ct)
    {
        var auth = db.Auth;
        if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecretRef))
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.Configuration, "OAuthM2M requires Auth.ClientId and Auth.ClientSecretRef.");
        }

        var now = _time.GetUtcNow();
        if (_tokenCache.TryGet(db.Host, auth.ClientId, string.Empty, now, out var cached))
        {
            return cached;
        }

        var secret = SecretReferenceResolver.Resolve(_secrets, auth.ClientSecretRef, _environment, true, _logger, "Databricks client secret")
                     ?? throw new DatabricksProviderException(DatabricksErrorCodes.Configuration, "The Databricks client secret could not be resolved.");

        // SEC-ADG-09 item 3: the token endpoint is derived from the host, never configurable.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{db.Host}/oidc/v1/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "all-apis" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.ClientId}:{secret}")));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.AuthFailed, $"Databricks authentication failed (HTTP {(int)response.StatusCode}).");
        }

        var bytes = await ReadBoundedAsync(response, MaxTokenResponseBytes, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tokenEl.GetString()))
        {
            throw new DatabricksProviderException(DatabricksErrorCodes.AuthFailed, "Databricks authentication returned no access token.");
        }

        var token = tokenEl.GetString()!;
        var lifetime = doc.RootElement.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 300;
        _tokenCache.Set(db.Host, auth.ClientId, string.Empty, token, now.AddSeconds(Math.Max(30, lifetime - 60)));
        return token;
    }

    private static DateTimeOffset? ExtractJwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var expSecs))
            {
                return DateTimeOffset.FromUnixTimeSeconds(expSecs);
            }
        }
        catch
        {
            // Ignore format/json parsing failures on non-standard tokens
        }

        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new ArrayBufferWriter<byte>(8192);
        while (true)
        {
            var memory = buffer.GetMemory(8192);
            var read = await stream.ReadAsync(memory, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenSpan.ToArray();
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                throw new DatabricksProviderException(DatabricksErrorCodes.ResultTooLarge, "Databricks token response exceeded the allowed size limit.");
            }
        }
    }
}
