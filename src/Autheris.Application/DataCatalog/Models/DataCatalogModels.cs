namespace Autheris.Application.DataCatalog.Models;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed record CatalogColumnAsset
{
    public required string ColumnName { get; init; }
    public string DataType { get; init; } = "varchar";
    public string? Description { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Classifications { get; init; } = [];
    public bool IsNullable { get; init; } = true;
    public bool IsPrimaryKey { get; init; }
}

public sealed record CatalogTableAsset
{
    public required TableIdentifier Identifier { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string SourceType { get; init; } = "PostgreSQL";
    public string? ExternalAssetId { get; init; }
    public string? OwnerRef { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Classifications { get; init; } = [];
    public List<CatalogColumnAsset> Columns { get; init; } = [];

    /// <summary>Declared primary key (ordered). When empty, the columns flagged <see cref="CatalogColumnAsset.IsPrimaryKey"/> (in column order) are used.</summary>
    public List<string> PrimaryKeyColumns { get; init; } = [];

    /// <summary>Issue #390: direction per <see cref="PrimaryKeyColumns"/> entry; empty = all ascending. Only meaningful with declared <see cref="PrimaryKeyColumns"/>.</summary>
    public List<KeyColumnDirection> PrimaryKeyDirections { get; init; } = [];

    /// <summary>Declared indexes (unique and non-unique). Replaces the stored definition only together with a declared key or when non-empty.</summary>
    public List<TableIndexDefinition> Indexes { get; init; } = [];

    public Dictionary<string, string> CustomProperties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record CatalogSyncResult(
    int SyncedTablesCount,
    int SyncedColumnsCount,
    int MaskedColumnsCount,
    int Art9ProtectedTablesCount,
    IReadOnlyList<TableIdentifier> AffectedTables,
    IReadOnlyList<string> Warnings,
    bool Success);
