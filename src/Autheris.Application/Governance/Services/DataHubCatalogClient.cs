namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Microsoft.Extensions.Logging;

public sealed class DataHubCatalogClient : IDataHubCatalogClient
{
    private readonly IDataContractEngine _contractEngine;
    private readonly ILogger<DataHubCatalogClient> _logger;

    public DataHubCatalogClient(
        IDataContractEngine contractEngine,
        ILogger<DataHubCatalogClient> logger)
    {
        _contractEngine = contractEngine ?? throw new ArgumentNullException(nameof(contractEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IngestMetadataChangeEventAsync(string mclPayloadJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mclPayloadJson);

        try
        {
            using var doc = JsonDocument.Parse(mclPayloadJson);
            var root = doc.RootElement;

            // Check if this is an assertion run event or dataset status change
            if (root.TryGetProperty("entityType", out var entityType) &&
                entityType.GetString() == "assertion" &&
                root.TryGetProperty("aspect", out var aspect))
            {
                var assertionUrn = root.TryGetProperty("entityUrn", out var urn) ? urn.GetString() ?? "" : "";
                var result = aspect.TryGetProperty("type", out var t) ? t.GetString() : "SUCCESS";
                var isPassing = string.Equals(result, "SUCCESS", StringComparison.OrdinalIgnoreCase);

                _logger.LogInformation("Received DataHub assertion update for {Urn}: Passing={IsPassing}", assertionUrn, isPassing);
                return true;
            }

            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse DataHub MCL payload.");
            return false;
        }
    }

    public Task<IReadOnlyList<string>> FetchExternalTagsAsync(TableIdentifier table, CancellationToken cancellationToken = default)
    {
        // Fallback / mock for external tags in air-gapped / unit test environments
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
