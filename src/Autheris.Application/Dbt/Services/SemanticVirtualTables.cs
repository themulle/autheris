namespace Autheris.Application.Dbt.Services;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// The catalog identity of the virtual tables a semantic manifest import creates (#112 A1-4, SEC-112-14/-15/-23). One place decides
/// what a virtual table is and how its tenant schema is named, so the visibility engine, the metadata listing and the ingestion
/// service can never classify the same table differently.
/// </summary>
public static partial class SemanticVirtualTables
{
    /// <summary>The catalog domain only the ingestion service writes.</summary>
    public const string Domain = "semantic";

    /// <summary>The schema of the virtual tables in <see cref="TenancyMode.Single"/> deployments.</summary>
    public const string SingleModeSchema = "semantic";

    /// <summary>The <c>DocumentationSource</c> the ingestion service sets (server-set marker).</summary>
    public const string SourceMarker = "dbt";

    [GeneratedRegex("^t_[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex TenantSchemaPattern();

    /// <summary>The schema of the tenant's virtual tables in Multi mode: <c>t_</c> plus the first 16 lower-case hex digits of SHA-256 of the tenant id.</summary>
    public static string TenantSchema(TenantId tenant) =>
        "t_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant.Value)))[..16];

    public static bool IsTenantSchemaName(string? schema) => schema is not null && TenantSchemaPattern().IsMatch(schema);

    /// <summary>The schema name the ingestion service uses for <paramref name="tenant"/>; derived from the server-resolved tenant only.</summary>
    public static string SchemaFor(TenantId tenant, TenancyMode mode) => mode == TenancyMode.Single ? SingleModeSchema : TenantSchema(tenant);

    public static TableIdentifier IdentifierFor(TenantId tenant, TenancyMode mode, string modelName) =>
        new(Domain, SchemaFor(tenant, mode), modelName);

    /// <summary>
    /// True for a table the ingestion service created: domain <c>semantic</c>, the server-set source marker and a schema that is either
    /// the single-mode schema or a tenant schema. A real table that merely has a schema named like a tenant hash is not one.
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
        (string.Equals(id.Schema, SingleModeSchema, StringComparison.Ordinal) || IsTenantSchemaName(id.Schema));
}
