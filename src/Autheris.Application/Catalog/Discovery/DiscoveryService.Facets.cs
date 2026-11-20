namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class DiscoveryService
{
    public async ValueTask<DiscoveryFacets> FacetsAsync(
        DiscoveryAccessState? access,
        CatalogCaller caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();

        var sid = await DiscoveryCallerGate.EnsureAllowedAsync(
            caller, _discoveryOptions, _gatewayOptions, _rateLimiter, ct).ConfigureAwait(false);

        if (_metadataRepository is null || _decisionPoint is null || _consentRepository is null)
        {
            RecordFailure("Required discovery dependency missing.");
            return new DiscoveryFacets(0, []);
        }

        try
        {
            var scopeKey = BuildScopeMemoKey(sid, caller);
            var candidates = await LoadPreFilteredTablesAsync(
                caller,
                sid,
                scopeKey,
                domain: null,
                schema: null,
                kind: null,
                tag: null,
                sensitivity: null,
                search: null,
                access: access,
                metadataPredicate: null,
                ct: ct).ConfigureAwait(false);

            var facets = BuildFacets(candidates);
            await WriteFacetsAuditAsync(caller.Tenant, sid, access, facets, ct).ConfigureAwait(false);
            return facets;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DiscoveryStatusException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex, "Discovery FacetsAsync failed closed.");
            return new DiscoveryFacets(0, []);
        }
    }

    internal static DiscoveryFacets BuildFacets(IReadOnlyList<TableMetadata> tables)
    {
        if (tables.Count == 0)
        {
            return new DiscoveryFacets(0, []);
        }

        var domainBuckets = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var domain = table.Identifier.Domain;
            var schema = table.Identifier.Schema;
            if (!domainBuckets.TryGetValue(domain, out var schemas))
            {
                schemas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                domainBuckets[domain] = schemas;
            }

            schemas[schema] = schemas.TryGetValue(schema, out var count) ? count + 1 : 1;
        }

        var domains = new List<DiscoveryDomainFacet>(domainBuckets.Count);
        foreach (var (domain, schemas) in domainBuckets.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase).ThenBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            var schemaList = schemas
                .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => new DiscoverySchemaFacet(kv.Key, kv.Value))
                .ToList();
            var domainCount = schemaList.Sum(static s => s.Count);
            domains.Add(new DiscoveryDomainFacet(domain, domainCount, schemaList));
        }

        return new DiscoveryFacets(tables.Count, domains);
    }

    private async Task WriteFacetsAuditAsync(
        TenantId tenant,
        Sid actorSid,
        DiscoveryAccessState? access,
        DiscoveryFacets facets,
        CancellationToken ct)
    {
        if (_auditWriter is null)
        {
            return;
        }

        var entry = new AuditLogEntry
        {
            TenantId = tenant,
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorSid = actorSid,
            EventType = AuditEventTypes.CatalogRead,
            TargetTable = "*",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                surface = SurfaceName(DiscoverySurface.Rest),
                operation = "facets",
                accessStatus = access?.ToString().ToLowerInvariant(),
                totalCount = facets.TotalCount,
                domainCount = facets.Domains.Count,
                schemaCount = facets.Domains.Sum(static d => d.Schemas.Count),
                tenant = tenant.Value
            })
        };

        await _auditWriter.RecordAsync(entry, ct).ConfigureAwait(false);
    }
}
