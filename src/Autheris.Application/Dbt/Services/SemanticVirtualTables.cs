namespace Autheris.Application.Dbt.Services;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The catalog identity of the virtual tables a semantic manifest import creates (#112 A1-4, SEC-112-14/-15/-23). One place decides
/// what a virtual table is and how its tenant schema is named, so the visibility engine, the metadata listing and the ingestion
/// service can never classify the same table differently.
/// </summary>
public static partial class SemanticVirtualTables
{
    /// <summary>The catalog domain only the ingestion service writes.</summary>
    public const string Domain = "semantic";

    /// <summary>The <c>DocumentationSource</c> the ingestion service sets (server-set marker).</summary>
    public const string SourceMarker = "dbt";

    [GeneratedRegex("^t_[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex TenantSchemaPattern();

    /// <summary>The schema of the tenant's virtual tables (every tenant, the default tenant included): <c>t_</c> plus the first 16 lower-case hex digits of SHA-256 of the tenant id.</summary>
    public static string TenantSchema(TenantId tenant) =>
        "t_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant.Value)))[..16];

    public static bool IsTenantSchemaName(string? schema) => schema is not null && TenantSchemaPattern().IsMatch(schema);

    /// <summary>The schema name the ingestion service uses for <paramref name="tenant"/>; derived from the server-resolved tenant only.</summary>
    public static string SchemaFor(TenantId tenant) => TenantSchema(tenant);

    public static TableIdentifier IdentifierFor(TenantId tenant, string modelName) =>
        new(Domain, SchemaFor(tenant), modelName);

    /// <summary>
    /// True for a table the ingestion service created: domain <c>semantic</c>, the server-set source marker and a tenant schema. A real table that merely has a schema named like a tenant hash is not one.
    /// </summary>
    public static bool IsVirtualTable(TableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return IsVirtualIdentity(table.Identifier) &&
               string.Equals(table.Table?.DocumentationSource, SourceMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Identity part of <see cref="IsVirtualTable"/> (for call sites that hold only the identifier).</summary>
    public static bool IsVirtualIdentity(TableIdentifier id) =>
        string.Equals(id.Domain, Domain, StringComparison.OrdinalIgnoreCase) &&
        IsTenantSchemaName(id.Schema);

    /// <summary>
    /// True for every identifier in the semantic catalog domain, well-formed or not. The visibility engine drops such a table unless it
    /// <see cref="BelongsToTenant"/> (a malformed or empty schema belongs to nobody: fail closed).
    /// </summary>
    public static bool IsInSemanticDomain(TableIdentifier id) => string.Equals(id.Domain, Domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="table"/> is a virtual table in the tenant schema of <paramref name="tenant"/>; an unset tenant owns nothing.
    /// </summary>
    public static bool BelongsToTenant(TableMetadata table, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (tenant.Value is null || !IsVirtualTable(table))
        {
            return false;
        }

        var expected = TenantSchema(tenant);
        return string.Equals(table.Identifier.Schema, expected, StringComparison.Ordinal) &&
               string.Equals(table.Table?.SchemaName, expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEC-112-15: the persistence boundary of the catalog keeps the virtual-table namespace to the ingestion service. A table in the
    /// semantic domain must be a well-formed, marked virtual table, and no other table may use a tenant-hash schema name.
    /// </summary>
    /// <exception cref="Autheris.Domain.Exceptions.GatewayInvalidQueryException">The identity is reserved for semantic virtual tables.</exception>
    public static void EnsureNamespaceRespected(TableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var reservedDomain = IsInSemanticDomain(table.Identifier);
        if ((reservedDomain && !IsVirtualTable(table)) || (!reservedDomain && IsTenantSchemaName(table.Identifier.Schema)))
        {
            throw new Autheris.Domain.Exceptions.GatewayInvalidQueryException("The table identity is reserved.", "RESERVED_TABLE_IDENTITY");
        }
    }
}
