namespace Autheris.Application.Common.Interfaces;

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>Requested database access mode for session initialization (SEC-OI-505-5).</summary>
public enum AccessMode
{
    /// <summary>Default caller-managed / read-write transaction mode.</summary>
    Default = 0,

    /// <summary>
    /// Explicitly opens a database read-only transaction (<c>SET TRANSACTION READ ONLY</c> on PostgreSQL and Oracle)
    /// for user read queries. Never passed on DML write paths.
    /// </summary>
    ReadOnly = 1
}

/// <summary>
/// M-1: Centralizes database session and transaction initialization across all database access paths
/// (GraphQL, OData, WebSQL, Procedures). Ensures consistent PostgreSQL session GUCs (autheris.tenant_id,
/// app.tenant_id, user_sid, purpose, TimeZone) and SQL Server SESSION_CONTEXT.
/// </summary>
public interface IDbSessionContextInitializer
{
    Task<DbTransaction?> InitializeSessionAsync(
        DbConnection connection,
        DatabaseDialect dialect,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        bool requireTransaction = true,
        CancellationToken ct = default);

    Task<DbTransaction?> InitializeSessionAsync(
        DbConnection connection,
        DatabaseDialect dialect,
        TenantId tenantId,
        AccessMode accessMode,
        string? userSid = null,
        string? purpose = null,
        bool requireTransaction = true,
        CancellationToken ct = default) =>
        InitializeSessionAsync(connection, dialect, tenantId, userSid, purpose, requireTransaction, ct);

    Task<DbTransaction?> InitializeSessionAsync(
        DbConnection connection,
        string? provider,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        bool requireTransaction = true,
        CancellationToken ct = default);

    Task<DbTransaction?> InitializeSessionAsync(
        DbConnection connection,
        string? provider,
        TenantId tenantId,
        AccessMode accessMode,
        string? userSid = null,
        string? purpose = null,
        bool requireTransaction = true,
        CancellationToken ct = default) =>
        InitializeSessionAsync(connection, provider, tenantId, userSid, purpose, requireTransaction, ct);

    Task InitializeSessionAsync(
        DbConnection connection,
        DbTransaction? tx,
        DatabaseDialect dialect,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        CancellationToken ct = default);
}
