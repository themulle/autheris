namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DeltaLakeGateOrderTests
{
    [Fact]
    public async Task NonDemo_ThrowsNotImplemented_WithoutReadingDeltaLog()
    {
        var reader = Substitute.For<IDeltaMetadataReader>();
        var executor = new DeltaLakeDataSourceExecutor(
            reader,
            new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance),
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(new GatewayOptions()),
            NullLogger<DeltaLakeDataSourceExecutor>.Instance,
            new DemoDataSwitch(false));

        var tableId = new TableIdentifier("lake", "default", "delta_table");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "default", TableName = "delta_table", DataSourceType = DataSourceType.LakehouseDelta },
            Columns = [new TableColumn { ColumnName = "id", DataType = "string" }, new TableColumn { ColumnName = "tenant_id", DataType = "string" }]
        };
        var context = new DataSourceExecutionContext(
            "lake", metadata, new ClaimsPrincipal(new ClaimsIdentity()),
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["tenant_id"] = ColumnAccessLevel.Clear
            }),
            new Dictionary<string, object?>(), new List<string> { "id" }, null, 1000, 0, new TenantId("tenant-1"));

        await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayNotImplementedException>(() => executor.ExecuteAsync(context));

        await reader.DidNotReceiveWithAnyArgs().LoadSnapshotAsync(default!, default, default, default);
    }
}
