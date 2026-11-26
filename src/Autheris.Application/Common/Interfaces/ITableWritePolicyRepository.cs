using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

/// <summary>Tenant-scoped store for explicit table-level write policies (issue #1154).</summary>
public interface ITableWritePolicyRepository
{
    Task<TableWritePolicy?> GetAsync(TenantId tenant, Guid tableId, CancellationToken ct = default);

    Task<IReadOnlyList<TableWritePolicy>> ListAsync(TenantId tenant, CancellationToken ct = default);

    Task<TableWritePolicy> UpsertAsync(TableWritePolicy policy, CancellationToken ct = default);

    Task<bool> DeleteAsync(TenantId tenant, Guid tableId, CancellationToken ct = default);
}

/// <summary>Default no-op repository when no governance database is wired.</summary>
public sealed class EmptyTableWritePolicyRepository : ITableWritePolicyRepository
{
    public static EmptyTableWritePolicyRepository Instance { get; } = new();

    public Task<TableWritePolicy?> GetAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
        Task.FromResult<TableWritePolicy?>(null);

    public Task<IReadOnlyList<TableWritePolicy>> ListAsync(TenantId tenant, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TableWritePolicy>>([]);

    public Task<TableWritePolicy> UpsertAsync(TableWritePolicy policy, CancellationToken ct = default) =>
        throw new NotSupportedException("Table write policies require a configured governance store.");

    public Task<bool> DeleteAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
        Task.FromResult(false);
}
