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
    string ParentColumn,
    string ChildColumn
);

public sealed record DbtSyncResult(
    bool Success,
    int ParsedModelsCount,
    int GeneratedProposalsCount,
    int UpdatedLineageNodesCount,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage = null,
    int ImportedRelationsCount = 0
);

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
    string? ErrorMessage = null
);

