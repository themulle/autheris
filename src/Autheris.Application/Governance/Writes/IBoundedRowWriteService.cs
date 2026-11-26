namespace Autheris.Application.Governance.Writes;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public enum BoundedRowWriteOperation
{
    Insert = 1,
    Upsert = 2,
    UpdateByPk = 3,
    DeleteByPk = 4
}

public sealed record BoundedRowWriteResult(
    string DatasetId,
    string Operation,
    int AffectedRows,
    bool Replayed,
    string IdempotencyKey);

public sealed class BoundedRowWriteException : Exception
{
    public string Code { get; }

    public BoundedRowWriteException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
    }
}

/// <summary>
/// Typed single-table row write service (insert, upsert, updateByPk, deleteByPk) for web applications over governed datasets
/// (including Databricks Delta, PostgreSQL, and SQL Server). Enforces <see cref="Autheris.Domain.Model.TableWritePolicy"/>,
/// reuses the table's existing SELECT row-level security predicates, enforces dedicated write connections, server-stamped columns,
/// idempotency keys, write budgets, and fail-closed audit logging.
/// </summary>
public interface IBoundedRowWriteService
{
    Task<BoundedRowWriteResult> InsertRowAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> row,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<BoundedRowWriteResult> UpsertRowAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> row,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<BoundedRowWriteResult> UpdateRowByPkAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> patch,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<BoundedRowWriteResult> DeleteRowByPkAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> key,
        string idempotencyKey,
        CancellationToken ct = default);
}
