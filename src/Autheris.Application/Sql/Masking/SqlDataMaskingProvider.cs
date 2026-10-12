namespace Autheris.Application.Sql.Masking;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// SEC H-13 / AR-05: Dedicated provider for generating dialect-specific SQL column masking expressions.
/// </summary>
public sealed class SqlDataMaskingProvider
{
    internal const string InternalParameterPrefix = "__gql_";

    private readonly IOptions<GatewayOptions> _options;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly ILogger? _logger;

    private byte[]? _masterHmacKey;
    private bool _masterHmacKeyResolved;

    public SqlDataMaskingProvider(
        IOptions<GatewayOptions> options,
        IKeyVaultSecretProvider? secretProvider = null,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public string GetMaskExpressionForRule(
        string columnName,
        TableMetadata tableMeta,
        TenantId tenantId,
        Dictionary<string, object?> internalParameters,
        Dictionary<string, string> hmacKeyParameterNames,
        out bool isEffectiveHmac)
    {
        isEffectiveHmac = false;
        if (tableMeta.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            var ruleType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";
            if (ruleType == "NULLIFY")
            {
                return "NULL";
            }
            if (ruleType is "HMAC" or "HMAC_SHA256" or "HASH")
            {
                string keyId = !string.IsNullOrWhiteSpace(rule.HmacKeyId)
                    ? rule.HmacKeyId
                    : (_options.Value.DataMasking.HmacKeyId ?? "default");

                var expression = TryBuildKeyedHmacExpression(columnName, tableMeta.Dialect, keyId, tenantId, internalParameters, hmacKeyParameterNames);
                if (expression != null)
                {
                    isEffectiveHmac = true;
                    return expression;
                }

                return BuildDefaultTypeSafeMask(tableMeta, columnName);
            }
            if (ruleType == "MASK_EMAIL")
            {
                return BuildEmailMaskExpression(columnName, tableMeta.Dialect);
            }
            if (ruleType == "MASK_IBAN")
            {
                return BuildIbanMaskExpression(columnName, tableMeta.Dialect);
            }
            if (ruleType == "GEO_JITTER")
            {
                var targetDialect = SqlDialectMapper.ToTargetDialect(tableMeta.Dialect);
                return AstSecurityVisitor.BuildDialectMaskExpression(
                    columnName,
                    "GEO_JITTER",
                    targetDialect,
                    decimals: rule.Decimals ?? 2);
            }
            if (ruleType == "PARTIAL_MASK")
            {
                var targetDialect = SqlDialectMapper.ToTargetDialect(tableMeta.Dialect);
                return AstSecurityVisitor.BuildDialectMaskExpression(
                    columnName,
                    "PARTIAL_MASK",
                    targetDialect,
                    keepPrefix: rule.KeepPrefix ?? 1,
                    keepSuffix: rule.KeepSuffix ?? 0,
                    maskChar: rule.MaskChar ?? '*',
                    fixedLength: rule.FixedLength ?? false);
            }
            var col = tableMeta.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
            if (col != null && IsNumericOrTemporalType(col.DataType))
            {
                return "NULL";
            }

            if (!string.IsNullOrWhiteSpace(rule.Replacement))
            {
                var prefix = tableMeta.Dialect == DatabaseDialect.SqlServer ? "N" : string.Empty;
                return $"{prefix}'{tableMeta.Dialect.EscapeSqlLiteral(rule.Replacement)}'";
            }
        }
        return BuildDefaultTypeSafeMask(tableMeta, columnName);
    }

    public static bool IsNumericOrTemporalType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();
        return dt is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal"
            or "money" or "smallmoney" or "real" or "float" or "double precision" or "double"
            or "bit" or "bool" or "boolean" or "date" or "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamptz" or "time" or "uniqueidentifier" or "uuid";
    }

    public static string BuildDefaultTypeSafeMask(TableMetadata tableMeta, string columnName)
    {
        var col = tableMeta.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (col == null || string.IsNullOrWhiteSpace(col.DataType))
        {
            return "'***'";
        }

        var dt = col.DataType.Trim().ToLowerInvariant();
        if (dt.Contains('('))
        {
            dt = dt[..dt.IndexOf('(')].Trim();
        }

        return dt switch
        {
            "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal" or "money" or "smallmoney" or "real" or "float" or "double precision" or "double" => "NULL",
            "bit" or "bool" or "boolean" => "NULL",
            "date" => "NULL",
            "datetime" or "datetime2" or "smalldatetime" or "timestamp" or "timestamptz" => "NULL",
            "uniqueidentifier" or "uuid" => "NULL",
            _ => "'***'"
        };
    }

    public static string BuildEmailMaskExpression(string columnName, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.SqlServer)
        {
            var col = $"[{columnName.Replace("]", "]]")}]";
            return $"CASE WHEN CHARINDEX('@', CAST({col} AS NVARCHAR(MAX))) > 1 THEN SUBSTRING(CAST({col} AS NVARCHAR(MAX)), 1, 1) + '***@***' ELSE '***@***' END";
        }

        var quotedCol = $"\"{columnName.Replace("\"", "\"\"")}\"";
        if (dialect == DatabaseDialect.Sqlite)
        {
            return $"CASE WHEN INSTR(CAST({quotedCol} AS TEXT), '@') > 1 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 1) || '***@***' ELSE '***@***' END";
        }

        return $"CASE WHEN POSITION('@' IN CAST({quotedCol} AS TEXT)) > 1 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 1) || '***@***' ELSE '***@***' END";
    }

    public static string BuildIbanMaskExpression(string columnName, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.SqlServer)
        {
            var col = $"[{columnName.Replace("]", "]]")}]";
            return $"CASE WHEN LEN(CAST({col} AS NVARCHAR(MAX))) >= 8 THEN SUBSTRING(CAST({col} AS NVARCHAR(MAX)), 1, 2) + '** **** **** ' + RIGHT(CAST({col} AS NVARCHAR(MAX)), 4) ELSE '****' END";
        }

        var quotedCol = $"\"{columnName.Replace("\"", "\"\"")}\"";
        if (dialect == DatabaseDialect.Sqlite)
        {
            return $"CASE WHEN LENGTH(CAST({quotedCol} AS TEXT)) >= 8 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 2) || '** **** **** ' || SUBSTR(CAST({quotedCol} AS TEXT), -4) ELSE '****' END";
        }

        return $"CASE WHEN LENGTH(CAST({quotedCol} AS TEXT)) >= 8 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 2) || '** **** **** ' || SUBSTR(CAST({quotedCol} AS TEXT), LENGTH(CAST({quotedCol} AS TEXT)) - 3, 4) ELSE '****' END";
    }

    public string? TryBuildKeyedHmacExpression(
        string columnName,
        DatabaseDialect dialect,
        string keyId,
        TenantId tenantId,
        Dictionary<string, object?> internalParameters,
        Dictionary<string, string> hmacKeyParameterNames)
    {
        if (_options.Value.DataMasking.PreventInDbHmacKeyExposure)
        {
            _logger?.LogWarning("WebSQL in-DB HMAC key parameter binding is disabled by policy (PreventInDbHmacKeyExposure = true); column '{Column}' is redacted fail-closed.", columnName);
            return null;
        }

        var masterKey = GetMasterHmacKey();
        if (masterKey == null)
        {
            return null;
        }

        if (!hmacKeyParameterNames.TryGetValue(keyId, out var paramBase))
        {
            paramBase = $"{InternalParameterPrefix}mk{hmacKeyParameterNames.Count}";
            hmacKeyParameterNames[keyId] = paramBase;

            byte[] derivedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKey,
                32,
                Encoding.UTF8.GetBytes("gql-websql-tenant:" + tenantId.Value),
                Encoding.UTF8.GetBytes("gql-websql-mask:" + keyId));
            string hexKey = Convert.ToHexString(derivedKey);

            if (dialect == DatabaseDialect.SqlServer)
            {
                byte[] keyBytes = Encoding.ASCII.GetBytes(hexKey);
                var innerPad = new byte[keyBytes.Length];
                var outerPad = new byte[keyBytes.Length];
                for (int i = 0; i < keyBytes.Length; i++)
                {
                    innerPad[i] = (byte)(keyBytes[i] ^ 0x36);
                    outerPad[i] = (byte)(keyBytes[i] ^ 0x5c);
                }

                internalParameters["@" + paramBase + "_i"] = innerPad;
                internalParameters["@" + paramBase + "_o"] = outerPad;
            }
            else
            {
                internalParameters["@" + paramBase] = hexKey;
            }
        }

        return dialect switch
        {
            DatabaseDialect.PostgreSql =>
                $"ENCODE(HMAC(CAST(\"{columnName.Replace("\"", "\"\"")}\" AS TEXT), CAST(@{paramBase} AS TEXT), 'sha256'), 'hex')",
            DatabaseDialect.SqlServer =>
                $"CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', @{paramBase}_o + HASHBYTES('SHA2_256', @{paramBase}_i + CAST(CAST([{columnName.Replace("]", "]]")}] AS NVARCHAR(MAX)) AS VARBINARY(MAX)))), 2)",
            _ =>
                $"gateway_hmac_sha256(CAST(\"{columnName.Replace("\"", "\"\"")}\" AS TEXT), @{paramBase})"
        };
    }

    private byte[]? GetMasterHmacKey()
    {
        if (_masterHmacKeyResolved)
        {
            return _masterHmacKey;
        }

        _masterHmacKeyResolved = true;
        var secretRef = _options.Value.DataMasking.HmacSecretKeyVaultRef;
        if (_secretProvider == null || string.IsNullOrWhiteSpace(secretRef))
        {
            _logger?.LogWarning("WebSQL HMAC masking key is not resolvable (no secret provider or secret reference); HMAC columns are redacted.");
            return null;
        }

        try
        {
            var key = _secretProvider.GetSecretBytes(secretRef);
            _masterHmacKey = key is { Length: > 0 } ? key : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            _logger?.LogWarning("WebSQL HMAC masking secret could not be resolved; HMAC columns are redacted.");
            _masterHmacKey = null;
        }

        return _masterHmacKey;
    }
}
