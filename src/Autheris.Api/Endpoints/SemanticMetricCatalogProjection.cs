namespace Autheris.Api.Endpoints;

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Model;

/// <summary>
/// Shapes the semantic metric catalog listing per caller (PLAN-DATA-112 WP-0, F-6). Filter templates (admin-authored predicate text), type
/// parameters and free-form <c>Meta</c> are implementation detail of a manifest; only administrators of the manifest see them.
/// Every other authenticated caller gets the descriptive fields needed to pick a metric.
/// </summary>
internal static class SemanticMetricCatalogProjection
{
    internal static bool SeesDefinitionDetails(ClaimsPrincipal? user) =>
        user != null &&
        (EndpointSecurity.IsGlobalGovernanceAdmin(user) ||
         CallerScopeResolver.HoldsGovernanceAdminRole(user) ||
         user.IsInRole("DbtAdmin"));

    internal static IReadOnlyList<object> Project(IReadOnlyList<DbtMetric> metrics, ClaimsPrincipal? user)
    {
        if (SeesDefinitionDetails(user))
        {
            return metrics.Cast<object>().ToList();
        }

        return metrics
            .Select(m => (object)new { name = m.Name, type = m.Type, label = m.Label, description = m.Description, unit = m.Unit })
            .ToList();
    }
}
