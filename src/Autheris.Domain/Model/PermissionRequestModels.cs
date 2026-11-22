namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Tenancy;

/// <summary>Target of a permission request across data, unmask, catalog, or profile grants.</summary>
public sealed record PermissionTarget(
    TableIdentifier? Table = null,
    IReadOnlyList<string>? Columns = null,
    ContainerKind? ContainerKind = null,
    Guid? ContainerId = null,
    Guid? ProfileId = null)
{
    public bool HasTable => Table is { } t && !string.IsNullOrWhiteSpace(t.TableName);

    public bool HasColumns => Columns is { Count: > 0 };

    public bool HasContainer => ContainerKind.HasValue && ContainerId.HasValue && ContainerId.Value != Guid.Empty;

    public bool HasProfile => ProfileId.HasValue && ProfileId.Value != Guid.Empty;

    public static PermissionTarget ForTable(TableIdentifier table, IReadOnlyList<string>? columns = null) =>
        new(Table: table, Columns: columns);

    public static PermissionTarget ForContainer(ContainerKind containerKind, Guid containerId) =>
        new(ContainerKind: containerKind, ContainerId: containerId);

    public static PermissionTarget ForProfile(Guid profileId) =>
        new(ProfileId: profileId);
}

/// <summary>Container-scoped catalog visibility grant persisted in <c>catalog_visibility_grants</c>.</summary>
public sealed class CatalogVisibilityGrant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TenantId TenantId { get; init; }
    public ContainerKind ContainerKind { get; init; }
    public Guid ContainerId { get; init; }
    public GranteeType GranteeType { get; init; } = GranteeType.User;
    public Sid GranteeSid { get; init; }
    public DateTimeOffset ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public Sid GrantedBySid { get; init; }
    public ApproverCapacity ApproverCapacity { get; init; }
    public Guid? RequestId { get; init; }
    public bool IsRevoked { get; init; }
    public Sid? RevokedBySid { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokeReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive(DateTimeOffset atTime) =>
        !IsRevoked && ValidFrom <= atTime && (!ValidTo.HasValue || atTime < ValidTo.Value);
}

/// <summary>Per-table row recorded prior to a D13 start-fresh purge step.</summary>
public sealed record MigrationPurgeReportEntry(
    string Id,
    string Step,
    string TableName,
    long RowCount,
    string? LedgerHeadHash,
    DateTimeOffset PurgedAt,
    DateTimeOffset? AuditedAt);

/// <summary>Archived tenant ledger head captured before a D13 purge.</summary>
public sealed record GrantRecordHeadArchiveEntry(
    string Step,
    string TenantId,
    long LastSeq,
    string LastHash,
    DateTimeOffset ArchivedAt);

/// <summary>Audit actor subject kind recorded in structured permission audit details.</summary>
public enum PermissionAuditActorKind
{
    User = 0,
    ServicePrincipal = 1
}
