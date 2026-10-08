namespace Autheris.Application.Federation.Services;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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

    private static readonly MaskingRule EmailMaskRule = new() { RuleType = "MASK_EMAIL" };
    private static readonly MaskingRule IbanMaskRule = new() { RuleType = "MASK_IBAN" };
    private static readonly MaskingRule PhoneMaskRule = new() { RuleType = "MASK_PHONE" };
    private static readonly MaskingRule RedactMaskRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };
    private static readonly Autheris.Domain.Security.GatewayRole[] AdminRoles = [Autheris.Domain.Security.GatewayRole.GovernanceAdmin, Autheris.Domain.Security.GatewayRole.ClusterAdmin];

    public SubgraphResultMasker(
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> options,
        ILogger<SubgraphResultMasker> logger,
        IGatewayRoleEvaluator? roleEvaluator = null)
    {
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _roleEvaluator = roleEvaluator ?? new Autheris.Application.Security.GatewayRoleEvaluator();
    }

    private bool IsAdmin(ClaimsPrincipal? principal)
    {
        if (principal == null) return false;
        return _roleEvaluator.HasAnyRole(principal, AdminRoles);
    }

    public object? MaskResultData(object? data, ClaimsPrincipal? principal)
    {
        if (data == null) return null;

        var fedOptions = _options.Value.Federation;
        if (!fedOptions.EnableResultMasking || _options.Value.IsColumnMaskingDisabled)
        {
            return data;
        }

        // Privileged governance roles with full cleartext access
        if (IsAdmin(principal))
        {
            return data;
        }

        return MaskResultData(data, principal, null);
    }

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

        return MaskRecursive(data, aliasToFieldMap, null);
    }

    private object? MaskRecursive(object? node, IReadOnlyDictionary<string, string>? aliasToFieldMap, string? path)
    {
        if (node == null) return null;

        if (node is IReadOnlyDictionary<string, object?> dict)
        {
            var result = new Dictionary<string, object?>(dict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in dict)
            {
                var childPath = AppendPath(path, k);
                if (ShouldMaskField(k, childPath, aliasToFieldMap, out var rule))
                {
                    result[k] = _maskingProvider.MaskValue(k, v, rule);
                }
                else
                {
                    result[k] = MaskRecursive(v, aliasToFieldMap, childPath);
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
                if (ShouldMaskField(k, childPath, aliasToFieldMap, out var rule))
                {
                    result[k] = _maskingProvider.MaskValue(k, v, rule);
                }
                else
                {
                    result[k] = MaskRecursive(v, aliasToFieldMap, childPath);
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
                resultList.Add(MaskRecursive(item, aliasToFieldMap, path));
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

    private static bool ShouldMaskField(string responseKey, string path, IReadOnlyDictionary<string, string>? aliasToFieldMap, out MaskingRule rule)
    {
        // SEC (Federation alias collision): aliases are resolved per response path ("a.x"), not via a flat global map,
        // so `{ a { x: email } b { x: id } }` masks a.x without the b.x alias overriding it (and vice versa).
        if (aliasToFieldMap != null && aliasToFieldMap.TryGetValue(path, out var realFieldName))
        {
            if (IsSensitiveFieldName(realFieldName, out rule))
            {
                return true;
            }
        }

        return IsSensitiveFieldName(responseKey, out rule);
    }

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
            lower.Contains("passwort", StringComparison.Ordinal) || lower.Contains("password", StringComparison.Ordinal))
        {
            rule = RedactMaskRule;
            return true;
        }

        rule = RedactMaskRule;
        return false;
    }
}
