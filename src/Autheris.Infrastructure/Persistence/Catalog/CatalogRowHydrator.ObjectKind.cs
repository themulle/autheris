namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Data.Common;
using Autheris.Domain.Model;

internal static partial class CatalogRowHydrator
{
    internal static DiscoveryObjectKind ParseObjectKind(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return DiscoveryObjectKind.Table;
        }

        return reader.GetString(ordinal) switch
        {
            "view" => DiscoveryObjectKind.View,
            "metric_view" => DiscoveryObjectKind.MetricView,
            _ => DiscoveryObjectKind.Table
        };
    }

    internal static MetricViewColumnRole? ParseColumnRole(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetString(ordinal) switch
        {
            "dimension" => MetricViewColumnRole.Dimension,
            "measure" => MetricViewColumnRole.Measure,
            _ => null
        };
    }

    internal static object FormatColumnRole(MetricViewColumnRole? role) => role switch
    {
        MetricViewColumnRole.Dimension => "dimension",
        MetricViewColumnRole.Measure => "measure",
        null => DBNull.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown MetricViewColumnRole.")
    };
}
