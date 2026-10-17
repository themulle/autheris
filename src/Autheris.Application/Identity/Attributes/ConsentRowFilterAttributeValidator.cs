namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Save-time rules of consent row filters that carry caller attributes (PLAN-GOV-129 A-9, V-129-4, BC-129-11, SEC-129-16). The message names
/// the filter's column and the rule, never a value. Two layers: <see cref="ValidateShape"/> needs nothing from the registry (the three
/// repositories run it at their insert, so no writer can bypass it) and <see cref="ValidateAgainstRegistry"/> (the decorating repository).
/// </summary>
public static class ConsentRowFilterAttributeValidator
{
    public const string UserAttributeSource = ConsentRowFilterShape.UserAttributeSource;
    public const string LiteralSource = ConsentRowFilterShape.LiteralSource;

    /// <exception cref="InvalidOperationException">A filter is not acceptable.</exception>
    public static void ValidateShape(IEnumerable<ConsentRowFilter> filters) => ConsentRowFilterShape.Validate(filters);

    /// <summary>The registry rules: feature on, attribute registered and active for the consent's tenant, cardinality fits the operator, SQL source.</summary>
    /// <exception cref="InvalidOperationException">A filter is not acceptable.</exception>
    public static void ValidateAgainstRegistry(
        Consent consent, CallerAttributeSnapshot snapshot, CallerAttributeOptions options, TableMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        foreach (var filter in consent.RowFilters.Where(f => string.Equals(f.ValueSource, UserAttributeSource, StringComparison.OrdinalIgnoreCase)))
        {
            if (!options.Enabled)
            {
                throw Refuse(filter, "caller attributes are disabled");
            }

            if (!snapshot.TryGetDefinition(consent.TenantId, filter.UserAttribute!, out var definition))
            {
                throw Refuse(filter, "the attribute is not registered and active");
            }

            if (!string.Equals(filter.Operator, "IN", StringComparison.OrdinalIgnoreCase) && definition.Cardinality != CallerAttributeCardinality.Scalar)
            {
                throw Refuse(filter, "a list attribute needs the operator IN");
            }

            if (metadata?.Table is null || metadata.DataSourceType != DataSourceType.Sql)
            {
                throw Refuse(filter, "a caller attribute filter needs a catalogued SQL table");
            }

            var column = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, filter.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (column is not null && !CallerAttributeColumnTypeFit.Fits(definition.ValueType, column.DataType))
            {
                throw Refuse(filter, "the attribute type does not fit the column type");
            }
        }
    }

    /// <summary>
    /// A-10: a consent on the table an attribute-table provider reads would make the privileged read reachable through the data channels
    /// (BC-129-11). Refused unless the table is listed in <c>AllowAttributeSourceInCatalog</c>.
    /// </summary>
    public static bool IsAttributeSource(Consent consent, CallerAttributeSnapshot snapshot, CallerAttributeOptions options)
    {
        var table = consent.TableIdentifier;
        string qualified = $"{table.Schema}.{table.TableName}";
        foreach (var definition in snapshot.Definitions.Where(d => d.Provider.Kind == CallerAttributeProviderBinding.AttributeTable && d.Tenant == consent.TenantId))
        {
            if (!AttributeTableSpec.TryParse(definition.Provider.Config, out var spec, out _) ||
                !string.Equals(spec!.DataSource, table.Domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool sameTable = string.Equals(spec.Table, table.TableName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(spec.Schema) || string.Equals(spec.Schema, table.Schema, StringComparison.OrdinalIgnoreCase));
            if (sameTable && !options.AllowAttributeSourceInCatalog.Any(a => string.Equals(a, qualified, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static InvalidOperationException Refuse(ConsentRowFilter filter, string rule) => ConsentRowFilterShape.Refuse(filter, rule);
}
