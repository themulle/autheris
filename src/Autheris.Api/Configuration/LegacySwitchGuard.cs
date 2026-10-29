using Microsoft.Extensions.Configuration;

namespace Autheris.Api.Configuration;

/// <summary>
/// ADR-012 / concept phase 4 and the no-legacy sweep: removed keys fail startup on <b>presence</b>, whatever their value (<c>false</c> included),
/// instead of being ignored or merely warned about. Messages name the key and its replacement, never a value (DEF-SC-11).
/// </summary>
public static class LegacySwitchGuard
{
    private const string G = "Gateway:";

    /// <summary>Removed key (relative to <c>Gateway:</c>) to its replacement.</summary>
    public static IReadOnlyDictionary<string, string> Replacements { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authentication:danger_allow_anonymous_access"] = "Insecure:danger_allow_anonymous_access",
            ["GovernanceDb:danger_bypass_consent_checks"] = "Insecure:danger_bypass_consent_checks",
            ["GovernanceDb:warn_auto_approve_access_requests"] = "Insecure:warn_auto_approve_access_requests",
            ["RateLimiting:warn_disable_rate_limiting"] = "Insecure:warn_disable_rate_limiting",
            ["GraphQL:warn_allow_all_cors_origins"] = "Insecure:warn_allow_all_cors_origins",
            ["GraphQL:warn_relaxed_query_limits"] = "Insecure:warn_relaxed_query_limits",
            ["GraphQL:warn_enable_introspection"] = "Insecure:warn_enable_introspection",
            ["DataMasking:danger_disable_column_masking"] = "Insecure:danger_disable_column_masking",
            ["OpenMetadata:danger_bypass_webhook_signature_validation"] = "Insecure:danger_bypass_webhook_signature_validation",
            ["OpenMetadata:warn_ignore_webhook_timestamp_tolerance"] = "Insecure:warn_ignore_webhook_timestamp_tolerance",
            ["OpenMetadata:danger_allow_untrusted_certificates"] = "Insecure:danger_allow_untrusted_certificates",
            ["Itsm:danger_bypass_webhook_signature_validation"] = "Insecure:danger_bypass_webhook_signature_validation",
            ["Itsm:warn_ignore_webhook_timestamp_tolerance"] = "Insecure:warn_ignore_webhook_timestamp_tolerance",
            ["Itsm:warn_mock_external_systems_if_unreachable"] = "Insecure:warn_mock_external_systems_if_unreachable",
            ["Itsm:danger_allow_untrusted_certificates"] = "Insecure:danger_allow_untrusted_certificates",
            ["Mcp:warn_allow_unmasked_ai_access"] = "Insecure:warn_allow_unmasked_ai_access",
            ["Mcp:danger_bypass_mcp_auth"] = "Insecure:danger_bypass_mcp_auth",
            ["Lakehouse:warn_allow_unsigned_s3_requests"] = "Insecure:warn_allow_unsigned_s3_requests",
            ["Lakehouse:danger_bypass_lakehouse_auth"] = "Insecure:danger_bypass_lakehouse_auth",
            ["Dbt:danger_bypass_webhook_signature_validation"] = "Insecure:danger_bypass_webhook_signature_validation",
            ["WebSql:warn_allow_dml"] = "WebSql:AllowDml",
            ["Insecure:warn_allow_websql_dml"] = "WebSql:AllowDml",
            ["WebSql:danger_bypass_sql_governance"] = "Insecure:danger_bypass_websql_governance",
            ["Profile"] = "Dev:Preset"
        };

    /// <summary>Removed keys (relative to <c>Gateway:</c>) without a replacement: the behaviour they enabled no longer exists.</summary>
    public static IReadOnlyCollection<string> Removed { get; } =
    [
        "Itsm:warn_fallback_default_tenant_for_webhooks",
        "Insecure:warn_fallback_default_tenant_for_webhooks",
        "Caching:Redis:FailClosedOnRevocationLookupError"
    ];

    /// <summary>Returns one message per removed key that is present (any value).</summary>
    public static IReadOnlyList<string> FindViolations(IConfiguration configuration) =>
        Replacements
            .Where(r => configuration.GetSection(G + r.Key).Exists())
            .Select(r => $"'{G}{r.Key}' was removed; use '{G}{r.Value}' instead.")
            .Concat(Removed
                .Where(k => configuration.GetSection(G + k).Exists())
                .Select(k => $"'{G}{k}' was removed together with the behaviour it enabled (for example an unbound webhook is always rejected, a failed token revocation lookup always rejects the token); delete the key."))
            .ToList();

    public static void ThrowIfLegacyKeysSet(IConfiguration configuration)
    {
        var violations = FindViolations(configuration);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "Removed configuration keys are still configured:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }
}
