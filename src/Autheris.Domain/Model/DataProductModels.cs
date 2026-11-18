namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>What a data product is: a view over catalog ids or a source procedure.</summary>
public enum DataProductKind { View, Procedure }

/// <summary>When the stage-1 result of a view is loaded. <c>OnDemand</c> carries the ttl in minutes, <c>Live</c> is ttl 0.</summary>
public enum LoadMode { Schedule, OnDemand, Live }

public enum DataProductStatus { Active, Stale, Invalid, Retired }

/// <summary>The creation authority a product was created under (decides who may change its definition).</summary>
public enum ProductCreatorAuthority { BaseOwner, DataSourceOwner, DataProductAdmin, GitSync }

/// <summary>Classification of a product column: masked by default, clear only with a clear rule on the caller's product grant.</summary>
public enum ProductColumnClass { PiiMasked, PiiClearEligible, NonPii }

/// <summary>One catalog column a product column is derived from.</summary>
public sealed record ColumnLineage(TableIdentifier Table, string Column);

public sealed record DataProductColumn(
    string Name,
    string DataType,
    ProductColumnClass Class,
    IReadOnlyList<ColumnLineage> Lineage,
    string? DeclassifiedBy,
    string? DeclassificationReason);

/// <summary>
/// Load settings of a view. <see cref="Interval"/> is the fixed refresh interval of <c>Schedule</c> (at least one minute; cron expressions are
/// not supported). <see cref="MaxStalenessMinutes"/> is the optional maximum age of the last successful load.
/// </summary>
public sealed record LoadSettings(
    LoadMode Mode,
    int TtlMinutes = 0,
    TimeSpan? Interval = null,
    int IdleDropMinutes = 0,
    int? MaxStalenessMinutes = null)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(7);

    public static LoadSettings Default { get; } = new(LoadMode.Live);
}

public sealed record ProductLimits(long MaxRows = 0, long MaxBytes = 0, int MaxRuntimeSeconds = 0)
{
    public static ProductLimits Default { get; } = new();
}

/// <summary>
/// A data product: an object defined in Autheris, never in the source database. <see cref="ProductOwner"/> decides the grants on it;
/// the base owners are only notified. <see cref="Version"/> counts definition revisions (1 on creation).
/// </summary>
public sealed record DataProduct(
    Guid Id,
    TenantId Tenant,
    string Schema,
    string Name,
    DataProductKind Kind,
    string Definition,
    string DefinitionHash,
    IReadOnlyList<TableIdentifier> BaseObjects,
    IReadOnlyList<DataProductColumn> Columns,
    Sid CreatedBy,
    ProductCreatorAuthority Authority,
    DataProductStatus Status,
    string? ManagedBy,
    LoadSettings Load,
    ProductLimits Limits,
    string? Description,
    Sid ProductOwner,
    int Version = 1,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset UpdatedAt = default);

/// <summary>One immutable definition revision of a product.</summary>
public sealed record DataProductVersion(
    Guid ProductId,
    int Version,
    string DefinitionHash,
    string Definition,
    Sid Author,
    ProductCreatorAuthority Authority,
    DateTimeOffset CreatedAt);

/// <summary>The owner of a whole data source within a tenant (creation authority over every object of that source).</summary>
public sealed record DataSourceOwner(TenantId Tenant, string DataSource, Sid OwnerSid, Sid AssignedBy, DateTimeOffset AssignedAt);
