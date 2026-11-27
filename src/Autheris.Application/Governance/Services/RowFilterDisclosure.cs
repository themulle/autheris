using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Interfaces;

namespace Autheris.Application.Governance.Services;

/// <summary>
/// The only row-filter text a caller may learn about: fixed constants, never SQL, parameters, recipe names or consent text.
/// Shared by <c>describe_my_access</c> and the GraphQL dataset-facts extension so both always agree. Scope: a row filter
/// defined by the data owner or by consent; tenant isolation, virtual and mandatory filters are intentionally not reported.
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

        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            return [EffectiveAccessExplanationService.RowFilterDescription];
        }

        return [];
    }
}
