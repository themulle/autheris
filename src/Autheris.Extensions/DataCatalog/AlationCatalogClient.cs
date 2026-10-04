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
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Alation Integration API v2 client (<c>/integration/v2/table/</c>).
/// Same hardening level as the other catalog clients: URL validation + SSRF handler (via DI), API token authentication
/// (<c>TOKEN</c> header, optionally resolved through <see cref="IKeyVaultSecretProvider"/>), bounded response reading
/// and error propagation instead of silently returning an empty catalog.
/// </summary>
public sealed class AlationCatalogClient : IDataCatalogClient
{
    private const string TokenHeaderName = "TOKEN";

    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AlationCatalogClient> _logger;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IHostEnvironment? _environment;

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.Alation;

    public AlationCatalogClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<AlationCatalogClient> logger,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _secretProvider = secretProvider;
        _environment = environment;
    }

    /// <summary>Page size for the Alation table API (<c>limit</c>/<c>skip</c> paging).</summary>
    internal const int PageSize = 250;

    /// <summary>SEC E-08: upper bound for pages per sync (protects against endless paging).</summary>
    internal const int MaxPages = 100;

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var alationOpts = _options.Value.Catalog.Alation;
        if (string.IsNullOrWhiteSpace(alationOpts.BaseUrl))
        {
            throw new InvalidOperationException("Catalog:Alation:BaseUrl is not configured.");
        }

        var baseUrl = alationOpts.BaseUrl.TrimEnd('/');
        DeclarativeHttpDataSourceExecutor.ValidateUrl(new Uri(baseUrl));

        // SEC E-08: resolved before the first request; fails closed (SecurityException) when no usable token exists.
        var apiToken = ResolveApiToken(alationOpts);

        var tables = new List<CatalogTableAsset>();
        var skippedUnmapped = 0;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
            {
                _logger.LogWarning("Alation paging limit of {MaxPages} pages reached; remaining tables are not synchronized.", MaxPages);
                break;
            }

            var uriBuilder = new UriBuilder($"{baseUrl}/integration/v2/table/");
            uriBuilder.Query = $"limit={PageSize}&skip={page * PageSize}{(string.IsNullOrWhiteSpace(filter) ? "" : $"&name={Uri.EscapeDataString(filter)}")}";

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (apiToken != null)
            {
                request.Headers.TryAddWithoutValidation(TokenHeaderName, apiToken);
            }

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Alation tables query returned status {StatusCode}: {Reason}", response.StatusCode, response.ReasonPhrase);
                throw new HttpRequestException(
                    $"Alation tables query failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }

            var json = await CatalogHttpContent.ReadBoundedStringAsync(response, "Alation", ct).ConfigureAwait(false);
            var pageResult = ParseAlationTablesResponse(json, alationOpts.DataSourceToDomainMap);
            tables.AddRange(pageResult.Tables);
            skippedUnmapped += pageResult.SkippedUnmapped;

            if (pageResult.RawItemCount < PageSize)
            {
                break;
            }
        }

        if (skippedUnmapped > 0)
        {
            _logger.LogWarning(
                "Discarded {Count} Alation tables whose data source (ds_id) is not mapped in Catalog:Alation:DataSourceToDomainMap.",
                skippedUnmapped);
        }

        return tables;
    }

    public async Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var tables = await GetTablesAsync(table.TableName, ct).ConfigureAwait(false);
        return tables.FirstOrDefault(t => t.Identifier.Equals(table));
    }

    /// <summary>
    /// SEC E-08: <see cref="AlationOptions.ApiToken"/> is a secret reference resolved through <see cref="IKeyVaultSecretProvider"/>.
    /// Lookup errors are logged without the exception message (it may contain the reference or a raw token). A failed
    /// lookup, empty secret bytes, a missing provider or a "resolved" value equal to the reference itself (Development
    /// placeholder) fail closed with a <see cref="System.Security.SecurityException"/> – no token is sent. The only
    /// exception: <see cref="AlationOptions.AllowPlaintextApiTokenInDevelopment"/> in the Development environment.
    /// Returns null only when no token is configured at all.
    /// </summary>
    private string? ResolveApiToken(AlationOptions alationOpts) =>
        Autheris.Application.Security.SecretReferenceResolver.Resolve(
            _secretProvider,
            alationOpts.ApiToken,
            _environment,
            allowPlaintextInDevelopment: alationOpts.AllowPlaintextApiTokenInDevelopment,
            logger: _logger,
            secretDescription: "Alation API token");

    internal readonly record struct AlationPage(IReadOnlyList<CatalogTableAsset> Tables, int RawItemCount, int SkippedUnmapped);

    /// <summary>SEC E-08: <c>ds_id</c> must be a non-negative integer (number or numeric string).</summary>
    private static bool TryGetDataSourceId(JsonElement item, out string dsId)
    {
        dsId = string.Empty;
        if (!item.TryGetProperty("ds_id", out var dsProp))
        {
            return false;
        }

        if (dsProp.ValueKind == JsonValueKind.Number && dsProp.TryGetInt64(out var numeric) && numeric >= 0)
        {
            dsId = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (dsProp.ValueKind == JsonValueKind.String &&
            long.TryParse(dsProp.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            dsId = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    internal static AlationPage ParseAlationTablesResponse(string json, IReadOnlyDictionary<string, string> dataSourceToDomainMap)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("results", out var results) &&
                 results.ValueKind == JsonValueKind.Array)
        {
            items = results;
        }
        else
        {
            throw new JsonException("Unexpected Alation tables response format (expected an array or an object with 'results').");
        }

        var tables = new List<CatalogTableAsset>();
        var rawCount = 0;
        var skippedUnmapped = 0;
        foreach (var item in items.EnumerateArray())
        {
            rawCount++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : null;
            var schema = item.TryGetProperty("schema_name", out var sProp) && sProp.ValueKind == JsonValueKind.String
                ? sProp.GetString() ?? "dbo"
                : "dbo";
            // SEC E-08: explicit ds_id -> gateway domain mapping; unmapped or malformed data sources are discarded
            // instead of creating active "ds_<id>" ghost tables.
            if (!TryGetDataSourceId(item, out var dsId) ||
                !dataSourceToDomainMap.TryGetValue(dsId, out var domain) ||
                string.IsNullOrWhiteSpace(domain))
            {
                skippedUnmapped++;
                continue;
            }

            var classifications = new List<string>();
            if (item.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsElem.EnumerateArray())
                {
                    var tagName = tag.ValueKind switch
                    {
                        JsonValueKind.String => tag.GetString(),
                        JsonValueKind.Object when tag.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String => tn.GetString(),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(tagName))
                    {
                        classifications.Add(tagName);
                    }
                }
            }

            TableIdentifier identifier;
            try
            {
                identifier = new TableIdentifier(domain, schema, name);
            }
            catch (ArgumentException)
            {
                skippedUnmapped++;
                continue;
            }

            tables.Add(new CatalogTableAsset
            {
                Identifier = identifier,
                DisplayName = name,
                ExternalAssetId = id,
                Classifications = classifications,
                Tags = [.. classifications],
                SourceType = "Alation"
            });
        }

        return new AlationPage(tables, rawCount, skippedUnmapped);
    }
}
