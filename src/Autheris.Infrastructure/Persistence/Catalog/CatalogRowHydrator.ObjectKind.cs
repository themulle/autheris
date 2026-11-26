namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Data.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

internal static partial class CatalogRowHydrator
{
    private const int MaxLoggedObjectKindLength = 64;

    internal static DiscoveryObjectKind ParseObjectKind(DbDataReader reader, int ordinal, ILogger? logger = null)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return DiscoveryObjectKind.Table;
        }

        var stored = reader.GetString(ordinal);
        if (stored.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveryObjectKind.Table;
        }

        if (stored.Equals("view", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveryObjectKind.View;
        }

        if (stored.Equals("procedure", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveryObjectKind.Procedure;
        }

        if (stored.Equals("metric_view", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveryObjectKind.MetricView;
        }

        var safe = SanitizeForLog(stored);
        logger?.LogWarning("A catalog row in TABLES has the unknown object kind {ObjectKind} and was not loaded.", safe);
        throw new UnknownCatalogObjectKindException(safe);
    }

    private static string SanitizeForLog(string value)
    {
        var length = Math.Min(value.Length, MaxLoggedObjectKindLength);
        return string.Create(length, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
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
