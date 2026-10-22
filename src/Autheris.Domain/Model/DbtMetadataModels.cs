namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed record DbtColumnDefinition(
    string Name,
    string? DataType,
    string? Description,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Meta
);

/// <summary>
/// Primary key and unique constraints a dbt model declares (model or column <c>constraints</c>, <c>unique</c> tests). The size caps are
/// applied while parsing; <see cref="Invalid"/> marks a declaration that must be dropped as a whole.
/// </summary>
public sealed record DbtDeclaredKeys(
    IReadOnlyList<string>? PrimaryKey,
    IReadOnlyList<IReadOnlyList<string>> UniqueConstraints,
    bool Invalid);

public sealed record DbtModelDefinition(
    string UniqueId,
    string Name,
    string Database,
    string Schema,
    string Materialization,
    string? Description,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyDictionary<string, DbtColumnDefinition> Columns,
    IReadOnlyList<string> DependsOnNodes,
    bool ContractEnforced = false
)
{
    /// <summary>Declared keys of the model; null when the manifest declares none.</summary>
    public DbtDeclaredKeys? DeclaredKeys { get; init; }

    /// <summary>Synthetic sample rows declared in meta (e.g. meta.synthetic_samples); null when none declared.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? SyntheticSamples { get; init; }

    public TableIdentifier ToTableIdentifier() => new(Database, Schema, Name);
}

public enum DbtProposalStatus
{
    PendingReview = 0,
    Approved = 1,
    Rejected = 2
}

public sealed record DbtMetadataProposal(
    Guid Id,
    TableIdentifier Table,
    string ColumnName,
    string SuggestedRuleType, // e.g. MASK_EMAIL, REDACT, HMAC_SHA256
    string? SuggestedSensitivity, // HIGH, MEDIUM, LOW
    string? SuggestedOwnerTeam,
    string SourceDbtTag,
    DbtProposalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt = null,
    string? ReviewedBy = null
);

public sealed record DbtRelationshipDefinition(
    string Name,
    string ParentModelOrTable,
    string ChildModelOrTable,
    IReadOnlyList<string> ParentColumns,
    IReadOnlyList<string> ChildColumns
);

/// <summary>
/// Result of parsing a dbt manifest: models, relation definitions (all ordered key pairs, issue #151) and parser warnings
/// (reason-coded, free of manifest free text).
/// </summary>
public sealed record DbtManifestParseResult(
    IReadOnlyList<DbtModelDefinition> Models,
    IReadOnlyList<DbtRelationshipDefinition> Relationships,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? TestToModels = null
)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TestToModels { get; init; } =
        TestToModels ?? new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record DbtSuppressedRelaxation(
    TableIdentifier Table,
    string Field,
    string Reason
);

public sealed record DbtSyncResult(
    bool Success,
    int ParsedModelsCount,
    int GeneratedProposalsCount,
    int UpdatedLineageNodesCount,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage = null,
    int ImportedRelationsCount = 0,
    IReadOnlyList<string>? RejectedModels = null,
    IReadOnlyList<DbtSuppressedRelaxation>? SuppressedRelaxations = null
)
{
    public IReadOnlyList<string> RejectedModels { get; init; } = RejectedModels ?? Array.Empty<string>();
    public IReadOnlyList<DbtSuppressedRelaxation> SuppressedRelaxations { get; init; } = SuppressedRelaxations ?? Array.Empty<DbtSuppressedRelaxation>();
}

public sealed record DbtContractBreakingChange(
    TableIdentifier Table,
    string ColumnName,
    string ChangeType, // "DROPPED_COLUMN", "DATA_TYPE_MISMATCH"
    string? ExistingType,
    string? ProposedType,
    string Description
);

public sealed record DbtContractValidationResult(
    bool IsCompatible,
    int ValidatedModelsCount,
    IReadOnlyList<DbtContractBreakingChange> BreakingChanges,
    IReadOnlyList<string> Warnings
);

public sealed record DbtGovernanceSyncResult(
    bool Success,
    int UpdatedTablesCount,
    int UpdatedColumnsCount,
    int MaskingRulesCount,
    int VirtualFiltersCount,
    int AccessProfilesCount,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage = null,
    int RemovedMaskingRulesCount = 0,
    int RelaxedMaskingRulesCount = 0
);

