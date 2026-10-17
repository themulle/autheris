namespace Autheris.Application.Governance.Interfaces;

/// <summary>Issue #111 (A-9): the cached tier counts behind <c>autheris_catalog_visibility_total</c>; a change asks for a refresh.</summary>
public interface ICatalogVisibilityMetrics
{
    /// <summary>Schedules a refresh of the cached counts (never throws, never blocks the caller).</summary>
    void RequestRefresh();
}
