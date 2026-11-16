namespace Autheris.Application.Sql.Masking;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
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

    private static readonly TimeSpan KeyRetryBackoff = TimeSpan.FromSeconds(30);
    private readonly object _keyLock = new();
    private readonly TimeProvider _timeProvider;
    private byte[]? _masterHmacKey;
    private DateTimeOffset _keyRetryNotBefore = DateTimeOffset.MinValue;

    public SqlDataMaskingProvider(
        IOptions<GatewayOptions> options,
        IKeyVaultSecretProvider? secretProvider = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    public sealed record ClassificationMaskResolution(
        bool IsDenied,
        string? DenialReason,
        MaskSpec? Spec,
        MaskFunction EffectiveFunction,
        bool TypeFallbackApplied)
    {
        public static ClassificationMaskResolution Deny(string reason, MaskFunction function) =>
            new(true, reason, null, function, false);
    }

    public ClassificationMaskResolution ResolveClassificationMask(
        string? maskFunctionCode,
        string? paramsJson,
        string columnName,
        string? dataType,
        TargetSqlDialect dialect,
        TenantId tenantId,
        string? explicitKeyRef = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (!ClassificationMaskFunctions.TryParse(maskFunctionCode, out var requestedFunction))
        {
            return ClassificationMaskResolution.Deny("MASK_UNKNOWN_FUNCTION", default);
        }

        return ResolveClassificationMask(requestedFunction, paramsJson, columnName, dataType, dialect, tenantId, explicitKeyRef);
    }

    public ClassificationMaskResolution ResolveClassificationMask(
        MaskFunction requestedFunction,
        string? paramsJson,
        string columnName,
        string? dataType,
        TargetSqlDialect dialect,
        TenantId tenantId,
        string? explicitKeyRef = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (!Enum.IsDefined(requestedFunction))
        {
            return ClassificationMaskResolution.Deny("MASK_UNKNOWN_FUNCTION", requestedFunction);
        }

        if (!MaskFunctionCatalog.TryParseAndValidate(requestedFunction, paramsJson, out var parsed, out _, explicitKeyRef))
        {
            return ClassificationMaskResolution.Deny("MASK_INVALID_PARAMETERS", requestedFunction);
        }

        var family = MaskFunctionCatalog.ClassifyColumnType(dataType);
        bool typeFallback = !MaskFunctionCatalog.IsCompatibleWithType(requestedFunction, family);
        var effectiveFunction = typeFallback ? MaskFunction.TypeSafeRedact : requestedFunction;
        if (typeFallback)
        {
            GatewayDiagnostics.MaskTypeFallbackCounter.Add(1,
                new KeyValuePair<string, object?>("requested_function", ClassificationMaskFunctions.ToCode(requestedFunction)));
        }

        var names = new MaskParameterNames(columnName);
        MaskSpec spec;
        switch (effectiveFunction)
        {
            case MaskFunction.Nullify:
                spec = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
                break;

            case MaskFunction.Redact:
            case MaskFunction.TypeSafeRedact:
                spec = Redact(names, parsed.Replacement ?? "***");
                break;

            case MaskFunction.Partial:
                spec = PartialMask(names, parsed.KeepFirst, parsed.KeepLast, parsed.MaskChar);
                break;

            case MaskFunction.GeoJitter:
                spec = new MaskSpec(MaskKind.GeoJitter, new MaskArguments(Decimals: parsed.GeoDecimals), FrozenDictionary<string, PolicyValue>.Empty);
                break;

            case MaskFunction.TruncateDate:
                spec = new MaskSpec(MaskKind.TruncateDate, new MaskArguments(TruncateUnit: parsed.TruncateUnit), FrozenDictionary<string, PolicyValue>.Empty);
                break;

            case MaskFunction.BucketNumber:
            {
                var widthParam = names.Parameter("bw", SqlParameterType.Decimal);
                spec = new MaskSpec(
                    MaskKind.BucketNumber,
                    new MaskArguments(BucketWidth: widthParam),
                    new Dictionary<string, PolicyValue> { [widthParam.Name] = new PolicyValue(parsed.BucketWidth!.Value, SqlParameterType.Decimal) }
                        .ToFrozenDictionary(StringComparer.Ordinal));
                break;
            }

            case MaskFunction.RoundNumber:
            {
                var digitsParam = names.Parameter("rd", SqlParameterType.Int32);
                spec = new MaskSpec(
                    MaskKind.RoundNumber,
                    new MaskArguments(RoundDigits: digitsParam),
                    new Dictionary<string, PolicyValue> { [digitsParam.Name] = new PolicyValue(parsed.RoundDigits, SqlParameterType.Int32) }
                        .ToFrozenDictionary(StringComparer.Ordinal));
                break;
            }

            case MaskFunction.EmailDomain:
                spec = new MaskSpec(MaskKind.EmailDomain, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
                break;

            case MaskFunction.Hash:
            case MaskFunction.Tokenize:
            {
                var masterKey = TryResolveClassificationKey(parsed.KeyRef);
                if (masterKey is null)
                {
                    return new ClassificationMaskResolution(true, "MASK_KEY_UNAVAILABLE", null, effectiveFunction, typeFallback);
                }

                if (_options.Value.DataMasking.PreventInDbHmacKeyExposure || dialect is not (TargetSqlDialect.PostgreSql or TargetSqlDialect.SqlServer))
                {
                    spec = Redact(names, "***");
                    effectiveFunction = MaskFunction.TypeSafeRedact;
                    break;
                }

                var kind = effectiveFunction == MaskFunction.Tokenize ? MaskKind.Tokenize : MaskKind.Hmac;
                spec = BuildHmacOrTokenizeSpec(names, dialect, masterKey, tenantId, parsed.KeyRef!, kind, parsed.TokenDomain);
                break;
            }

            default:
                return ClassificationMaskResolution.Deny("MASK_UNKNOWN_FUNCTION", effectiveFunction);
        }

        return new ClassificationMaskResolution(false, null, spec, effectiveFunction, typeFallback);
    }

    private byte[]? TryResolveClassificationKey(string? keyRef)
    {
        if (string.IsNullOrWhiteSpace(keyRef) || _secretProvider is null)
        {
            return null;
        }

        try
        {
            var fetched = _secretProvider.GetSecretBytes(keyRef);
            return fetched is { Length: > 0 } ? fetched : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or NotSupportedException)
        {
            return null;
        }
    }

    private MaskSpec? TryBuildHmacSpec(MaskParameterNames names, DatabaseDialect dialect, string keyId, TenantId tenantId, string columnName)
    {
        if (_options.Value.DataMasking.PreventInDbHmacKeyExposure)
        {
            _logger?.LogWarning("WebSQL in-DB HMAC key parameter binding is disabled by policy (PreventInDbHmacKeyExposure = true); column '{Column}' is redacted fail-closed.", columnName);
            return null;
        }

        // no in-database HMAC on Oracle without a DBMS_CRYPTO grant; other dialects have no in-database HMAC either.
        if (dialect is not (DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer))
        {
            return null;
        }

        var masterKey = GetMasterHmacKey();
        if (masterKey == null)
        {
            return null;
        }

        return BuildHmacOrTokenizeSpec(names, SqlDialectMapper.ToTargetDialect(dialect), masterKey, tenantId, keyId, MaskKind.Hmac, null);
    }

    private static MaskSpec BuildHmacOrTokenizeSpec(
        MaskParameterNames names,
        TargetSqlDialect dialect,
        byte[] masterKey,
        TenantId tenantId,
        string keyId,
        MaskKind kind,
        string? tokenDomain)
    {
        string hexKey = DeriveHexKey(masterKey, tenantId, keyId);
        var parameters = new Dictionary<string, PolicyValue>(StringComparer.Ordinal);
        PolicyParameterExpression? domainParam = null;
        if (kind == MaskKind.Tokenize)
        {
            domainParam = names.Parameter("td", SqlParameterType.String);
            parameters[domainParam.Name] = new PolicyValue(tokenDomain ?? "DEFAULT", SqlParameterType.String);
        }

        if (dialect == TargetSqlDialect.SqlServer)
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
            parameters[inner.Name] = new PolicyValue(innerPad, SqlParameterType.Binary);
            parameters[outer.Name] = new PolicyValue(outerPad, SqlParameterType.Binary);
            return new MaskSpec(kind, new MaskArguments(HmacKey: inner, HmacKeyOuter: outer, TokenDomain: domainParam),
                parameters.ToFrozenDictionary(StringComparer.Ordinal));
        }

        var key = names.Parameter("k", SqlParameterType.String);
        parameters[key.Name] = new PolicyValue(hexKey, SqlParameterType.String);
        return new MaskSpec(kind, new MaskArguments(HmacKey: key, TokenDomain: domainParam),
            parameters.ToFrozenDictionary(StringComparer.Ordinal));
    }

    internal static string DeriveHexKey(byte[] masterKey, TenantId tenantId, string keyId)
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
        if (dt.Contains('(', StringComparison.Ordinal)) dt = dt[..dt.IndexOf('(', StringComparison.Ordinal)].Trim();
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
        if (dt.Contains('(', StringComparison.Ordinal))
        {
            dt = dt[..dt.IndexOf('(', StringComparison.Ordinal)].Trim();
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
            var col = $"[{columnName.Replace("]", "]]", StringComparison.Ordinal)}]";
            return $"CASE WHEN CHARINDEX('@', CAST({col} AS NVARCHAR(MAX))) > 1 THEN SUBSTRING(CAST({col} AS NVARCHAR(MAX)), 1, 1) + '***@***' ELSE '***@***' END";
        }

        var quotedCol = $"\"{columnName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        if (dialect == DatabaseDialect.Oracle)
        {
            // CR-ADG-18: Oracle has no TEXT type and no POSITION function (CAST ... AS TEXT failed on the executable Oracle path).
            string text = $"CAST({quotedCol} AS VARCHAR2(4000))";
            return $"CASE WHEN INSTR({text}, '@') > 1 THEN SUBSTR({text}, 1, 1) || '***@***' ELSE '***@***' END";
        }

        return $"CASE WHEN POSITION('@' IN CAST({quotedCol} AS TEXT)) > 1 THEN SUBSTR(CAST({quotedCol} AS TEXT), 1, 1) || '***@***' ELSE '***@***' END";
    }

    public static string BuildIbanMaskExpression(string columnName, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.SqlServer)
        {
            var col = $"[{columnName.Replace("]", "]]", StringComparison.Ordinal)}]";
            return $"CASE WHEN LEN(CAST({col} AS NVARCHAR(MAX))) >= 8 THEN SUBSTRING(CAST({col} AS NVARCHAR(MAX)), 1, 2) + '** **** **** ' + RIGHT(CAST({col} AS NVARCHAR(MAX)), 4) ELSE '****' END";
        }

        var quotedCol = $"\"{columnName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        if (dialect == DatabaseDialect.Oracle)
        {
            string text = $"CAST({quotedCol} AS VARCHAR2(4000))";
            return $"CASE WHEN LENGTH({text}) >= 8 THEN SUBSTR({text}, 1, 2) || '** **** **** ' || SUBSTR({text}, -4) ELSE '****' END";
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
            // no in-database HMAC on Oracle without a DBMS_CRYPTO grant; the column is redacted (fail closed), never
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
                $"ENCODE(HMAC(CAST(\"{columnName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" AS TEXT), CAST({keyMarker} AS TEXT), 'sha256'), 'hex')",
            DatabaseDialect.SqlServer =>
                $"CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', {outerMarker} + HASHBYTES('SHA2_256', {innerMarker} + CAST(CAST([{columnName.Replace("]", "]]", StringComparison.Ordinal)}] AS NVARCHAR(MAX)) AS VARBINARY(MAX)))), 2)",
            _ => null
        };
    }

    /// <summary>
    /// SQLA-03: resolves the master HMAC key under a lock. A key is cached only after a successful fetch; a failed resolve is
    /// retried after <see cref="KeyRetryBackoff"/> instead of disabling HMAC pseudonymization for the life of the process.
    /// </summary>
    private byte[]? GetMasterHmacKey()
    {
        var cached = Volatile.Read(ref _masterHmacKey);
        if (cached != null)
        {
            return cached;
        }

        lock (_keyLock)
        {
            if (_masterHmacKey != null)
            {
                return _masterHmacKey;
            }

            var now = _timeProvider.GetUtcNow();
            if (now < _keyRetryNotBefore)
            {
                return null;
            }

            var secretRef = _options.Value.DataMasking.HmacSecretKeyVaultRef;
            byte[]? key = null;
            if (_secretProvider == null || string.IsNullOrWhiteSpace(secretRef))
            {
                _logger?.LogWarning("WebSQL HMAC masking key is not resolvable (no secret provider or secret reference); HMAC columns are redacted.");
            }
            else
            {
                try
                {
                    var fetched = _secretProvider.GetSecretBytes(secretRef);
                    key = fetched is { Length: > 0 } ? fetched : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
                {
                    _logger?.LogWarning("WebSQL HMAC masking secret could not be resolved; HMAC columns are redacted.");
                }
            }

            if (key == null)
            {
                _keyRetryNotBefore = now + KeyRetryBackoff;
                return null;
            }

            Volatile.Write(ref _masterHmacKey, key);
            return key;
        }
    }
}
