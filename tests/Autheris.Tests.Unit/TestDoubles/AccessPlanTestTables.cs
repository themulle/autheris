namespace Autheris.Tests.Unit.TestDoubles;

using System;
using System.Threading;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Autheris.Tests.Common.Fakes;

/// <summary>A table repository that knows <c>sales.public.orders</c>: applying an access plan writes grant records, which need a registered table.</summary>
internal static class AccessPlanTestTables
{
    public static ITableMetadataRepository OrdersRepository()
    {
        var id = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { Id = id, SchemaName = "public", TableName = "orders", Sensitivity = "INTERNAL", IsActive = true },
            Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        var repo = new InMemoryTableMetadataRepository();
        repo.Add(meta);
        return repo;
    }
}
