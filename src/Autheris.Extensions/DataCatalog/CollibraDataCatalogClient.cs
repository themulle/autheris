namespace Autheris.Extensions.DataCatalog;

using System;
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

public sealed class CollibraDataCatalogClient : IDataCatalogClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<CollibraDataCatalogClient> _logger;

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.Collibra;

    private readonly Microsoft.Extensions.Hosting.IHostEnvironment? _environment;
    private readonly Autheris.Application.Interfaces.IKeyVaultSecretProvider? _secretProvider;

    public CollibraDataCatalogClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<CollibraDataCatalogClient> logger,
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
        var collibraOpts = _options.Value.Catalog.Collibra;
        var baseUrl = string.IsNullOrWhiteSpace(collibraOpts.BaseUrl) ? "https://collibra.corp.internal" : collibraOpts.BaseUrl.TrimEnd('/');
        DeclarativeHttpDataSourceExecutor.ValidateUrl(new Uri(baseUrl));

        var uriBuilder = new UriBuilder($"{baseUrl}/rest/2.0/assets");
        uriBuilder.Query = $"limit=100&offset=0{(string.IsNullOrWhiteSpace(filter) ? "" : $"&name={Uri.EscapeDataString(filter)}")}";

        using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
        ApplyAuthentication(request, collibraOpts);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Collibra assets query returned status {StatusCode}: {Reason}", response.StatusCode, response.ReasonPhrase);
            return [];
        }

        var json = await CatalogHttpContent.ReadBoundedStringAsync(response, "Collibra", ct).ConfigureAwait(false);
        return ParseCollibraAssetsResponse(json);
    }

    public async Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var tables = await GetTablesAsync(table.TableName, ct).ConfigureAwait(false);
        return tables.FirstOrDefault(t => t.Identifier.Equals(table));
    }

    private void ApplyAuthentication(HttpRequestMessage request, CollibraOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.ApiToken))
        {
            var token = Autheris.Application.Security.SecretReferenceResolver.Resolve(
                _secretProvider,
                opts.ApiToken,
                _environment,
                allowPlaintextInDevelopment: true,
                logger: _logger,
                secretDescription: "Collibra ApiToken");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? opts.ApiToken);
        }
        else if (!string.IsNullOrWhiteSpace(opts.Username) && !string.IsNullOrWhiteSpace(opts.Password))
        {
            var password = Autheris.Application.Security.SecretReferenceResolver.Resolve(
                _secretProvider,
                opts.Password,
                _environment,
                allowPlaintextInDevelopment: true,
                logger: _logger,
                secretDescription: "Collibra Password");
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{opts.Username}:{password ?? opts.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
    }

    private static IReadOnlyList<CatalogTableAsset> ParseCollibraAssetsResponse(string json)
    {
        var result = new List<CatalogTableAsset>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in results.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                var tableId = new TableIdentifier("collibra", "dbo", name);
                result.Add(new CatalogTableAsset
                {
                    Identifier = tableId,
                    DisplayName = name,
                    ExternalAssetId = id,
                    SourceType = "Collibra"
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
