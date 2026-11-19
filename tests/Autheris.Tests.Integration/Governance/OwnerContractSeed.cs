using System;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Seeding and counting helpers shared by the data owner contract suites (raw rows keep the setup independent of the code under test).</summary>
internal static class OwnerContractSeed
{
    public static async Task<TableIdentifier> SeedTableAsync(this GovernanceTestStore store)
    {
        var id = new TableIdentifier("Sales", "Crm", "Orders_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = id.Domain,
            SchemaName = id.Schema,
            TableName = id.TableName,
            Sensitivity = "LOW",
            RequiresFourEyes = false,
            IsActive = true
        };
        await store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        return id;
    }

    public static Task SeedOwnerAsync(this GovernanceTestStore store, string id, string sid, string account, string name, string email, int active = 1) =>
        store.ExecuteNonQueryAsync(
            "INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES (@id, @sid, @acc, @n, @e, @a)",
            ("@id", id), ("@sid", sid), ("@acc", account), ("@n", name), ("@e", email), ("@a", active));

    public static async Task SeedManualAssignmentAsync(this GovernanceTestStore store, TableIdentifier table, string ownerId, string role = "PRIMARY") =>
        await store.ExecuteNonQueryAsync(
            "INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role) VALUES (@id, @t, @o, @r)",
            ("@id", Guid.NewGuid().ToString()), ("@t", await store.TableIdAsync(table)), ("@o", ownerId), ("@r", role));

    public static async Task<string> TableIdAsync(this GovernanceTestStore store, TableIdentifier table) =>
        Convert.ToString(await store.ExecuteScalarAsync(
            "SELECT id FROM TABLES WHERE source_name = @d AND schema_name = @s AND table_name = @t",
            ("@d", table.Domain), ("@s", table.Schema), ("@t", table.TableName)))!;

    public static async Task<int> OwnerRowsAsync(this GovernanceTestStore store, TableIdentifier table) =>
        Convert.ToInt32(await store.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM TABLE_OWNERS WHERE table_id = @t", ("@t", await store.TableIdAsync(table))));

    public static async Task<int> TotalOwnerRowsAsync(this GovernanceTestStore store) =>
        Convert.ToInt32(await store.ExecuteScalarAsync("SELECT COUNT(*) FROM DATA_OWNERS"));
}
