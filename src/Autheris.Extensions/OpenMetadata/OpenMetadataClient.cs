using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Extensions.OpenMetadata;

public sealed class OpenMetadataClient : IOpenMetadataClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenMetadataClient> _logger;
    private const long MaxAllowedResponseBytes = 10 * 1024 * 1024; // 10 MB maximum payload cap
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record PagedResponse<T>(
        [property: JsonPropertyName("data")] List<T>? Data,
        [property: JsonPropertyName("paging")] PagingInfo? Paging);

    private sealed record PagingInfo(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("after")] string? After);

    public OpenMetadataClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<OpenMetadataClient> logger,
        Autheris.Application.Interfaces.IKeyVaultSecretProvider? secretProvider = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        var omOptions = options.Value.OpenMetadata;
        if (!string.IsNullOrWhiteSpace(omOptions.ServerUrl) && _httpClient.BaseAddress == null)
        {
            var serverUrl = omOptions.ServerUrl.TrimEnd('/') + "/";
            _httpClient.BaseAddress = new Uri(serverUrl);
        }

        var authToken = SecretReferenceResolver.Resolve(
            secretProvider,
            omOptions.AuthToken,
            environment,
            allowPlaintextInDevelopment: true,
            logger: _logger,
            secretDescription: "OpenMetadata auth token");

        if (!string.IsNullOrWhiteSpace(authToken) &&
            _httpClient.DefaultRequestHeaders.Authorization == null)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", authToken);
        }
    }

    public async Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default)
    {
        var relativeUrl = "tables?limit=1000&fields=columns,tags,owners,database,databaseSchema,service";
        var tables = await GetPagedEntitiesAsync<OpenMetadataTable>(relativeUrl, ct);

        if (!string.IsNullOrWhiteSpace(service))
        {
            return tables.Where(t =>
                string.Equals(t.Service?.Name, service, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Service?.FullyQualifiedName, service, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return tables;
    }

    public async Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqn);

        var relativeUrl = $"tables/name/{Uri.EscapeDataString(fqn)}?fields=columns,tags,owners,database,databaseSchema,service";
        using var response = await _httpClient.GetAsync(relativeUrl, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await BoundedHttpContent.ReadBoundedStreamAsync(response, "OpenMetadata", MaxAllowedResponseBytes, ct);
        return await JsonSerializer.DeserializeAsync<OpenMetadataTable>(stream, JsonOptions, ct);
    }

    public Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataPolicy>("policies?limit=1000&fields=rules,location", ct);

    public Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataRole>("roles?limit=1000&fields=policies,users,teams", ct);

    public Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataTeam>("teams?limit=1000&fields=defaultRoles,policies,users", ct);

    public Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataUser>("users?limit=1000&fields=roles,teams", ct);

    private async Task<IReadOnlyList<T>> GetPagedEntitiesAsync<T>(string baseUrl, CancellationToken ct)
    {
        var allItems = new List<T>();
        string? afterCursor = null;
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        const int maxPages = 1000;
        int pageCount = 0;

        try
        {
            do
            {
                if (++pageCount > maxPages)
                {
                    _logger.LogError("Pagination limit of {MaxPages} pages reached while fetching from {Url}. Aborting to prevent partial synchronization.", maxPages, baseUrl);
                    throw new InvalidOperationException($"Pagination limit of {maxPages} pages reached while fetching from {baseUrl}. Aborting to prevent partial synchronization.");
                }

                if (!string.IsNullOrEmpty(afterCursor) && !visitedCursors.Add(afterCursor))
                {
                    _logger.LogError("Duplicate cursor detected '{Cursor}' while fetching from {Url}. Aborting to prevent partial synchronization.", afterCursor, baseUrl);
                    throw new InvalidOperationException($"Duplicate cursor detected '{afterCursor}' while fetching from {baseUrl}. Aborting to prevent partial synchronization.");
                }

                var separator = baseUrl.Contains('?') ? "&" : "?";
                var url = string.IsNullOrEmpty(afterCursor)
                    ? baseUrl
                    : $"{baseUrl}{separator}after={Uri.EscapeDataString(afterCursor)}";

                using var response = await _httpClient.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                await using var stream = await BoundedHttpContent.ReadBoundedStreamAsync(response, "OpenMetadata", MaxAllowedResponseBytes, ct);
                var paged = await JsonSerializer.DeserializeAsync<PagedResponse<T>>(stream, JsonOptions, ct);

                if (paged?.Data == null || paged.Data.Count == 0)
                {
                    break;
                }

                allItems.AddRange(paged.Data);
                afterCursor = paged.Paging?.After;
            } while (!string.IsNullOrEmpty(afterCursor));

            return allItems;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to retrieve OpenMetadata entities from endpoint: {Url}", baseUrl);
            throw;
        }
    }
}
