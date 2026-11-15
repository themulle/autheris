namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed partial class InfrastructureAndPluginSecurityTests
{
    // =========================================================================
    // SEC-LOW-08: DuckDb disallowed functions & sanitized column collisions
    // =========================================================================

    [Theory]
    [InlineData("SELECT * FROM iceberg_scan('s3://bucket/table')")]
    [InlineData("SELECT * FROM delta_scan('s3://bucket/table')")]
    [InlineData("SELECT * FROM sqlite_scan('test.db', 'tbl')")]
    [InlineData("SELECT * FROM postgres_scan('host=localhost', 'public', 'tbl')")]
    [InlineData("SELECT * FROM mysql_scan('host=localhost', 'db', 'tbl')")]
    [InlineData("SELECT current_setting('threads')")]
    [InlineData("SELECT getenv('AWS_SECRET_ACCESS_KEY')")]
    [InlineData("SELECT * FROM pragma_table_info('tbl')")]
    public async Task SEC_LOW_08_DuckDb_DisallowedFunctions_AreRejected(string sql)
    {
        var engine = new DuckDbOlapEngine(new DuckDbOlapOptions { Enabled = true }, NullLogger<DuckDbOlapEngine>.Instance);
        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await engine.ExecuteOlapQueryAsync(new OlapQueryRequest(sql, []));
        });
        ex.Message.ShouldContain("prohibited", Case.Insensitive);
    }

    [Fact]
    public async Task SEC_LOW_08_DuckDb_SanitizedColumnCollision_ThrowsInvalidOperationException()
    {
        var engine = new DuckDbOlapEngine(new DuckDbOlapOptions { Enabled = true }, NullLogger<DuckDbOlapEngine>.Instance);
        var tableId = new TableIdentifier("sales", "crm", "contacts");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SchemaName = "crm", TableName = "contacts" },
            Columns =
            [
                new TableColumn { ColumnName = "user_name", DataType = "string" },
                new TableColumn { ColumnName = "user-name", DataType = "string" }
            ]
        };

        var source = new OlapTableSource(
            tableId,
            new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["user_name"] = "Alice", ["user-name"] = "Bob" }
            },
            Metadata: metadata
        );

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await engine.ExecuteOlapQueryAsync(new OlapQueryRequest("SELECT * FROM contacts", [source]));
        });
        ex.Message.ShouldContain("collision", Case.Insensitive);
    }
}
