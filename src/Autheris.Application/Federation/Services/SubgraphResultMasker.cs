namespace Autheris.Application.Federation.Services;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SubgraphResultMasker : ISubgraphResultMasker
{
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<SubgraphResultMasker> _logger;
    private readonly IGatewayRoleEvaluator _roleEvaluator;
    private readonly IFederatedMaskingCatalog? _catalog;

    private static readonly MaskingRule EmailMaskRule = new() { RuleType = "MASK_EMAIL" };
    private static readonly MaskingRule IbanMaskRule = new() { RuleType = "MASK_IBAN" };
    private static readonly MaskingRule PhoneMaskRule = new() { RuleType = "MASK_PHONE" };
    private static readonly MaskingRule RedactMaskRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };
    private static readonly Autheris.Domain.Security.GatewayRole[] AdminRoles = [Autheris.Domain.Security.GatewayRole.GovernanceAdmin, Autheris.Domain.Security.GatewayRole.ClusterAdmin];

    public SubgraphResultMasker(
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> options,
        ILogger<SubgraphResultMasker> logger,
        IGatewayRoleEvaluator? roleEvaluator = null,
        IFederatedMaskingCatalog? catalog = null)
    {
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _roleEvaluator = roleEvaluator ?? new Autheris.Application.Security.GatewayRoleEvaluator();
        _catalog = catalog;
    }

    /// <summary>Per-request classification state: catalog map, config overrides and the fail-closed flag.</summary>
    private sealed class MaskContext
    {
        public static readonly MaskContext Empty = new(new Dictionary<string, MaskingRule?>(), new Dictionary<string, MaskingRule?>(), false);

        public MaskContext(
            IReadOnlyDictionary<string, MaskingRule?> catalog,
            IReadOnlyDictionary<string, MaskingRule?> config,
            bool maskUnknownScalars)
        {
            Catalog = catalog;
            Config = config;
            MaskUnknownScalars = maskUnknownScalars;
        }

        public IReadOnlyDictionary<string, MaskingRule?> Catalog { get; }

        /// <summary>Config overrides; a null rule means the field is explicitly exempt (<c>NONE</c>).</summary>
        public IReadOnlyDictionary<string, MaskingRule?> Config { get; }

        public bool MaskUnknownScalars { get; }
    }

    private Dictionary<string, MaskingRule?> BuildConfigMap()
    {
        var map = new Dictionary<string, MaskingRule?>(StringComparer.Ordinal);
        var configured = _options.Value.Federation.FieldClassifications;
        if (configured == null) return map;

        foreach (var (key, ruleType) in configured)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(ruleType)) continue;
            var lastDot = key.LastIndexOf('.');
            var field = FederatedFieldName.Normalize(lastDot >= 0 ? key[(lastDot + 1)..] : key);
            if (field.Length == 0) continue;

            var type = ruleType.Trim().ToUpperInvariant();
            map[field] = type == "NONE" ? null : new MaskingRule { RuleType = type, Replacement = "[REDACTED]" };
        }

        return map;
    }

    private async ValueTask<MaskContext> BuildContextAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        var config = BuildConfigMap();
        var failClosedScalars = _options.Value.Federation.DefaultMaskUnknownSensitiveScalars;
        if (_catalog == null)
        {
            return new MaskContext(new Dictionary<string, MaskingRule?>(), config, false);
        }

        try
        {
            var tenant = principal.GetTenantId();
            var classifications = await _catalog.GetClassificationsAsync(tenant, ct).ConfigureAwait(false);
            var catalogMap = new Dictionary<string, MaskingRule?>(classifications.Count, StringComparer.Ordinal);
            foreach (var (name, rule) in classifications)
            {
                catalogMap[name] = rule;
            }

            return new MaskContext(catalogMap, config, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: fall back to config + heuristic and, when enabled, mask all non-identifier scalars.
            _logger.LogWarning(ex, "Catalog-driven federated masking classification unavailable; falling back to heuristic masking.");
            return new MaskContext(new Dictionary<string, MaskingRule?>(), config, failClosedScalars);
        }
    }

    public async ValueTask<object?> MaskResultDataAsync(
        object? data,
        ClaimsPrincipal? principal,
        IReadOnlyDictionary<string, string>? aliasToFieldMap,
        CancellationToken cancellationToken = default)
    {
        if (data == null) return null;

        var fedOptions = _options.Value.Federation;
        if (!fedOptions.EnableResultMasking || _options.Value.IsColumnMaskingDisabled)
        {
            return data;
        }

        if (IsAdmin(principal))
        {
            return data;
        }

        var ctx = await BuildContextAsync(principal, cancellationToken).ConfigureAwait(false);
        return MaskRecursive(data, aliasToFieldMap, null, ctx);
    }

    /// <summary>Merges two alias targets into one '|'-separated candidate list (duplicates removed).</summary>
    public static string MergeAliasTargets(string existing, string added)
    {
        var parts = existing.Split('|');
        return Array.IndexOf(parts, added) >= 0 ? existing : existing + "|" + added;
    }

    private bool IsAdmin(ClaimsPrincipal? principal)
    {
        if (principal == null) return false;
        return _roleEvaluator.HasAnyRole(principal, AdminRoles);
    }

    public object? MaskResultData(object? data, ClaimsPrincipal? principal)
        => MaskResultData(data, principal, null);

    public object? MaskResultData(object? data, ClaimsPrincipal? principal, IReadOnlyDictionary<string, string>? aliasToFieldMap)
    {
        if (data == null) return null;

        var fedOptions = _options.Value.Federation;
        if (!fedOptions.EnableResultMasking || _options.Value.IsColumnMaskingDisabled)
        {
            return data;
        }

        if (IsAdmin(principal))
        {
            return data;
        }

        // Synchronous entry point; the catalog lookup is only awaited when a catalog is configured (prefer MaskResultDataAsync).
        if (_catalog == null)
        {
            return MaskRecursive(data, aliasToFieldMap, null, new MaskContext(new Dictionary<string, MaskingRule?>(), BuildConfigMap(), false));
        }

        return MaskResultDataAsync(data, principal, aliasToFieldMap).AsTask().GetAwaiter().GetResult();
    }

    private object? MaskRecursive(object? node, IReadOnlyDictionary<string, string>? aliasToFieldMap, string? path, MaskContext ctx)
    {
        if (node == null) return null;

        if (node is IReadOnlyDictionary<string, object?> dict)
        {
            var result = new Dictionary<string, object?>(dict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in dict)
            {
                var childPath = AppendPath(path, k);
                if (ShouldMaskField(k, childPath, v, aliasToFieldMap, ctx, out var rule))
                {
                    result[k] = _maskingProvider.MaskValue(k, v, rule);
                }
                else
                {
                    result[k] = MaskRecursive(v, aliasToFieldMap, childPath, ctx);
                }
            }
            return result;
        }

        if (node is IDictionary legacyDict)
        {
            var result = new Dictionary<string, object?>(legacyDict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in legacyDict)
            {
                var k = entry.Key?.ToString() ?? string.Empty;
                var v = entry.Value;
                var childPath = AppendPath(path, k);
                if (ShouldMaskField(k, childPath, v, aliasToFieldMap, ctx, out var rule))
                {
                    result[k] = _maskingProvider.MaskValue(k, v, rule);
                }
                else
                {
                    result[k] = MaskRecursive(v, aliasToFieldMap, childPath, ctx);
                }
            }
            return result;
        }

        if (node is IEnumerable list && node is not string)
        {
            // List elements share the path of the list field (indices are not part of the alias path).
            var resultList = new List<object?>();
            foreach (var item in list)
            {
                resultList.Add(MaskRecursive(item, aliasToFieldMap, path, ctx));
            }
            return resultList;
        }

        return node;
    }

    /// <summary>
    /// SEC (Federation alias collision): Builds the alias path, i.e. the chain of response keys separated by '.'
    /// (GraphQL names cannot contain '.', so the separator is unambiguous).
    /// </summary>
    public static string AppendPath(string? parentPath, string responseKey)
        => string.IsNullOrEmpty(parentPath) ? responseKey : parentPath + "." + responseKey;

    /// <summary>
    /// Returns true if the given (real) field name is treated as sensitive by the federated result masker.
    /// </summary>
    public static bool IsSensitiveField(string fieldName)
        => !string.IsNullOrEmpty(fieldName) && IsSensitiveFieldName(fieldName, out _);

    private static bool ShouldMaskField(
        string responseKey,
        string path,
        object? value,
        IReadOnlyDictionary<string, string>? aliasToFieldMap,
        MaskContext ctx,
        out MaskingRule rule)
    {
        // SEC (Federation alias collision): aliases are resolved per response path ("a.x"), not via a flat global map,
        // so `{ a { x: email } b { x: id } }` masks a.x without the b.x alias overriding it (and vice versa).
        if (aliasToFieldMap != null && aliasToFieldMap.TryGetValue(path, out var realFieldNames))
        {
            // Several candidates ('|'-separated) arise from conflicting type conditions on the same path; any sensitive one masks.
            foreach (var realFieldName in realFieldNames.Split('|'))
            {
                if (ClassifyName(realFieldName, ctx, out rule))
                {
                    return true;
                }
            }
        }

        if (ClassifyName(responseKey, ctx, out rule))
        {
            return true;
        }

        // Fail-closed mode (catalog unavailable): mask every non-identifier scalar leaf.
        if (ctx.MaskUnknownScalars && IsScalarLeaf(value) && !IsIdentifierName(responseKey))
        {
            rule = RedactMaskRule;
            return true;
        }

        rule = RedactMaskRule;
        return false;
    }

    /// <summary>Precedence: explicit config override, then catalog classification, then the legacy name heuristic.</summary>
    private static bool ClassifyName(string fieldName, MaskContext ctx, out MaskingRule rule)
    {
        var normalized = FederatedFieldName.Normalize(fieldName);
        if (ctx.Config.TryGetValue(normalized, out var configured))
        {
            if (configured == null)
            {
                rule = RedactMaskRule;
                return false;
            }

            rule = configured;
            return true;
        }

        if (ctx.Catalog.TryGetValue(normalized, out var cataloged) && cataloged != null)
        {
            rule = cataloged;
            return true;
        }

        return IsSensitiveFieldName(fieldName, out rule);
    }

    private static bool IsScalarLeaf(object? value)
        => value != null && (value is string || (value is not IEnumerable && value is not IDictionary));

    private static bool IsIdentifierName(string name)
        => name.Equals("__typename", StringComparison.Ordinal)
           || name.Equals("id", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("Id", StringComparison.Ordinal);

    private static bool IsSensitiveFieldName(string fieldName, out MaskingRule rule)
    {
        var lower = fieldName.ToLowerInvariant();

        if (lower.Contains("email", StringComparison.Ordinal) || lower.EndsWith("mail", StringComparison.Ordinal))
        {
            rule = EmailMaskRule;
            return true;
        }

        if (lower.Contains("iban", StringComparison.Ordinal) || lower.Contains("bankaccount", StringComparison.Ordinal))
        {
            rule = IbanMaskRule;
            return true;
        }

        if (lower.Contains("phone", StringComparison.Ordinal) || lower.Contains("mobile", StringComparison.Ordinal) || lower.Contains("telefon", StringComparison.Ordinal))
        {
            rule = PhoneMaskRule;
            return true;
        }

        if (lower == "ssn" || lower.Contains("socialsecurity", StringComparison.Ordinal) ||
            lower.Contains("salary", StringComparison.Ordinal) || lower.Contains("gehalt", StringComparison.Ordinal) ||
            lower.Contains("compensation", StringComparison.Ordinal) || lower.Contains("wage", StringComparison.Ordinal) ||
            lower.Contains("balance", StringComparison.Ordinal) || lower.Contains("saldo", StringComparison.Ordinal) ||
            lower.Contains("taxid", StringComparison.Ordinal) || lower.Contains("steuernummer", StringComparison.Ordinal) ||
            lower.Contains("birth", StringComparison.Ordinal) || lower.Contains("geburtsdatum", StringComparison.Ordinal) ||
            lower == "diagnosis" || lower == "medicalrecord" ||
            lower == "healthcondition" || lower.Contains("creditcard", StringComparison.Ordinal) ||
            lower.Contains("passwort", StringComparison.Ordinal) || lower.Contains("password", StringComparison.Ordinal) ||
            // SR-L-12: modest floor of additional examples; a heuristic, not a policy (catalog classification is authoritative).
            lower.Contains("taxnumber", StringComparison.Ordinal) || lower.Contains("passport", StringComparison.Ordinal) ||
            lower.Contains("nationalid", StringComparison.Ordinal) || lower.Contains("homeaddress", StringComparison.Ordinal) ||
            lower.Contains("licenseplate", StringComparison.Ordinal))
        {
            rule = RedactMaskRule;
            return true;
        }

        rule = RedactMaskRule;
        return false;
    }
}
