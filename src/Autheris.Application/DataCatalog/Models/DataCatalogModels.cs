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
