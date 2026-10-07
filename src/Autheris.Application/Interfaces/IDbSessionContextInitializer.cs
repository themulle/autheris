namespace Autheris.Application.Interfaces;

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

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
        string? provider,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        bool requireTransaction = true,
        CancellationToken ct = default);

    Task InitializeSessionAsync(
        DbConnection connection,
        DbTransaction? tx,
        DatabaseDialect dialect,
        TenantId tenantId,
        string? userSid = null,
        string? purpose = null,
        CancellationToken ct = default);
}
