namespace Autheris.Extensions.DataCatalog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;

public sealed class PurviewDataCatalogClient : IDataCatalogClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<PurviewDataCatalogClient> _logger;

    private static string? _cachedToken;
    private static DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.MicrosoftPurview;

    private readonly Microsoft.Extensions.Hosting.IHostEnvironment? _environment;
    private readonly Autheris.Application.Interfaces.IKeyVaultSecretProvider? _secretProvider;

    public PurviewDataCatalogClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<PurviewDataCatalogClient> logger,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        Autheris.Application.Interfaces.IKeyVaultSecretProvider? secretProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment;
        _secretProvider = secretProvider;
    }

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var purviewOpts = _options.Value.Catalog.Purview;
        var endpoint = GetEffectiveEndpoint(purviewOpts);
        DeclarativeHttpDataSourceExecutor.ValidateUrl(new Uri(endpoint));

        var token = await AcquireTokenAsync(purviewOpts, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/datamap/api/atlas/v2/search/advanced");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var searchPayload = new
        {
            query = string.IsNullOrWhiteSpace(filter) ? "*" : filter,
            filter = new
            {
                typeName = "rdbms_table"
            },
            limit = 100
        };

        request.Content = new StringContent(JsonSerializer.Serialize(searchPayload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Purview search returned status {StatusCode}: {Reason}", response.StatusCode, response.ReasonPhrase);
            return [];
        }

        var json = await CatalogHttpContent.ReadBoundedStringAsync(response, "Purview", ct).ConfigureAwait(false);
        return ParseAtlasSearchResponse(json);
    }

    public async Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var tables = await GetTablesAsync(table.TableName, ct).ConfigureAwait(false);
        return tables.FirstOrDefault(t => t.Identifier.Equals(table));
    }

    private string GetEffectiveEndpoint(PurviewOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            return opts.Endpoint.TrimEnd('/');
        }
        if (!string.IsNullOrWhiteSpace(opts.AccountName))
        {
            return $"https://{opts.AccountName}.purview.azure.com";
        }
        return "https://api.purview.azure.com";
    }

    private async Task<string> AcquireTokenAsync(PurviewOptions opts, CancellationToken ct)
    {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _cachedToken;
        }

        await TokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return _cachedToken;
            }

            var isDev = _environment != null &&
                        string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(opts.TenantId) || string.IsNullOrWhiteSpace(opts.ClientId) || string.IsNullOrWhiteSpace(opts.ClientSecret))
            {
                if (!isDev)
                {
                    throw new System.Security.SecurityException(
                        "Microsoft Purview credentials (TenantId, ClientId, ClientSecret) must be configured in non-Development environment.");
                }

                // Fallback for mocked/dev environment only
                _cachedToken = "purview-dev-mock-bearer-token";
                _tokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
                return _cachedToken;
            }

            var resolvedSecret = Autheris.Application.Security.SecretReferenceResolver.Resolve(
                _secretProvider,
                opts.ClientSecret,
                _environment,
                allowPlaintextInDevelopment: true,
                logger: _logger,
                secretDescription: "Purview ClientSecret");

            var tokenEndpoint = $"https://login.microsoftonline.com/{opts.TenantId}/oauth2/v2.0/token";
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = opts.ClientId,
                ["client_secret"] = resolvedSecret ?? opts.ClientSecret,
                ["scope"] = "https://purview.azure.net/.default"
            };
            tokenReq.Content = new FormUrlEncodedContent(form);

            using var tokenResp = await _httpClient.SendAsync(tokenReq, ct).ConfigureAwait(false);
            tokenResp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await CatalogHttpContent.ReadBoundedStringAsync(tokenResp, "Purview token endpoint", ct).ConfigureAwait(false));
            var root = doc.RootElement;
            _cachedToken = root.GetProperty("access_token").GetString()!;
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            return _cachedToken;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static IReadOnlyList<CatalogTableAsset> ParseAtlasSearchResponse(string json)
    {
        var result = new List<CatalogTableAsset>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var entity in entities.EnumerateArray())
            {
                var qualifiedName = entity.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("qualifiedName", out var qn)
                    ? qn.GetString()
                    : null;
                var tableName = attrs.TryGetProperty("name", out var n) ? n.GetString() : "unknown";

                var parts = qualifiedName?.Split('/') ?? [];
                var domain = parts.Length > 0 ? parts[0] : "default";
                var schema = parts.Length > 1 ? parts[1] : "dbo";
                var tableId = new TableIdentifier(domain, schema, tableName ?? "unknown");

                var classifications = new List<string>();
                if (entity.TryGetProperty("classificationNames", out var cNames) && cNames.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cNames.EnumerateArray())
                    {
                        if (c.GetString() is { } cStr) classifications.Add(cStr);
                    }
                }

                result.Add(new CatalogTableAsset
                {
                    Identifier = tableId,
                    DisplayName = tableName,
                    Classifications = classifications,
                    SourceType = "MicrosoftPurview"
                });
            }
        }
        catch
        {
            // Resilient fallback on parsing error
        }

        return result;
    }
}
