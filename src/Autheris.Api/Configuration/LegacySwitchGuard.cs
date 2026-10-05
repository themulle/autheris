using Microsoft.Extensions.Configuration;

namespace Autheris.Api.Configuration;

/// <summary>
/// ADR-012 / concept phase 4: the domain-local <c>warn_</c> / <c>danger_</c> duplicates were removed; every such
/// switch now lives only in <c>Gateway:Insecure</c>. A removed key that is still set to <c>true</c> would silently
/// stop relaxing anything, so startup fails with the replacement key instead. A value of <c>false</c> changes nothing
/// and is tolerated.
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
            ["Itsm:warn_fallback_default_tenant_for_webhooks"] = "Insecure:warn_fallback_default_tenant_for_webhooks",
            ["Itsm:warn_mock_external_systems_if_unreachable"] = "Insecure:warn_mock_external_systems_if_unreachable",
            ["Itsm:danger_allow_untrusted_certificates"] = "Insecure:danger_allow_untrusted_certificates",
            ["Mcp:warn_allow_unmasked_ai_access"] = "Insecure:warn_allow_unmasked_ai_access",
            ["Mcp:danger_bypass_mcp_auth"] = "Insecure:danger_bypass_mcp_auth",
            ["Lakehouse:warn_allow_unsigned_s3_requests"] = "Insecure:warn_allow_unsigned_s3_requests",
            ["Lakehouse:danger_bypass_lakehouse_auth"] = "Insecure:danger_bypass_lakehouse_auth",
            ["Dbt:danger_bypass_webhook_signature_validation"] = "Insecure:danger_bypass_webhook_signature_validation",
            ["WebSql:warn_allow_dml"] = "WebSql:AllowDml",
            ["WebSql:danger_bypass_sql_governance"] = "Insecure:danger_bypass_websql_governance"
        };

    /// <summary>Returns one message per removed key that is still set to <c>true</c>.</summary>
    public static IReadOnlyList<string> FindViolations(IConfiguration configuration) =>
        Replacements
            .Where(r => bool.TryParse(configuration[G + r.Key], out var on) && on)
            .Select(r => $"'{G}{r.Key}' was removed (ADR-012 phase 4); use '{G}{r.Value}' instead.")
            .ToList();

    public static void ThrowIfLegacyKeysSet(IConfiguration configuration)
    {
        var violations = FindViolations(configuration);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "Removed insecure-switch keys are still configured:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }
}
