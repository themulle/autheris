namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Shared steps of the callers that bind caller attributes into a typed policy (REQ-GOV-129 A-1): which attributes a recipe names, and one resolution under the bind reserve.</summary>
public static class CallerAttributeRecipes
{
    /// <summary>The attribute names of the simple-predicate <c>USER_ATTRIBUTE</c> leaves of <paramref name="recipe"/> (valid names only).</summary>
    public static IReadOnlyCollection<string> Names(ConsentRowFilterRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filter in recipe.Allow.Concat(recipe.Deny).SelectMany(static f => f))
        {
            if (string.Equals(filter.ValueSource, "USER_ATTRIBUTE", StringComparison.OrdinalIgnoreCase) &&
                filter.FilterType == RowFilterType.SimpleColumnPredicate &&
                CallerAttributeDefinition.IsValidName(filter.UserAttribute))
            {
                names.Add(filter.UserAttribute!);
            }
        }

        return names;
    }

    /// <summary>
    /// Resolves <paramref name="names"/> once and reserves the padded expansion in the subject's request scope (BC-129-09): over the reserve every
    /// attribute becomes <see cref="CallerAttributeOutcome.OverCardinality"/>, the same constant as <c>Missing</c>.
    /// </summary>
    public static async Task<(CallerAttributeSet Set, CallerAttributePadding Padding)> ResolveAsync(
        ICallerAttributeResolver resolver, CallerSubject subject, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(subject);
        var padding = resolver.Padding;
        var set = await resolver.ResolveAsync(subject, names, ct).ConfigureAwait(false);

        int items = 0;
        foreach (var name in names)
        {
            if (set.TryGet(name, out var result) && result.Outcome == CallerAttributeOutcome.Resolved && result.Value is { } value)
            {
                items += padding.PaddedCount(value.Count);
            }
        }

        if (items > 0 && !subject.TryReserveBinds(items, resolver.BindReserve))
        {
            set = new CallerAttributeSet(names.ToDictionary(n => n, _ => CallerAttributeResult.OverCardinality(), StringComparer.Ordinal));
        }

        return (set, padding);
    }
}
