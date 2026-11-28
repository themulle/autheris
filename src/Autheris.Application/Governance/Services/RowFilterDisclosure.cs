using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Interfaces;

namespace Autheris.Application.Governance.Services;

/// <summary>
/// The only row-filter text a caller may learn about: fixed constants, never SQL, parameters, recipe names or consent text.
/// Shared by <c>describe_my_access</c> and the GraphQL dataset-facts extension so both always agree. Scope: a row filter
/// defined by the data owner or by consent (including per-cell consent filters); tenant isolation, virtual and mandatory
/// filters are intentionally not reported, so a Virtual Filter never changes what a caller learns.
/// </summary>
internal static class RowFilterDisclosure
{
    internal static IReadOnlyList<string> Describe(TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.ConsentFilterRecipe is { } recipe && CallerAttributeRecipes.Names(recipe).Count > 0)
        {
            return [EffectiveAccessExplanationService.AttributeRowFilterDescription];
        }

        return HasOwnerOrConsentRowFilter(decision) ? [EffectiveAccessExplanationService.RowFilterDescription] : [];
    }

    private static bool HasOwnerOrConsentRowFilter(TableAccessDecision decision)
    {
        // A per-cell consent restriction has no combined text or recipe, only the typed per-cell policy.
        if (decision.PerCellColumns is not null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql))
        {
            return decision.RowPolicy is not null || !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql);
        }

        // A Virtual Filter is composed into the combined filter: look only at what stood before and after it.
        return decision.RowPolicyBeforeVirtualFilters is not null
            || !string.IsNullOrWhiteSpace(decision.RowFilterSqlBeforeVirtualFilters)
            || decision.RowPoliciesAfterVirtualFilters is { Count: > 0 }
            || decision.RowFiltersAfterVirtualFilters is { Count: > 0 };
    }
}
