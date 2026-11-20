namespace Autheris.Modules.Dbt.Tests;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

internal static class SemanticManifestFixtures
{
    internal static TableMetadata Orders()
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("analytics", "public", "orders"),
            Table = new Table { Id = id, SourceType = "PostgreSQL", SourceName = "analytics", SchemaName = "public", TableName = "orders", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns =
            [
                new TableColumn { TableId = id, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = id, ColumnName = "status", DataType = "varchar" },
                new TableColumn { TableId = id, ColumnName = "amount", DataType = "decimal" }
            ]
        };
    }
}
