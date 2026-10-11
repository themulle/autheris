namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

/// <summary>
/// The row-filter inputs of a consent decision (WP-X0l): the filters of every allow consent (an empty list is an unconstrained allow)
/// and of every deny consent that has filters. The typed row policy is built from this recipe, and the same recipe produced the legacy
/// text, so the two cannot disagree. It is serializable because the consent decision cache stores no typed objects.
/// </summary>
public sealed record ConsentRowFilterRecipe(
    IReadOnlyList<IReadOnlyList<ConsentRowFilter>> Allow,
    IReadOnlyList<IReadOnlyList<ConsentRowFilter>> Deny)
{
    private sealed class Dto
    {
        public List<List<ConsentRowFilter>>? Allow { get; set; }
        public List<List<ConsentRowFilter>>? Deny { get; set; }
    }

    public string ToJson() => JsonSerializer.Serialize(new Dto
    {
        Allow = Allow.Select(static f => f.ToList()).ToList(),
        Deny = Deny.Select(static f => f.ToList()).ToList()
    });

    /// <summary>Fail closed: anything that is not a well-formed recipe is rejected (the caller then treats the cache entry as a miss).</summary>
    public static bool TryParse(string? json, out ConsentRowFilterRecipe? recipe)
    {
        recipe = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json);
            if (dto?.Allow is null || dto.Deny is null || dto.Allow.Any(static f => f is null || f.Any(static x => x is null)) ||
                dto.Deny.Any(static f => f is null || f.Any(static x => x is null)))
            {
                return false;
            }

            recipe = new ConsentRowFilterRecipe(
                dto.Allow.Select(static f => (IReadOnlyList<ConsentRowFilter>)f).ToList(),
                dto.Deny.Select(static f => (IReadOnlyList<ConsentRowFilter>)f).ToList());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
