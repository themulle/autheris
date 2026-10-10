namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Root configuration options for data classification, generic label taxonomies, PII categories and workflow policies.
/// </summary>
public sealed class ClassificationOptions
{
    public const string SectionName = "Gateway:Classification";

    public string DefaultSensitivityKey { get; init; } = "L2_INTERNAL";
    public int FourEyesThresholdRank { get; init; } = 3;
    public int StepUpAuthThresholdRank { get; init; } = 4;
    public int AutoApproveMaxRank { get; init; } = 1;

    public List<SensitivityLevelDefinition> SensitivityLevels { get; init; } = GetDefaultSensitivityLevels();
    public List<PiiCategoryDefinition> PiiCategories { get; init; } = GetDefaultPiiCategories();
    public List<NamedMaskingRule> MaskingRules { get; init; } = GetDefaultMaskingRules();
    public WorkflowOptions Workflow { get; init; } = new();

    public static List<SensitivityLevelDefinition> GetDefaultSensitivityLevels() => new()
    {
        new("L1_PUBLIC", "Stufe 1: Öffentlich", "Frei zugängliche Daten, Pressemitteilungen, öffentliche Stammdaten.", 1, RequiresFourEyes: false, RequiresStepUpAuth: false, MaxConsentTtlDays: 365),
        new("L2_INTERNAL", "Stufe 2: Intern", "Betriebsinterne Informationen ohne direkten Personen- oder Finanzbezug.", 2, RequiresFourEyes: false, RequiresStepUpAuth: false, MaxConsentTtlDays: 180),
        new("L3_CONFIDENTIAL", "Stufe 3: Vertraulich", "Sensible Geschäftsdaten, Kundendaten, Standard-PII, interne Verträge.", 3, RequiresFourEyes: true, RequiresStepUpAuth: false, MaxConsentTtlDays: 60),
        new("L4_STRICTLY_CONFIDENTIAL", "Stufe 4: Höchst vertraulich", "Gehälter, Gesundheitsdaten, M&A-Planungen, biometrische Daten.", 4, RequiresFourEyes: true, RequiresStepUpAuth: true, MaxConsentTtlDays: 7)
    };

    public static List<PiiCategoryDefinition> GetDefaultPiiCategories() => new()
    {
        new("IBAN", "Internationale Bankkontonummer", "Bankverbindung nach ISO 13616 / SEPA.", 3, "IBAN_STANDARD_4_4", new[] { "^iban$", ".*_iban$", "^bank_account.*", "^kto_nr$" }),
        new("CREDIT_CARD", "Kreditkartennummer (PAN)", "16-stellige Zahlungs- und Kreditkartennummern (PCI-DSS Scope).", 4, "CREDIT_CARD_LAST_4", new[] { ".*credit.*card.*", ".*pan.*", ".*cc_num.*" }),
        new("EMAIL", "E-Mail-Adresse", "Personenbezogene geschäftliche oder private Mailadresse.", 2, "EMAIL_DOMAIN_RETAIN", new[] { "^email$", ".*_email$", "^mail$", ".*_mail$" }),
        new("PHONE_NUMBER", "Telefon- / Mobilnummer", "Festnetz- oder Mobiltelefonnummer nach E.164.", 2, "PHONE_RETAIN_COUNTRY_CODE", new[] { ".*phone.*", ".*telefon.*", ".*mobil.*", ".*fax.*" }),
        new("IP_ADDRESS", "IP-Adresse (IPv4 / IPv6)", "Netzwerkadresse (nach DSGVO personenbezogenes Datum).", 2, "IP_ANONYMIZE_SUBNET", new[] { "^ip$", ".*_ip$", "^ip_address$", "^client_ip$" }),
        new("BIRTH_DATE", "Geburtsdatum", "Geburtsdatum einer natürlichen Person.", 3, "DATE_TRUNCATE_TO_YEAR", new[] { ".*birth.*", ".*dob.*", ".*geburtsdatum.*" }),
        new("FULL_NAME", "Vollständiger Name / Person", "Vorname, Nachname oder zusammengesetzter Personenname.", 2, "NAME_INITIALS_ONLY", new[] { "^name$", "^full_name$", "^nachname$", "^vorname$", ".*_name$" }),
        new("GEO_LOCATION", "Geokoordinaten", "GPS-Koordinaten (Latitude / Longitude).", 2, "GEO_DISTRICT_500M", new[] { "^lat$", "^lon$", ".*lat.*", ".*lon.*", ".*latitude.*", ".*longitude.*", ".*geo.*" }),
        new("HEALTH_DATA", "Gesundheitsdaten (Art. 9 DSGVO / HIPAA)", "Diagnosen, Laborwerte, ICD-10-Codes oder Patientenhistorie.", 4, "REDACT_COMPLETELY", new[] { ".*diagnos.*", ".*icd10.*", ".*health.*", ".*befund.*" })
    };

    public static List<NamedMaskingRule> GetDefaultMaskingRules() => new()
    {
        new("IBAN_STANDARD_4_4", "PARTIAL_MASK", KeepPrefix: 4, KeepSuffix: 4, MaskChar: '*'),
        new("CREDIT_CARD_LAST_4", "PARTIAL_MASK", KeepPrefix: 0, KeepSuffix: 4, MaskChar: '*'),
        new("EMAIL_DOMAIN_RETAIN", "MASK_EMAIL"),
        new("PHONE_RETAIN_COUNTRY_CODE", "MASK_PHONE"),
        new("IP_ANONYMIZE_SUBNET", "PARTIAL_MASK", KeepPrefix: 9, KeepSuffix: 0, MaskChar: '0'),
        new("DATE_TRUNCATE_TO_YEAR", "PARTIAL_MASK", KeepPrefix: 4, KeepSuffix: 0, MaskChar: '*'),
        new("NAME_INITIALS_ONLY", "PARTIAL_MASK", KeepPrefix: 1, KeepSuffix: 0, MaskChar: '.'),
        new("GEO_DISTRICT_500M", "GEO_JITTER", RadiusMeters: 500.0),
        new("REDACT_COMPLETELY", "REDACT", Replacement: "[REDACTED]")
    };
}

/// <summary>
/// Named masking rule template with configured strategy and parameters.
/// </summary>
public sealed record NamedMaskingRule(
    string Name,
    string RuleType,
    int? KeepPrefix = null,
    int? KeepSuffix = null,
    char? MaskChar = '*',
    double? RadiusMeters = null,
    string? PatternOrFormat = null,
    string? Replacement = null);

/// <summary>
/// Workflow options and configuration profiles.
/// </summary>
public sealed class WorkflowOptions
{
    public string ActiveProfile { get; init; } = "ENTERPRISE_HYBRID";

    public Dictionary<string, ClassificationWorkflowProfile> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTERPRISE_HYBRID"] = new(
            new OwnerResolutionConfig("MetadataFirstThenCatalogCascade"),
            new PreClassificationConfig("Disabled", ZeroTouchAutoApprovePublicAndLowRisk: false, AutoApproveConfidenceThreshold: 0.95, AutoApproveMaxSensitivityRank: 1),
            new ApprovalPipelineConfig(EnableFourEyes: true, Mode: "ConditionalDualStage", FourEyesThresholdRank: 3, RequireSecondStageOnDisputed: true, RequireStepUpAuthThresholdRank: 4, RequireFourEyesOnDowngrades: true)
        ),
        ["LEAN_DATA_MESH"] = new(
            new OwnerResolutionConfig("MetadataFirstThenCatalogCascade"),
            new PreClassificationConfig("Disabled", ZeroTouchAutoApprovePublicAndLowRisk: true, AutoApproveConfidenceThreshold: 0.95, AutoApproveMaxSensitivityRank: 2),
            new ApprovalPipelineConfig(EnableFourEyes: false, Mode: "SingleStageDataOwnerOnly", FourEyesThresholdRank: 999, RequireSecondStageOnDisputed: false, RequireStepUpAuthThresholdRank: 999, RequireFourEyesOnDowngrades: false)
        ),
        ["TRADITIONAL_NO_AI_ENTERPRISE"] = new(
            new OwnerResolutionConfig("MetadataFirstThenCatalogCascade"),
            new PreClassificationConfig("Disabled", ZeroTouchAutoApprovePublicAndLowRisk: false),
            new ApprovalPipelineConfig(EnableFourEyes: true, Mode: "ConditionalDualStage", FourEyesThresholdRank: 3, RequireSecondStageOnDisputed: true, RequireStepUpAuthThresholdRank: 4, RequireFourEyesOnDowngrades: true)
        )
    };

    public ClassificationWorkflowProfile GetActiveProfile() =>
        Profiles.TryGetValue(ActiveProfile, out var profile) ? profile : Profiles["ENTERPRISE_HYBRID"];
}
