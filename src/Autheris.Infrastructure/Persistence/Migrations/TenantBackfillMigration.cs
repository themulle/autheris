namespace Autheris.Infrastructure.Persistence.Migrations;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public static class TenantBackfillMigration
{
    public static async Task<int> RunBackfillAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int updated = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                UPDATE CONSENT_REQUESTS 
                SET tenant_id = @defaultTenant 
                WHERE tenant_id IS NULL OR TRIM(tenant_id) = '' OR tenant_id = 'default' OR tenant_id = 'legacy-default';
            ";
            cmd.Parameters.AddWithValue("@defaultTenant", TenantId.LegacySingleTenant.Value);
            updated += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (TableExists(connection, "CONSENTS"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE CONSENTS
                SET tenant_id = @defaultTenant
                WHERE tenant_id IS NULL OR TRIM(tenant_id) = '' OR tenant_id = 'default' OR tenant_id = 'legacy-default';
            ";
            cmd.Parameters.AddWithValue("@defaultTenant", TenantId.LegacySingleTenant.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return updated;
    }

    public static async Task VerifyTenantBackfillAsync(SqliteConnection connection, bool isMultiTenantEnabled, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!isMultiTenantEnabled) return;

        long unmigratedCount = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM CONSENT_REQUESTS WHERE tenant_id IS NULL OR TRIM(tenant_id) = '' OR tenant_id = 'default' OR tenant_id = 'legacy-default';";
            var countObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            unmigratedCount += Convert.ToInt64(countObj);
        }

        if (TableExists(connection, "CONSENTS"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM CONSENTS WHERE tenant_id IS NULL OR TRIM(tenant_id) = '' OR tenant_id = 'default' OR tenant_id = 'legacy-default';";
            var countObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            unmigratedCount += Convert.ToInt64(countObj);
        }

        if (unmigratedCount > 0)
        {
            throw new InvalidOperationException(
                $"FATAL MIGRATION ERROR: Es wurden {unmigratedCount} unmigrierte Datensätze ohne gültige TenantId in CONSENT_REQUESTS / CONSENTS gefunden. " +
                $"Multi-Tenancy Start verweigert bis TenantBackfillMigration ausgeführt wurde.");
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@tableName;";
        cmd.Parameters.AddWithValue("@tableName", tableName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
