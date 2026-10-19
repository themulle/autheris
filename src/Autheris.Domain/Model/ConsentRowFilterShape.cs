namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Registry-independent save-time rules of consent row filters (PLAN-GOV-129 A-9, SEC-129-16): called from <see cref="Consent.Validate"/>, which every
/// repository runs before it stores a consent, so no writer can store an unknown value source or a malformed caller attribute filter.
/// The message names the column and the rule, never a value.
/// </summary>
public static class ConsentRowFilterShape
{
    public const string UserAttributeSource = "USER_ATTRIBUTE";
    public const string LiteralSource = "LITERAL";

    /// <exception cref="InvalidOperationException">A filter is not acceptable.</exception>
    public static void Validate(IEnumerable<ConsentRowFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        foreach (var filter in filters)
        {
            bool attribute = string.Equals(filter.ValueSource, UserAttributeSource, StringComparison.OrdinalIgnoreCase);
            bool literal = string.Equals(filter.ValueSource, LiteralSource, StringComparison.OrdinalIgnoreCase);
            if (!attribute && !literal)
            {
                throw Refuse(filter, "the value source must be LITERAL or USER_ATTRIBUTE");
            }

            if (!attribute)
            {
                continue;
            }

            if (!CallerAttributeDefinition.IsValidName(filter.UserAttribute))
            {
                throw Refuse(filter, "the attribute name is not valid");
            }

            if (filter.FilterType != RowFilterType.SimpleColumnPredicate)
            {
                throw Refuse(filter, "a caller attribute is only allowed in a simple column predicate");
            }

            var op = filter.Operator.ToUpperInvariant();
            if (op is not ("EQ" or "NEQ" or "IN"))
            {
                throw Refuse(filter, "a caller attribute supports the operators EQ, NEQ and IN");
            }

            var json = filter.ValueJson?.Trim();
            if (!string.IsNullOrEmpty(json) && json != "[]")
            {
                throw Refuse(filter, "a caller attribute filter carries no literal value");
            }
        }
    }

    public static InvalidOperationException Refuse(ConsentRowFilter filter, string rule) =>
        new($"The row filter on column '{filter.ColumnName}' is refused: {rule}.");
}
