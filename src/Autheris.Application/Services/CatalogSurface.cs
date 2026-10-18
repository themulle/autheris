namespace Autheris.Application.Services;

/// <summary>
/// Issue #111 (A-4): the surface a catalog visibility decision is made for. <see cref="DataAccess"/> is the default of every overload
/// (fail closed: only admins and consented tables); only the discovery surfaces explicitly pass <see cref="Discovery"/>, where the persisted
/// dataset tiers (Hidden, Summary, Full) apply.
/// </summary>
public enum CatalogSurface
{
    /// <summary>Query metadata and data paths (Trino, Arrow, Iceberg, guardrail, golden queries, semantic resources): consents only.</summary>
    DataAccess = 0,

    /// <summary>Dataset discovery (MCP list/describe/relations, GraphQL catalog, REST catalog): persisted tiers apply on shared tables.</summary>
    Discovery = 1
}
