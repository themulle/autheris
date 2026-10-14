namespace Autheris.Application.Sql.Masking;

using System;
using System.Collections.Frozen;
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
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// SEC H-13 / AR-05: Dedicated provider for generating dialect-specific SQL column masking expressions.
/// </summary>
public sealed class SqlDataMaskingProvider
{
    internal const string InternalParameterPrefix = "__gql_";

    private readonly IOptions<GatewayOptions> _options;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly ILogger? _logger;
    private readonly MaskSpecRenderer _renderer = new();

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
            if (ruleType is "GEO_JITTER" or "PARTIAL_MASK")
            {
                // WP-X0f: the string facade renders the typed spec through the dialect generator; its values are bound parameters.
                return RenderTypedMask(rule, columnName, tableMeta, tenantId, internalParameters);
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

    /// <summary>
    /// WP-X0f: the typed mask of a column. Every value (replacement, prefix and suffix counts, mask character, HMAC key) is a bound
    /// parameter of the returned spec (origin <see cref="ParameterOrigin.Mask"/>); the dialect generator renders it. A rule that
    /// cannot be expressed exactly is mapped to the stricter mask (fail closed): an HMAC that may not reach the database (policy,
    /// Oracle, no key) and a fixed-length partial mask become Redact; MASK_EMAIL and MASK_IBAN become the constants
    /// <c>***@***</c> and <c>****</c> (neither characters nor length of the value are revealed); on SQLite a partial mask becomes the
    /// five-character constant of the legacy SQLite mask.
    /// </summary>
    /// <param name="rule">The catalog rule, or null for a column that is masked without a rule (ABAC).</param>
    /// <param name="dataType">The catalog data type; the emitter requires it (numeric and temporal columns redact to a typed NULL).</param>
    /// <param name="hmacKeyId">Overrides the key id of an HMAC rule.</param>
    public MaskSpec GetMaskSpec(MaskingRule? rule, string columnName, string? dataType, DatabaseDialect dialect, TenantId tenantId, string? hmacKeyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (rule is { IsHmac: true })
        {
            string keyId = !string.IsNullOrWhiteSpace(hmacKeyId)
                ? hmacKeyId
                : !string.IsNullOrWhiteSpace(rule.HmacKeyId) ? rule.HmacKeyId : (_options.Value.DataMasking.HmacKeyId ?? "default");
            return TryBuildHmacSpec(new MaskParameterNames(columnName), dialect, keyId, tenantId, columnName)
                ?? Redact(new MaskParameterNames(columnName), DefaultRedactText(rule));
        }

        return BuildUnkeyedMaskSpec(rule, columnName, dialect);
    }

    /// <summary>The typed mask of every rule that needs no key (everything except HMAC, which degrades to Redact here).</summary>
    public static MaskSpec BuildUnkeyedMaskSpec(MaskingRule? rule, string columnName, DatabaseDialect dialect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        var names = new MaskParameterNames(columnName);
        if (rule is null)
        {
            return Redact(names, "***");
        }

        var ruleType = rule.RuleType?.Trim().ToUpperInvariant() is { Length: > 0 } t ? t : "REDACT";
        switch (ruleType)
        {
            case "NULLIFY":
                return new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
            case "GEO_JITTER":
                return new MaskSpec(MaskKind.GeoJitter, new MaskArguments(Decimals: Math.Clamp(rule.Decimals ?? 2, 0, 6)), FrozenDictionary<string, PolicyValue>.Empty);
            case "PARTIAL_MASK":
                if (rule.FixedLength == true)
                {
                    // The typed partial mask always reveals the length; hiding the whole value is the stricter choice.
                    return Redact(names, new string(rule.MaskChar ?? '*', 5));
                }

                if (dialect == DatabaseDialect.Sqlite)
                {
                    // The legacy SQLite partial mask always produced five mask characters; the typed variable-length mask would
                    // reveal the value length there, so SQLite redacts to the same constant length (never more than the legacy output).
                    return Redact(names, new string(rule.MaskChar ?? '*', 5));
                }

                return PartialMask(names, rule.KeepPrefix ?? 1, rule.KeepSuffix ?? 0, rule.MaskChar ?? '*');
            case "MASK_EMAIL":
                // A typed mask never reveals more than the legacy one, neither characters nor length: the legacy mask kept the first
                // character of an address and nothing of a value without '@'; a constant is the only output that is never looser.
                return Redact(names, "***@***");
            case "MASK_IBAN":
                return Redact(names, "****");
            default:
                return Redact(names, DefaultRedactText(rule));
        }
    }

    private static string DefaultRedactText(MaskingRule rule) =>
        string.IsNullOrWhiteSpace(rule.Replacement) ? "***" : rule.Replacement;

    private static MaskSpec Redact(MaskParameterNames names, string text)
    {
        var constant = names.Parameter("c", SqlParameterType.String);
        return new MaskSpec(MaskKind.Redact, new MaskArguments(Constant: constant),
            new Dictionary<string, PolicyValue> { [constant.Name] = new PolicyValue(text, SqlParameterType.String) }.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static MaskSpec PartialMask(MaskParameterNames names, int keepPrefix, int keepSuffix, char maskChar)
    {
        var prefix = names.Parameter("pre", SqlParameterType.Int32);
        var suffix = names.Parameter("suf", SqlParameterType.Int32);
        var mask = names.Parameter("mc", SqlParameterType.String);
        return new MaskSpec(MaskKind.PartialMask, new MaskArguments(KeepPrefix: prefix, KeepSuffix: suffix, MaskChar: mask),
            new Dictionary<string, PolicyValue>
            {
                [prefix.Name] = new PolicyValue(Math.Max(0, keepPrefix), SqlParameterType.Int32),
                [suffix.Name] = new PolicyValue(Math.Max(0, keepSuffix), SqlParameterType.Int32),
                [mask.Name] = new PolicyValue(maskChar.ToString(), SqlParameterType.String)
            }.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private MaskSpec? TryBuildHmacSpec(MaskParameterNames names, DatabaseDialect dialect, string keyId, TenantId tenantId, string columnName)
    {
        if (_options.Value.DataMasking.PreventInDbHmacKeyExposure)
        {
            _logger?.LogWarning("WebSQL in-DB HMAC key parameter binding is disabled by policy (PreventInDbHmacKeyExposure = true); column '{Column}' is redacted fail-closed.", columnName);
            return null;
        }

        // SEC-ADG-15: no in-database HMAC on Oracle without a DBMS_CRYPTO grant; other dialects have no in-database HMAC either.
        if (dialect is not (DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.Sqlite))
        {
            return null;
        }

        var masterKey = GetMasterHmacKey();
        if (masterKey == null)
        {
            return null;
        }

        string hexKey = DeriveHexKey(masterKey, tenantId, keyId);
        if (dialect == DatabaseDialect.SqlServer)
        {
            // HMAC from two bound pads (inner, outer) derived from the same key.
            byte[] keyBytes = Encoding.ASCII.GetBytes(hexKey);
            var innerPad = new byte[keyBytes.Length];
            var outerPad = new byte[keyBytes.Length];
            for (int i = 0; i < keyBytes.Length; i++)
            {
                innerPad[i] = (byte)(keyBytes[i] ^ 0x36);
                outerPad[i] = (byte)(keyBytes[i] ^ 0x5c);
            }

            var inner = names.Parameter("ki", SqlParameterType.Binary);
            var outer = names.Parameter("ko", SqlParameterType.Binary);
            return new MaskSpec(MaskKind.Hmac, new MaskArguments(HmacKey: inner, HmacKeyOuter: outer),
                new Dictionary<string, PolicyValue>
                {
                    [inner.Name] = new PolicyValue(innerPad, SqlParameterType.Binary),
                    [outer.Name] = new PolicyValue(outerPad, SqlParameterType.Binary)
                }.ToFrozenDictionary(StringComparer.Ordinal));
        }

        var key = names.Parameter("k", SqlParameterType.String);
        return new MaskSpec(MaskKind.Hmac, new MaskArguments(HmacKey: key),
            new Dictionary<string, PolicyValue> { [key.Name] = new PolicyValue(hexKey, SqlParameterType.String) }.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static string DeriveHexKey(byte[] masterKey, TenantId tenantId, string keyId)
    {
        byte[] derivedKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            32,
            Encoding.UTF8.GetBytes("gql-websql-tenant:" + tenantId.Value),
            Encoding.UTF8.GetBytes("gql-websql-mask:" + keyId));
        return Convert.ToHexString(derivedKey);
    }

    /// <summary>Deterministic, collision-free parameter names per column (<c>__gql_mask_{hash}_{role}</c>) so the specs of several columns merge.</summary>
    private sealed class MaskParameterNames(string column)
    {
        private readonly string _prefix = "__gql_mask_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(column.ToLowerInvariant())))[..10].ToLowerInvariant() + "_";

        public PolicyParameterExpression Parameter(string role, SqlParameterType type) =>
            new(_prefix + role, type, ParameterOrigin.Mask);
    }

    private string RenderTypedMask(
        MaskingRule rule,
        string columnName,
        TableMetadata tableMeta,
        TenantId tenantId,
        Dictionary<string, object?> internalParameters)
    {
        var dialect = tableMeta.Dialect;
        var dataType = tableMeta.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase))?.DataType;
        try
        {
            var spec = GetMaskSpec(rule, columnName, dataType, dialect, tenantId);
            var rendered = _renderer.Render(spec, columnName, dataType, SqlDialectMapper.ToTargetDialect(dialect), name => dialect.FormatParameterMarker(name));
            foreach (var (marker, value) in rendered.Parameters)
            {
                if (internalParameters.TryGetValue(marker, out var existing) && !Equals(existing, value))
                {
                    throw new System.Security.SecurityException("Conflicting column mask parameters.");
                }

                internalParameters[marker] = value;
            }

            return rendered.Sql;
        }
        catch (System.Security.SecurityException)
        {
            // The typed emitter needs a permitted catalog type of the column; without it the stricter default mask applies.
            _logger?.LogWarning("Column '{Column}' cannot be rendered as a typed mask (missing or unsupported catalog data type); the default mask is applied.", columnName);
            return BuildDefaultTypeSafeMask(tableMeta, columnName);
        }
    }

    public static bool IsNumericOrTemporalType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();
        return dt is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal"
            or "money" or "smallmoney" or "real" or "float" or "double precision" or "double"
            or "bit" or "bool" or "boolean" or "date" or "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamptz" or "time" or "uniqueidentifier" or "uuid"
            or "number" or "binary_float" or "binary_double";
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
            "number" or "binary_float" or "binary_double" => "NULL",
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
        if (dialect == DatabaseDialect.Oracle)
        {
            // CR-ADG-18: Oracle has no TEXT type and no POSITION function (CAST ... AS TEXT failed on the executable Oracle path).
            string text = $"CAST({quotedCol} AS VARCHAR2(4000))";
            return $"CASE WHEN INSTR({text}, '@') > 1 THEN SUBSTR({text}, 1, 1) || '***@***' ELSE '***@***' END";
        }

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
        if (dialect == DatabaseDialect.Oracle)
        {
            string text = $"CAST({quotedCol} AS VARCHAR2(4000))";
            return $"CASE WHEN LENGTH({text}) >= 8 THEN SUBSTR({text}, 1, 2) || '** **** **** ' || SUBSTR({text}, -4) ELSE '****' END";
        }

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

        if (dialect == DatabaseDialect.Oracle)
        {
            // SEC-ADG-15: no in-database HMAC on Oracle without a DBMS_CRYPTO grant; the column is redacted (fail closed), never
            // rendered through the SQLite UDF.
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

            string hexKey = DeriveHexKey(masterKey, tenantId, keyId);

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

                internalParameters[dialect.FormatParameterMarker(paramBase + "_i")] = innerPad;
                internalParameters[dialect.FormatParameterMarker(paramBase + "_o")] = outerPad;
            }
            else
            {
                internalParameters[dialect.FormatParameterMarker(paramBase)] = hexKey;
            }
        }

        var keyMarker = dialect.FormatParameterMarker(paramBase);
        var outerMarker = dialect.FormatParameterMarker(paramBase + "_o");
        var innerMarker = dialect.FormatParameterMarker(paramBase + "_i");
        return dialect switch
        {
            DatabaseDialect.PostgreSql =>
                $"ENCODE(HMAC(CAST(\"{columnName.Replace("\"", "\"\"")}\" AS TEXT), CAST({keyMarker} AS TEXT), 'sha256'), 'hex')",
            DatabaseDialect.SqlServer =>
                $"CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', {outerMarker} + HASHBYTES('SHA2_256', {innerMarker} + CAST(CAST([{columnName.Replace("]", "]]")}] AS NVARCHAR(MAX)) AS VARBINARY(MAX)))), 2)",
            DatabaseDialect.Sqlite =>
                $"gateway_hmac_sha256(CAST(\"{columnName.Replace("\"", "\"\"")}\" AS TEXT), {keyMarker})",
            _ => null
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
