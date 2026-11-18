namespace Autheris.Application.Mcp.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>Origin of a stored golden query; persisted as <c>DBT</c> or <c>CONFIG</c>.</summary>
public enum GoldenQuerySource
{
    Dbt,
    Config
}

/// <summary>One entry handed to <see cref="IGoldenQueryRepository.ReplaceForTableAsync"/>; text is already validated and sanitised.</summary>
public sealed record GoldenQueryStoreEntry(
    string SourceKey,
    string Title,
    string? Description,
    string QueryText,
    string? VariablesJson)
{
    /// <summary>Lower-case hex SHA-256 over title, description, query text and variables; stored as <c>content_hash</c> and used to skip unchanged syncs.</summary>
    public string ComputeContentHash() =>
        System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{Title}\u001f{Description}\u001f{QueryText}\u001f{VariablesJson}")));
}

/// <summary>A stored golden query joined to the owning active dataset.</summary>
public sealed record GoldenQueryStoreRecord(
    string Id,
    string TableId,
    string Domain,
    string Schema,
    string TableName,
    GoldenQuerySource Source,
    string SourceKey,
    string Title,
    string? Description,
    string QueryText,
    string? VariablesJson,
    string ContentHash,
    DateTimeOffset UpdatedAt);

/// <summary>Tenant-scoped persistence of golden queries; every call binds the tenant and rejects a default tenant.</summary>
public interface IGoldenQueryRepository
{
    /// <summary>
    /// Replaces the whole entry set of one table and source in one transaction (an empty list deletes the set) and bumps the catalog
    /// content version so cached readers refresh. Other tenants, tables and sources are never touched.
    /// </summary>
    Task ReplaceForTableAsync(
        TenantId tenant,
        string tableId,
        GoldenQuerySource source,
        IReadOnlyList<GoldenQueryStoreEntry> entries,
        CancellationToken ct = default);

    /// <summary>All entries of the tenant that belong to active datasets.</summary>
    Task<IReadOnlyList<GoldenQueryStoreRecord>> GetForTenantAsync(TenantId tenant, CancellationToken ct = default);
}
