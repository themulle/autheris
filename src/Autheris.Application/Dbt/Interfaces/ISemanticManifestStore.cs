namespace Autheris.Application.Dbt.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// One persisted, validated semantic manifest of a tenant (#112 A-9). <see cref="Json"/> is the manifest in the
/// <c>SemanticManifestJson</c> shape; <see cref="ContentHash"/> is the SHA-256 hex of <see cref="Json"/>.
/// </summary>
public sealed record StoredSemanticManifest(
    TenantId TenantId,
    string Version,
    string ContentHash,
    int FormatVersion,
    int ModelsCount,
    int MetricsCount,
    string Json,
    string ImportedBy,
    DateTimeOffset ImportedAt);

/// <summary>
/// Durable per-tenant storage of the latest semantic manifest (#112 A-9). The store never grants access; it only holds data. A save
/// replaces the previous manifest of the tenant atomically.
/// </summary>
public interface ISemanticManifestStore
{
    /// <summary>The stored manifest of the tenant, or <c>null</c> when none was imported.</summary>
    Task<StoredSemanticManifest?> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the manifest of <paramref name="tenantId"/> (single statement upsert). <paramref name="manifest"/> must carry the same tenant.</summary>
    Task SaveAsync(TenantId tenantId, StoredSemanticManifest manifest, CancellationToken cancellationToken = default);
}
