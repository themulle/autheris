using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Services;

public sealed partial class SqlDataSourceExecutor : IDataSourceExecutor
{
    /// <summary>
    /// WP-D4 (F-1): the one mapping from a catalog dialect to the mask SQL dialect. Fail-closed: a dialect without
    /// an explicit mapping (any other or undefined value) throws instead of receiving PostgreSQL mask SQL.
    /// </summary>
    public static TrinoSqlEngine.TargetSqlDialect ToMaskTargetDialect(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.PostgreSql => TrinoSqlEngine.TargetSqlDialect.PostgreSql,
        DatabaseDialect.SqlServer => TrinoSqlEngine.TargetSqlDialect.SqlServer,
        DatabaseDialect.Sqlite => TrinoSqlEngine.TargetSqlDialect.Sqlite,
        DatabaseDialect.Oracle => TrinoSqlEngine.TargetSqlDialect.Oracle,
        _ => throw new NotSupportedException($"No mask SQL mapping exists for dialect '{dialect}'.")
    };

    public static string BuildMaskedColumnProjection(string columnName, string? dataType, DatabaseDialect dialect, TableMetadata tableMeta)
    {
        ArgumentNullException.ThrowIfNull(tableMeta);
        DatabaseDialectExtensions.ValidateIdentifier(columnName);
        var quotedCol = dialect.QuoteIdentifier(columnName);
        string maskExpr;

        if (tableMeta.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            var ruleType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";
            var effectiveDataType = dataType ?? tableMeta.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase))?.DataType;
            if (ruleType == "NULLIFY" || (ruleType == "REDACT" && IsNumericOrTemporalType(effectiveDataType)))
            {
                maskExpr = "NULL";
            }
            else if (ruleType == "GEO_JITTER")
            {
                var targetDialect = ToMaskTargetDialect(dialect);
                maskExpr = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.BuildDialectMaskExpression(
                    columnName,
                    "GEO_JITTER",
                    targetDialect,
                    decimals: rule.Decimals ?? 2);
            }
            else if (ruleType == "PARTIAL_MASK")
            {
                var targetDialect = ToMaskTargetDialect(dialect);
                maskExpr = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.BuildDialectMaskExpression(
                    columnName,
                    "PARTIAL_MASK",
                    targetDialect,
                    keepPrefix: rule.KeepPrefix ?? 1,
                    keepSuffix: rule.KeepSuffix ?? 0,
                    maskChar: rule.MaskChar ?? '*',
                    fixedLength: rule.FixedLength ?? false);
            }
            else if (IsHmacRule(rule))
            {
                // SEC H-13: No unkeyed in-DB hash. HMAC columns are pseudonymized in the gateway (IColumnMaskingProvider);
                // without a masking provider the column is redacted (fail-closed).
                maskExpr = "'***'";
            }
            else if (!string.IsNullOrWhiteSpace(rule.Replacement))
            {
                var prefix = dialect == DatabaseDialect.SqlServer ? "N" : string.Empty;
                maskExpr = $"{prefix}'{dialect.EscapeSqlLiteral(rule.Replacement)}'";
            }
            else
            {
                maskExpr = "'***'";
            }
        }
        else
        {
            maskExpr = "'***'";
        }

        return $"{maskExpr} AS {quotedCol}";
    }

    private static bool IsHmacRule(MaskingRule rule) => rule.IsHmac;

    private static bool IsNumericOrTemporalType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();
        return dt is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal"
            or "money" or "smallmoney" or "real" or "float" or "double precision" or "double"
            or "bit" or "bool" or "boolean" or "date" or "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamptz" or "time" or "uniqueidentifier" or "uuid";
    }

    /// <summary>
    /// SEC H-13: Derives a tenant-scoped HMAC key id so pseudonyms cannot be correlated across tenants.
    /// The actual key derivation (HMAC over the master secret) happens inside <see cref="IColumnMaskingProvider"/>.
    /// </summary>
    private static MaskingRule CreateTenantScopedHmacRule(MaskingRule rule, string tenant, string? defaultKeyId) =>
        MaskingRule.CreateTenantScopedHmacRule(rule, tenant, defaultKeyId);

    public static string BuildColumnProjection(string columnName, string? dataType, DatabaseDialect dialect)
    {
        DatabaseDialectExtensions.ValidateIdentifier(columnName);
        var quotedCol = dialect.QuoteIdentifier(columnName);

        if (string.IsNullOrWhiteSpace(dataType))
        {
            return quotedCol;
        }

        var normalizedType = dataType.Trim().ToLowerInvariant();

        // Special case MSSQL: "timestamp" is a deprecated synonym for "rowversion" (8-byte binary token, NOT datetime!)
        if (dialect == DatabaseDialect.SqlServer && normalizedType is "timestamp" or "rowversion")
        {
            return quotedCol; // Handled as binary in reader -> Base64
        }

        // 1. Geospatial Types (geometry, geography, spatial, point, polygon, linestring, multipolygon, multipoint, sdo_geometry)
        if (normalizedType is "geometry" or "geography" or "spatial" or "point" or "polygon" or "linestring" or "multipolygon" or "multipoint" or "sdo_geometry")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"ST_AsGeoJSON({quotedCol}) AS {quotedCol}",
                DatabaseDialect.SqlServer => $"({quotedCol}.STAsText()) AS {quotedCol}",
                DatabaseDialect.Sqlite => $"AsGeoJSON({quotedCol}) AS {quotedCol}",
                DatabaseDialect.Oracle => $"SDO_UTIL.TO_GEOJSON({quotedCol}) AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 2. Binary Types (bytea, binary, varbinary, blob, image, raw, long raw)
        if (normalizedType is "bytea" or "binary" or "varbinary" or "blob" or "image" or "raw" or "long raw")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"encode({quotedCol}, 'base64') AS {quotedCol}",
                DatabaseDialect.Sqlite => $"hex({quotedCol}) AS {quotedCol}",
                DatabaseDialect.Oracle => $"RAWTOHEX({quotedCol}) AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 3. High-precision / timezone timestamps (timestamp, timestamptz, datetime2, datetimeoffset, etc.)
        if (normalizedType is "timestamptz" or "datetimeoffset" or "datetime2" or "datetime" or "smalldatetime" or "timestamp" or "timestamp_ntz" or "timestamp with time zone" or "timestamp with local time zone")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"to_char({quotedCol}, 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') AS {quotedCol}",
                DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(33), {quotedCol}, 126) AS {quotedCol}",
                DatabaseDialect.Sqlite => $"strftime('%Y-%m-%dT%H:%M:%fZ', {quotedCol}) AS {quotedCol}",
                DatabaseDialect.Oracle => $"TO_CHAR({quotedCol}, 'YYYY-MM-DD\"T\"HH24:MI:SS.FF6\"Z\"') AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 4. Date-only (date)
        if (normalizedType is "date")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"to_char({quotedCol}, 'YYYY-MM-DD') AS {quotedCol}",
                DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(10), {quotedCol}, 23) AS {quotedCol}",
                DatabaseDialect.Oracle => $"TO_CHAR({quotedCol}, 'YYYY-MM-DD') AS {quotedCol}",
                DatabaseDialect.Sqlite => $"strftime('%Y-%m-%d', {quotedCol}) AS {quotedCol}",
                _ => quotedCol
            };
        }

        // 5. Time-only (time, timetz, time without time zone)
        if (normalizedType is "time" or "timetz" or "time without time zone")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"to_char({quotedCol}, 'HH24:MI:SS.US') AS {quotedCol}",
                DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(16), {quotedCol}, 114) AS {quotedCol}",
                DatabaseDialect.Sqlite => $"strftime('%H:%M:%f', {quotedCol}) AS {quotedCol}",
                _ => quotedCol
            };
        }

        return quotedCol;
    }
}
