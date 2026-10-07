namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Configuration and options for dynamic OpenAPI 3.1 document generation (F-API-03).
/// </summary>
public sealed record OpenApiDocumentOptions
{
    public string Title { get; init; } = "Autheris OData v4 Enterprise API";
    public string Version { get; init; } = "v1";
    public string Description { get; init; } = "High-performance OData v4 and Zero-Trust data exposure generated dynamically from table metadata and catalog contracts.";
    public string ServerUrl { get; init; } = "/odata/v4";
    public bool IncludeGovernanceMetadata { get; init; } = true;
    public bool OpenSchema { get; init; } = false;

    /// <summary>
    /// Lists the stored procedure spec (/api/v1/procedures/openapi.json) in the index. The route only exists when
    /// SqlEndpoints:Procedures:Enabled is set, so the link must follow that switch.
    /// </summary>
    public bool IncludeStoredProcedureSpec { get; init; } = false;

    /// <summary>
    /// Lists the WebSQL spec (/api/v1/sql/openapi.json) in the index. The route follows WebSql:Enabled.
    /// </summary>
    public bool IncludeWebSqlSpec { get; init; } = false;
}

/// <summary>
/// Summary metadata for a specific domain slice in the OpenAPI catalog.
/// </summary>
public sealed record OpenApiDomainSummary(
    string Domain,
    int TableCount,
    string JsonUrl,
    string YamlUrl,
    IReadOnlyList<string> Tables
);

/// <summary>
/// Entry for a specific API specification slice to be listed in Swagger UI or API catalog.
/// </summary>
public sealed record OpenApiApiEntry(
    string Name,
    string Url,
    string? Domain = null
);

/// <summary>
/// Root index document describing all available OpenAPI 3.1 modular specifications.
/// </summary>
public sealed record OpenApiIndexDocument(
    int TotalDomains,
    int TotalTables,
    IReadOnlyList<OpenApiDomainSummary> Domains,
    IReadOnlyList<OpenApiApiEntry> Apis
);
