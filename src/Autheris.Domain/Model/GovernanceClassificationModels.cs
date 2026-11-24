namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>
/// Strongly-typed numerical hierarchy rank for sensitivity classifications (e.g. 1 to 4).
/// </summary>
public readonly record struct SensitivityRank(int Value) : IComparable<SensitivityRank>
{
    public int CompareTo(SensitivityRank other) => Value.CompareTo(other.Value);

    public static bool operator <(SensitivityRank left, SensitivityRank right) => left.Value < right.Value;
    public static bool operator <=(SensitivityRank left, SensitivityRank right) => left.Value <= right.Value;
    public static bool operator >(SensitivityRank left, SensitivityRank right) => left.Value > right.Value;
    public static bool operator >=(SensitivityRank left, SensitivityRank right) => left.Value >= right.Value;

    public static implicit operator int(SensitivityRank rank) => rank.Value;
    public static implicit operator SensitivityRank(int value) => new(value);

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Declarative sensitivity classification label definition (e.g. L1_PUBLIC, TISAX_HIGH, VS_VERTRAULICH).
/// </summary>
public sealed record SensitivityLevelDefinition(
    string Key,
    string DisplayName,
    string Description,
    int Rank,
    bool RequiresStepUpAuth = false,
    int MaxConsentTtlDays = 90,
    ProtectionClass ProtectionClass = ProtectionClass.Confidential);

/// <summary>
/// Declarative PII or semantic data type category with default masking mapping and regex patterns.
/// </summary>
public sealed record PiiCategoryDefinition(
    string Key,
    string DisplayName,
    string Description,
    int DefaultSensitivityRank,
    string DefaultMaskingRule,
    IReadOnlyList<string> NamePatterns,
    string? MaskCategory = null);

/// <summary>
/// Ingestion and owner resolution strategy.
/// </summary>
public sealed record OwnerResolutionConfig(
    string Strategy = "MetadataFirstThenCatalogCascade",
    IReadOnlyList<string>? ExtractFromMetadataTags = null,
    IReadOnlyDictionary<string, string>? RuleBasedMappings = null,
    string FallbackBehavior = "AssignToDataGovernanceExpert");

/// <summary>
/// Pre-classification configuration (deterministic vs. optional AI).
/// </summary>
public sealed record PreClassificationConfig(
    string Mode = "Disabled", // "Disabled" | "HumanInTheLoop" | "AutoApproveUnambiguous"
    bool ZeroTouchAutoApprovePublicAndLowRisk = false,
    double AutoApproveConfidenceThreshold = 0.95,
    int AutoApproveMaxSensitivityRank = 1,
    bool RequireManualReviewIfDisputed = true);

/// <summary>
/// Approval pipeline configuration: one decision by a Data Governance or owner member settles a proposal (single approval); a step-up
/// MFA token is required from the given sensitivity rank on.
/// </summary>
public sealed record ApprovalPipelineConfig(
    int RequireStepUpAuthThresholdRank = 4);

/// <summary>
/// Workflow profile encapsulating resolution, classification and approval policy.
/// </summary>
public sealed record ClassificationWorkflowProfile(
    OwnerResolutionConfig OwnerResolution,
    PreClassificationConfig PreClassification,
    ApprovalPipelineConfig ApprovalPipeline);

/// <summary>
/// Column proposal outcome produced by the classification engine.
/// </summary>
public sealed record ColumnClassificationProposal(
    string ColumnName,
    string DataType,
    string ProposedSensitivityKey,
    int ProposedSensitivityRank,
    string? DetectedPiiCategoryKey,
    string ProposedMaskingRule,
    double Confidence,
    bool IsDisputed,
    string? Reasoning = null,
    ProtectionClass ProposedProtectionClass = ProtectionClass.Confidential,
    string? ProposedMaskCategory = null);

/// <summary>
/// Table-level classification proposal package.
/// </summary>
public sealed record TableClassificationProposal(
    Guid ProposalId,
    TableIdentifier TableIdentifier,
    string? DataOwnerSid,
    string Status, // "UNCLASSIFIED", "PENDING_OWNER_REVIEW", "CLASSIFIED", "REJECTED" (legacy "PENDING_GOVERNANCE_REVIEW" is still open)
    IReadOnlyList<ColumnClassificationProposal> Columns,
    DateTimeOffset CreatedAtUtc,
    string? ProposedBySid = null,
    string? ApprovedBySid = null);

/// <summary>
/// Column-level human approval or override decision.
/// </summary>
public sealed record ColumnClassificationDecision(
    string ColumnName,
    string Action, // "ACCEPT_PROPOSAL", "OVERRIDE"
    string EffectiveSensitivityKey,
    int EffectiveSensitivityRank,
    string EffectiveMaskingRule,
    string? Comment = null,
    ProtectionClass? EffectiveProtectionClass = null,
    string? EffectiveMaskCategory = null);

/// <summary>
/// Human sign-off / transition request submitted by a Data Governance or owner member (single approval).
/// </summary>
public sealed record TableClassificationApprovalRequest(
    Guid ProposalId,
    TableIdentifier TableIdentifier,
    Sid ReviewerSid,
    string ReviewerRole, // "DATA_OWNER" | "DATA_GOVERNANCE_REVIEWER"
    string Action, // "APPROVE", "REJECT"
    IReadOnlyList<ColumnClassificationDecision> ColumnDecisions,
    string? OverallComment = null,
    string? Justification = null,
    string? StepUpAuthToken = null,
    TenantId? TenantId = null,
    ClaimsPrincipal? Caller = null,
    ProtectionClass? ExistingTableClass = null);

/// <summary>
/// Result of an approval pipeline transition.
/// </summary>
public sealed record TableClassificationApprovalResult(
    bool Success,
    string NewStatus,
    string Message,
    bool RequiresStepUpAuth = false,
    string? ErrorCode = null);

/// <summary>
/// WORM-sealed audit record minted when governance configuration changes at startup or reload.
/// </summary>
public sealed record WormConfigurationRecord(
    Guid ConfigAuditId,
    long PolicyEpoch,
    string Trigger, // "STARTUP_BOOTSTRAP", "CONFIGMAP_RELOAD", "ADMIN_API_UPDATE"
    string ActorSid,
    string Sha256ConfigHash,
    string PreviousConfigHash,
    IReadOnlyList<string> ModifiedSections,
    string CanonicalConfigJson,
    string WormSignature,
    DateTimeOffset TimestampUtc);
