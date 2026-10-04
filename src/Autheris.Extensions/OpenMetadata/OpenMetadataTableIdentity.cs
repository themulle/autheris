using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

namespace Autheris.Extensions.OpenMetadata;

/// <summary>
/// SEC E-09 / EX-06: Shared mapping of an OpenMetadata table to a gateway <see cref="TableIdentifier"/> for the
/// permission sync, the OM webhook and the OpenMetadata catalog provider.
/// <list type="bullet">
/// <item><c>OpenMetadata.ServiceFilter</c> is enforced per table (not only as a client-side query hint).</item>
/// <item>With <c>OpenMetadata.ServiceDatabaseToDomainMap</c> configured, the domain is taken from the
/// <c>"service.database"</c> entry; tables without an entry are rejected.</item>
/// <item>Without a map the domain is the service name (legacy). Tables of different databases that collapse onto the same
/// <c>service.schema.table</c> are rejected by <see cref="RejectDatabaseCollisions{T}"/> instead of overwriting each other.</item>
/// </list>
/// </summary>
internal static class OpenMetadataTableIdentity
{
    internal readonly record struct ResolvedTable(TableIdentifier Identifier, string Service, string? Database, TenantId TenantId);

    public static string? GetServiceName(OpenMetadataTable table)
    {
        if (table.Service is { } service && !string.IsNullOrWhiteSpace(service.Name))
        {
            return service.Name;
        }

        var parts = SplitFqn(table);
        return parts.Length >= 3 ? parts[0] : null;
    }

    public static string? GetDatabaseName(OpenMetadataTable table)
    {
        if (table.Database is { } database && !string.IsNullOrWhiteSpace(database.Name))
        {
            return database.Name;
        }

        var parts = SplitFqn(table);
        return parts.Length >= 4 ? parts[1] : null;
    }

    /// <summary>True when no ServiceFilter is configured or the table belongs to the configured service.</summary>
    public static bool IsServiceAllowed(OpenMetadataTable table, OpenMetadataOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceFilter))
        {
            return true;
        }

        var filter = options.ServiceFilter.Trim();
        var service = GetServiceName(table);
        return string.Equals(service, filter, StringComparison.OrdinalIgnoreCase) ||
               (table.Service is { } serviceRef && !string.IsNullOrWhiteSpace(serviceRef.FullyQualifiedName) &&
                string.Equals(serviceRef.FullyQualifiedName, filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the gateway identity. Returns false (with a reason) for tables outside the ServiceFilter, tables without
    /// a domain mapping (when the map is configured) and tables whose name parts are not valid identifier components.
    /// </summary>
    public static bool TryResolve(OpenMetadataTable table, OpenMetadataOptions options, out ResolvedTable resolved, out string reason)
    {
        resolved = default;

        if (!IsServiceAllowed(table, options))
        {
            reason = $"service '{GetServiceName(table)}' is outside OpenMetadata.ServiceFilter";
            return false;
        }

        var parts = SplitFqn(table);
        var service = GetServiceName(table);
        var database = GetDatabaseName(table);

        var schema = table.DatabaseSchema?.Name;
        if (string.IsNullOrWhiteSpace(schema))
        {
            schema = parts.Length >= 4 ? parts[2] : parts.Length >= 2 ? parts[^2] : null;
        }

        var tableName = !string.IsNullOrWhiteSpace(table.Name) ? table.Name : (parts.Length > 0 ? parts[^1] : null);

        string? domain;
        if (options.ServiceDatabaseToDomainMap.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(database) ||
                !options.ServiceDatabaseToDomainMap.TryGetValue($"{service}.{database}", out domain) ||
                string.IsNullOrWhiteSpace(domain))
            {
                reason = $"no OpenMetadata.ServiceDatabaseToDomainMap entry for '{service}.{database}'";
                return false;
            }
        }
        else
        {
            domain = string.IsNullOrWhiteSpace(service) ? "default" : service;
        }

        schema = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema;
        if (string.IsNullOrWhiteSpace(tableName))
        {
            reason = "table name is empty";
            return false;
        }

        if (!TryResolveTenant(service, database, options, out var tenantId, out var tenantReason))
        {
            reason = tenantReason;
            return false;
        }

        try
        {
            resolved = new ResolvedTable(new TableIdentifier(domain, schema, tableName), service ?? domain, database, tenantId);
        }
        catch (ArgumentException)
        {
            reason = "table identifier components are invalid";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// EX-03: Resolves the target tenant for an OpenMetadata entity based on service/database mappings or default tenant.
    /// Returns false if no tenant can be resolved (fail-closed).
    /// </summary>
    public static bool TryResolveTenant(
        string? service,
        string? database,
        OpenMetadataOptions options,
        out TenantId tenantId,
        out string reason)
    {
        tenantId = default;
        string? tenantStr = null;

        if (!string.IsNullOrWhiteSpace(service) && !string.IsNullOrWhiteSpace(database))
        {
            options.ServiceDatabaseToTenantMap.TryGetValue($"{service}.{database}", out tenantStr);
        }

        if (string.IsNullOrWhiteSpace(tenantStr) && !string.IsNullOrWhiteSpace(service))
        {
            options.ServiceDatabaseToTenantMap.TryGetValue(service, out tenantStr);
        }

        if (string.IsNullOrWhiteSpace(tenantStr) && !string.IsNullOrWhiteSpace(options.DefaultTenantId))
        {
            tenantStr = options.DefaultTenantId;
        }

        if (string.IsNullOrWhiteSpace(tenantStr))
        {
            reason = $"no tenant mapping for '{service}.{database}' (configure OpenMetadata.ServiceDatabaseToTenantMap or OpenMetadata.DefaultTenantId)";
            return false;
        }

        try
        {
            tenantId = new TenantId(tenantStr.Trim());
            reason = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            reason = $"invalid tenant identifier '{tenantStr}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Without a ServiceDatabaseToDomainMap the database is not part of the identity. Groups of tables that resolve to the
    /// same identity but come from different databases are rejected entirely (no last-writer-wins overwrite).
    /// </summary>
    public static List<(T Item, ResolvedTable Resolved)> RejectDatabaseCollisions<T>(
        IEnumerable<(T Item, ResolvedTable Resolved)> candidates,
        Action<TableIdentifier> onCollision)
    {
        var list = candidates.ToList();
        var databasesById = new Dictionary<TableIdentifier, HashSet<string>>();
        foreach (var (_, resolved) in list)
        {
            if (!databasesById.TryGetValue(resolved.Identifier, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                databasesById[resolved.Identifier] = set;
            }

            set.Add(resolved.Database ?? string.Empty);
        }

        var collided = new HashSet<TableIdentifier>();
        foreach (var (id, set) in databasesById)
        {
            if (set.Count > 1)
            {
                collided.Add(id);
                onCollision(id);
            }
        }

        return collided.Count == 0 ? list : list.Where(c => !collided.Contains(c.Resolved.Identifier)).ToList();
    }

    private static string[] SplitFqn(OpenMetadataTable table) =>
        string.IsNullOrWhiteSpace(table.FullyQualifiedName) ? [] : table.FullyQualifiedName.Split('.');
}
