namespace Autheris.Application.Dbt.Services;

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Security;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// The descriptive fields of a metric that every authenticated caller may see (#112 A1-6 c, SEC-112-18). Filter templates, type parameters
/// and free-form <c>Meta</c> are manifest implementation detail and are never part of it.
/// </summary>
public sealed record MetricDefinitionSummary(string Name, string Type, string? Label, string? Description, string? Unit)
{
    /// <summary>Explicit allow-list mapper: no reflection, no copy-all.</summary>
    public static MetricDefinitionSummary From(DbtMetric metric)
    {
        System.ArgumentNullException.ThrowIfNull(metric);
        return new MetricDefinitionSummary(metric.Name, metric.Type, metric.Label, metric.Description, metric.Unit);
    }
}

/// <summary>
/// Shapes the semantic metric catalog per caller for REST, MCP and GraphQL. Only administrators of the manifest see the full definition
/// (and only on the list and describe routes); every other authenticated caller gets <see cref="MetricDefinitionSummary"/>.
/// </summary>
public static class SemanticMetricCatalogProjection
{
    private static readonly GatewayRoleEvaluator Roles = new();

    public static bool SeesDefinitionDetails(ClaimsPrincipal? user) =>
        user != null &&
        (Roles.HasRole(user, GatewayRole.GovernanceAdmin) ||
         CallerScopeResolver.HoldsGovernanceAdminRole(user) ||
         user.IsInRole("DbtAdmin"));

    public static IReadOnlyList<object> Project(IReadOnlyList<DbtMetric> metrics, ClaimsPrincipal? user)
    {
        if (SeesDefinitionDetails(user))
        {
            return metrics.Cast<object>().ToList();
        }

        return metrics.Select(m => (object)MetricDefinitionSummary.From(m)).ToList();
    }

    /// <summary>One metric for the describe route: the full definition for administrators, the summary otherwise.</summary>
    public static object ProjectOne(DbtMetric metric, ClaimsPrincipal? user) =>
        SeesDefinitionDetails(user) ? metric : MetricDefinitionSummary.From(metric);
}
