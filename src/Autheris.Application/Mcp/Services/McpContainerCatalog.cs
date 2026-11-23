namespace Autheris.Application.Mcp.Services;

using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Visibility;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The <c>list_containers</c> MCP tool: business domains and data products as tiles with the caller's catalog access state
/// (<c>OPEN</c>, <c>GRANTED</c>, <c>PENDING</c>, <c>NONE</c>). Tiles carry container metadata only, never tables, columns or classes.
/// A host without the container catalog answers an empty list.
/// </summary>
public static class McpContainerCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = McpResultJson.Options;

    public static async Task<string> ListAsync(
        IContainerCatalogService? service,
        TenantId tenant,
        ClaimsPrincipal principal,
        JsonElement arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        ContainerKind? kind = null;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("kind", out var kindProp)
            && kindProp.ValueKind == JsonValueKind.String)
        {
            var text = kindProp.GetString();
            if (string.Equals(text, "domain", StringComparison.OrdinalIgnoreCase))
            {
                kind = ContainerKind.Domain;
            }
            else if (string.Equals(text, "dataProduct", StringComparison.OrdinalIgnoreCase))
            {
                kind = ContainerKind.DataProduct;
            }
        }

        int? limit = null;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("limit", out var limitProp)
            && limitProp.ValueKind == JsonValueKind.Number
            && limitProp.TryGetInt32(out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var tiles = service is null
            ? []
            : await service.ListAsync(tenant, principal, kind, limit, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { containers = tiles }, JsonOpts);
    }
}
