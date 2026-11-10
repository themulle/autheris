namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-SIMPL-143: Reserved source names cannot be ingested as governed tables on any path (dbt, OpenMetadata, OpenAPI, core API).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogSourceNameValidatorTests
{
    [Theory]
    [InlineData("sales", "sys", "orders")]
    [InlineData("sales", "information_schema", "orders")]
    [InlineData("sales", "pg_catalog", "orders")]
    [InlineData("sales", "pg_toast", "orders")]
    [InlineData("system", "core", "orders")]
    [InlineData("sales", "SYS", "orders")]
    [InlineData("sales", "SYSTEM", "orders")]
    [InlineData("sales", "XDB", "orders")]
    [InlineData("sales", "CTXSYS", "orders")]
    [InlineData("sales", "MDSYS", "orders")]
    [InlineData("sales", "dbo", "pg_class")]
    [InlineData("sales", "dbo", "pg_attribute")]
    [InlineData("sales", "dbo", "ALL_TABLES")]
    [InlineData("sales", "dbo", "USER_TAB_COLUMNS")]
    [InlineData("sales", "dbo", "DBA_OBJECTS")]
    [InlineData("sales", "dbo", "CDB_VIEWS")]
    [InlineData("sales", "dbo", "V$SESSION")]
    [InlineData("sales", "dbo", "GV$INSTANCE")]
    [InlineData("sales", "dbo", "dbms_metadata")]
    public void DbtOpenMetadataOpenApiUpserts_ReservedNames_Rejected(string domain, string schema, string table)
    {
        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table { SourceName = domain, SchemaName = schema, TableName = table, SourceType = "PostgreSql" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var ex = Should.Throw<CatalogReservedSourceNameException>(() => CatalogSourceNameValidator.Validate(metadata));
        ex.Message.ShouldContain("reserved");
    }

    [Theory]
    [InlineData("sales", "public", "orders")]
    [InlineData("sales", "core", "USER_PROFILES")]
    [InlineData("sales", "app", "orders_stats")]
    [InlineData("analytics", "reporting", "customer_orders")]
    public void NonReservedNames_Accepted(string domain, string schema, string table)
    {
        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table { SourceName = domain, SchemaName = schema, TableName = table, SourceType = "SqlServer" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        Should.NotThrow(() => CatalogSourceNameValidator.Validate(metadata));
    }
}
