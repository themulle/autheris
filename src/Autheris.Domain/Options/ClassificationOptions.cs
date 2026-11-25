namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Root configuration options for data classification, generic label taxonomies, PII categories and workflow policies.
/// </summary>
public sealed class ClassificationOptions
{
    public const string SectionName = "Gateway:Classification";

    /// <summary>Default sensitivity level key assigned when a column has no explicit classification.</summary>
    public string DefaultSensitivityKey { get; init; } = "L2_INTERNAL";

    /// <summary>Configured mapping from classification level keys to canonical <see cref="ProtectionClass"/> values.</summary>
    public Dictionary<string, ProtectionClass> LevelProtectionClasses { get; init; } = GetDefaultLevelProtectionClasses();

    /// <summary>Configured sensitivity level taxonomy ordered by rank.</summary>
    public List<SensitivityLevelDefinition> SensitivityLevels { get; init; } = GetDefaultSensitivityLevels();

    /// <summary>Configured PII category definitions and column-name matching patterns.</summary>
    public List<PiiCategoryDefinition> PiiCategories { get; init; } = GetDefaultPiiCategories();

    /// <summary>Classification workflow profile selection and definitions (<c>Gateway:Classification:Workflow</c>).</summary>
    public WorkflowOptions Workflow { get; init; } = new();

    public static Dictionary<string, ProtectionClass> GetDefaultLevelProtectionClasses() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["L1_PUBLIC"] = ProtectionClass.Public,
        ["PUBLIC"] = ProtectionClass.Public,
        ["L2_INTERNAL"] = ProtectionClass.Internal,
        ["INTERNAL"] = ProtectionClass.Internal,
        ["NORMAL"] = ProtectionClass.Internal,
        ["L3_CONFIDENTIAL"] = ProtectionClass.Confidential,
        ["CONFIDENTIAL"] = ProtectionClass.Confidential,
        ["HIGH"] = ProtectionClass.Confidential,
        ["RESTRICTED"] = ProtectionClass.Confidential,
        ["L4_STRICTLY_CONFIDENTIAL"] = ProtectionClass.Secret,
        ["SECRET"] = ProtectionClass.Secret
    };

    public ProtectionClass ResolveProtectionClass(string? levelKey, int? fallbackRank = null)
    {
        if (!string.IsNullOrWhiteSpace(levelKey))
        {
            if (LevelProtectionClasses.TryGetValue(levelKey.Trim(), out var mapped))
            {
                return mapped.NormalizeOrConfidential();
            }

            foreach (var level in SensitivityLevels)
            {
                if (string.Equals(level.Key, levelKey.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return level.ProtectionClass.NormalizeOrConfidential();
                }
            }
        }

        return fallbackRank switch
        {
            <= 1 => ProtectionClass.Public,
            2 => ProtectionClass.Internal,
            3 => ProtectionClass.Confidential,
            >= 4 => ProtectionClass.Secret,
            _ => ProtectionClass.Confidential
        };
    }

    public static List<SensitivityLevelDefinition> GetDefaultSensitivityLevels() => new()
    {
        new("L1_PUBLIC", "Stufe 1: Öffentlich", "Frei zugängliche Daten, Pressemitteilungen, öffentliche Stammdaten.", 1, RequiresStepUpAuth: false, MaxConsentTtlDays: 365, ProtectionClass: ProtectionClass.Public),
        new("L2_INTERNAL", "Stufe 2: Intern", "Betriebsinterne Informationen ohne direkten Personen- oder Finanzbezug.", 2, RequiresStepUpAuth: false, MaxConsentTtlDays: 180, ProtectionClass: ProtectionClass.Internal),
        new("L3_CONFIDENTIAL", "Stufe 3: Vertraulich", "Sensible Geschäftsdaten, Kundendaten, Standard-PII, interne Verträge.", 3, RequiresStepUpAuth: false, MaxConsentTtlDays: 60, ProtectionClass: ProtectionClass.Confidential),
        new("L4_STRICTLY_CONFIDENTIAL", "Stufe 4: Höchst vertraulich", "Gehälter, Gesundheitsdaten, M&A-Planungen, biometrische Daten.", 4, RequiresStepUpAuth: true, MaxConsentTtlDays: 7, ProtectionClass: ProtectionClass.Secret)
    };

    public static List<PiiCategoryDefinition> GetDefaultPiiCategories() => new()
    {
        new("IBAN", "Internationale Bankkontonummer", "Bankverbindung nach ISO 13616 / SEPA.", 3, "IBAN_STANDARD_4_4", new[] { ".*iban.*", "^bank_account.*", "^kto_nr$" }, ClassificationScopes.FinancialCode),
        new("CREDIT_CARD", "Kreditkartennummer (PAN)", "16-stellige Zahlungs- und Kreditkartennummern (PCI-DSS Scope).", 4, "CREDIT_CARD_LAST_4", new[] { ".*credit.*card.*", ".*pan.*", ".*cc_num.*" }, ClassificationScopes.FinancialCode),
        new("EMAIL", "E-Mail-Adresse", "Personenbezogene geschäftliche oder private Mailadresse.", 2, "EMAIL_DOMAIN_RETAIN", new[] { ".*email.*", ".*mail.*" }, ClassificationScopes.PiiCode),
        new("PHONE_NUMBER", "Telefon- / Mobilnummer", "Festnetz- oder Mobiltelefonnummer nach E.164.", 2, "PHONE_RETAIN_COUNTRY_CODE", new[] { ".*phone.*", ".*telefon.*", ".*mobil.*", ".*fax.*" }, ClassificationScopes.PiiCode),
        new("IP_ADDRESS", "IP-Adresse (IPv4 / IPv6)", "Netzwerkadresse (nach DSGVO personenbezogenes Datum).", 2, "IP_ANONYMIZE_SUBNET", new[] { "^ip$", ".*_ip$", "^ip_address$", "^client_ip$" }, ClassificationScopes.PiiCode),
        new("BIRTH_DATE", "Geburtsdatum", "Geburtsdatum einer natürlichen Person.", 3, "DATE_TRUNCATE_TO_YEAR", new[] { ".*birth.*", ".*dob.*", ".*geburtsdatum.*" }, ClassificationScopes.PiiCode),
        new("FULL_NAME", "Vollständiger Name / Person", "Vorname, Nachname oder zusammengesetzter Personenname.", 2, "NAME_INITIALS_ONLY", new[] { "^name$", "^full_name$", "^nachname$", "^vorname$", ".*_name$" }, ClassificationScopes.PiiCode),
        new("GEO_LOCATION", "Geokoordinaten", "GPS-Koordinaten (Latitude / Longitude).", 2, "GEO_DISTRICT_500M", new[] { "^lat$", "^lon$", ".*lat.*", ".*lon.*", ".*latitude.*", ".*longitude.*", ".*geo.*" }, ClassificationScopes.PiiCode),
        new("HEALTH_DATA", "Gesundheitsdaten (Art. 9 DSGVO / HIPAA)", "Diagnosen, Laborwerte, ICD-10-Codes oder Patientenhistorie.", 4, "REDACT_COMPLETELY", new[] { ".*diagnos.*", ".*icd10.*", ".*health.*", ".*befund.*" }, ClassificationScopes.PiiSpecialCode)
    };
}

/// <summary>
/// Workflow options and configuration profiles.
/// </summary>
public sealed class WorkflowOptions
{
    /// <summary>Name of the active classification workflow profile in <see cref="Profiles"/>.</summary>
    public string ActiveProfile { get; init; } = "ENTERPRISE_HYBRID";

    /// <summary>Named classification workflow profiles controlling owner resolution, pre-classification and approval stages.</summary>
    public Dictionary<string, ClassificationWorkflowProfile> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTERPRISE_HYBRID"] = new(
            new OwnerResolutionConfig("MetadataFirstThenCatalogCascade"),
            new PreClassificationConfig("Disabled", ZeroTouchAutoApprovePublicAndLowRisk: false, AutoApproveConfidenceThreshold: 0.95, AutoApproveMaxSensitivityRank: 1),
            new ApprovalPipelineConfig(RequireStepUpAuthThresholdRank: 4)
        ),
        ["LEAN_DATA_MESH"] = new(
            new OwnerResolutionConfig("MetadataFirstThenCatalogCascade"),
            new PreClassificationConfig("Disabled", ZeroTouchAutoApprovePublicAndLowRisk: true, AutoApproveConfidenceThreshold: 0.95, AutoApproveMaxSensitivityRank: 2),
            new ApprovalPipelineConfig(RequireStepUpAuthThresholdRank: 999)
        ),
        ["TRADITIONAL_NO_AI_ENTERPRISE"] = new(
            new OwnerResolutionConfig("MetadataFirstThenCatalogCascade"),
            new PreClassificationConfig("Disabled", ZeroTouchAutoApprovePublicAndLowRisk: false),
            new ApprovalPipelineConfig(RequireStepUpAuthThresholdRank: 4)
        )
    };

    public ClassificationWorkflowProfile GetActiveProfile() =>
        Profiles.TryGetValue(ActiveProfile, out var profile) ? profile : Profiles["ENTERPRISE_HYBRID"];
}
