namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

public sealed class GovernanceSchemaValidator
{
    public static readonly IReadOnlyList<string> RequiredWave2Tables = new[]
    {
        "IDENTITY_USERS",
        "IDENTITY_GROUPS",
        "IDENTITY_GROUP_MEMBERS",
        "IDENTITY_LOCKS",
        "FUNCTIONAL_RIGHT_MAPPINGS",
        "ACCESS_POLICIES",
        "ACCESS_POLICY_GRANTEES",
        "ACCESS_POLICY_SELECTORS",
        "RECERT_CAMPAIGNS",
        "RECERT_ITEMS"
    };

    public static async Task ValidateWave2TablesAsync(DbConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try SQLite first
        if (connection.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existingTables.Add(reader.GetString(0));
            }
        }
        else
        {
            // PostgreSQL / SQL Server standard information_schema
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT table_name FROM information_schema.tables";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existingTables.Add(reader.GetString(0));
            }
        }

        var missing = new List<string>();
        foreach (var req in RequiredWave2Tables)
        {
            if (!existingTables.Contains(req))
            {
                missing.Add(req);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Governance schema validation failed (fail closed): Missing required Wave 2 tables: {string.Join(", ", missing)}");
        }
    }
}
