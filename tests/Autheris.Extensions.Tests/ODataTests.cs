namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.OData;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ODataTests
{
    private static TableMetadata CreateSampleTable()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "invoices"),
            Table = new Table { SchemaName = "dbo", TableName = "invoices" },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "customer", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "invoice_date", DataType = "date" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamp" }
            ]
        };
    }

    [Fact]
    public void ODataCsdlGenerator_GeneratesValidEdmxXmlWithCorrectTypeMappings()
    {
        var tables = new List<TableMetadata> { CreateSampleTable() };
        var xml = ODataCsdlGenerator.GenerateMetadataXml(tables);

        xml.ShouldContain("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.ShouldContain("<edmx:Edmx Version=\"4.0\"");
        xml.ShouldContain("<EntityType Name=\"sales_dbo_invoices\">");
        xml.ShouldContain("<PropertyRef Name=\"id\" />");
        xml.ShouldContain("<Property Name=\"id\" Type=\"Edm.Int32\" Nullable=\"false\" />");
        xml.ShouldContain("<Property Name=\"customer\" Type=\"Edm.String\" />");
        xml.ShouldContain("<Property Name=\"amount\" Type=\"Edm.Decimal\" />");
        xml.ShouldContain("<Property Name=\"invoice_date\" Type=\"Edm.Date\" />");
        xml.ShouldContain("<Property Name=\"created_at\" Type=\"Edm.DateTimeOffset\" />");
        xml.ShouldContain("<EntitySet Name=\"sales_dbo_invoices\" EntityType=\"Autheris.OData.sales_dbo_invoices\" />");
    }

    [Fact]
    public void ODataResponseFormatter_FormatsServiceDocumentCorrectly()
    {
        var tables = new List<TableMetadata> { CreateSampleTable() };
        var doc = ODataResponseFormatter.FormatServiceDocument("https://gateway.corp.local/odata/v4", tables);

        doc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_WhenAllowed_Returns200WithRows()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var logger = NullLogger<ODataHandler>.Instance;

        var sampleRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["customer"] = "ACME Corp", ["amount"] = 1500.50m }
        };

        var decision = TableAccessDecision.Allowed(
            new TableIdentifier("sales", "dbo", "invoices"),
            new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["customer"] = ColumnAccessLevel.Clear, ["amount"] = ColumnAccessLevel.Clear },
            hasUnconstrainedColumnAllow: true
        );

        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Is<TableIdentifier>(t => t.TableName == "invoices"),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((sampleRows, decision)));

        var handler = new ODataHandler(metadataRepo, execService, logger);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: 50,
            skip: 0,
            select: "id,customer,amount",
            includeCount: true,
            headers: null
        );

        result.Success.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);
        result.Payload.ShouldNotBeNull();

        // Verify execution service was called with parsed select fields
        await execService.Received().ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Is<TableIdentifier>(t => t.TableName == "invoices"),
            first: 50,
            after: 0,
            queryArguments: null,
            requestedFields: Arg.Is<IReadOnlyList<string>?>(f => f != null && f.SequenceEqual(new[] { "id", "customer", "amount" })),
            requestHeaders: null,
            ct: Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_WhenDenied_Returns403WithODataError()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var logger = NullLogger<ODataHandler>.Instance;

        var decision = TableAccessDecision.Denied(
            new TableIdentifier("sales", "dbo", "invoices"),
            "No active consent granted for user SID"
        );

        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((Array.Empty<IReadOnlyDictionary<string, object?>>(), decision)));

        var handler = new ODataHandler(metadataRepo, execService, logger, DevEnv());

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: 10,
            skip: 0,
            select: null,
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(403);
        result.ErrorCode.ShouldBe("ACCESS_DENIED");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("No active consent");
    }

    private static Microsoft.Extensions.Hosting.IHostEnvironment DevEnv(string name = "Development")
    {
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Fact]
    public async Task ODataHandler_OutsideDevelopment_DeniedAndNotFoundAreGeneric403()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var decision = TableAccessDecision.Denied(new TableIdentifier("sales", "dbo", "invoices"), "Casbin: sid S-1-5-21-1 tenant acme denied");
        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Is<TableIdentifier>(t => t.TableName == "invoices"), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((Array.Empty<IReadOnlyDictionary<string, object?>>(), decision)));
        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Is<TableIdentifier>(t => t.TableName == "missing"), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>>(
                _ => throw new Autheris.Domain.Exceptions.TableNotFoundException(new TableIdentifier("acme", "dbo", "missing")));

        foreach (var handler in new[]
        {
            new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance, DevEnv("Production")),
            new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance)
        })
        {
            var denied = await handler.ExecuteEntitySetQueryAsync(null, "https://gateway/odata/v4", new TableIdentifier("sales", "dbo", "invoices"), 10, 0, null, false, null);
            var missing = await handler.ExecuteEntitySetQueryAsync(null, "https://gateway/odata/v4", new TableIdentifier("sales", "dbo", "missing"), 10, 0, null, false, null);

            denied.StatusCode.ShouldBe(403);
            missing.StatusCode.ShouldBe(403);
            denied.ErrorMessage.ShouldBe(missing.ErrorMessage);
            denied.ErrorMessage.ShouldNotBeNull().ShouldNotContain("Casbin");
            missing.ErrorMessage.ShouldNotBeNull().ShouldNotContain("acme");
        }
    }

    [Theory]
    [InlineData("int", "Edm.Int32")]
    [InlineData("integer", "Edm.Int32")]
    [InlineData("int4", "Edm.Int32")]
    [InlineData("serial", "Edm.Int32")]
    [InlineData("bigint", "Edm.Int64")]
    [InlineData("int8", "Edm.Int64")]
    [InlineData("bigserial", "Edm.Int64")]
    [InlineData("smallint", "Edm.Int16")]
    [InlineData("int2", "Edm.Int16")]
    [InlineData("decimal", "Edm.Decimal")]
    [InlineData("numeric", "Edm.Decimal")]
    [InlineData("money", "Edm.Decimal")]
    [InlineData("float", "Edm.Single")]
    [InlineData("real", "Edm.Single")]
    [InlineData("float4", "Edm.Single")]
    [InlineData("double", "Edm.Double")]
    [InlineData("float8", "Edm.Double")]
    [InlineData("double precision", "Edm.Double")]
    [InlineData("bool", "Edm.Boolean")]
    [InlineData("boolean", "Edm.Boolean")]
    [InlineData("bit", "Edm.Boolean")]
    [InlineData("date", "Edm.Date")]
    [InlineData("timestamp", "Edm.DateTimeOffset")]
    [InlineData("timestamptz", "Edm.DateTimeOffset")]
    [InlineData("datetime", "Edm.DateTimeOffset")]
    [InlineData("datetime2", "Edm.DateTimeOffset")]
    [InlineData("datetimeoffset", "Edm.DateTimeOffset")]
    [InlineData("guid", "Edm.Guid")]
    [InlineData("uuid", "Edm.Guid")]
    [InlineData("varchar", "Edm.String")]
    [InlineData("text", "Edm.String")]
    [InlineData("custom_geometry", "Edm.String")]
    [InlineData(null, "Edm.String")]
    [InlineData("", "Edm.String")]
    [InlineData("   ", "Edm.String")]
    public void ODataCsdlGenerator_AllDataTypes_MappedToExpectedEdmTypes(string? inputType, string expectedEdmType)
    {
        ODataCsdlGenerator.MapToEdmType(inputType).ShouldBe(expectedEdmType);
    }

    [Fact]
    public void ODataCsdlGenerator_EscapesXmlSpecialCharacters()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "special_chars"),
            Table = new Table
            {
                SchemaName = "dbo",
                TableName = "special_chars",
                Description = "Table & data <with> special \"quotes\" and 'apostrophes'",
                LongDescription = "Long & detailed <analysis> with 'special' \"symbols\""
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "col_one",
                    DataType = "varchar",
                    Description = "Col & <one> 'desc' \"quote\"",
                    LongDescription = "Long & <desc> 'test' \"here\""
                }
            ]
        };

        var xml = ODataCsdlGenerator.GenerateMetadataXml(new[] { table });

        xml.ShouldContain("Table &amp; data &lt;with&gt; special &quot;quotes&quot; and &apos;apostrophes&apos;");
        xml.ShouldContain("Long &amp; detailed &lt;analysis&gt; with &apos;special&apos; &quot;symbols&quot;");
        xml.ShouldContain("Col &amp; &lt;one&gt; &apos;desc&apos; &quot;quote&quot;");
        xml.ShouldContain("Long &amp; &lt;desc&gt; &apos;test&apos; &quot;here&quot;");
    }

    [Fact]
    public void ODataCsdlGenerator_CompositePrimaryKey_RendersMultipleKeyRefs()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("logistics", "shipping", "deliveries"),
            Table = new Table { SchemaName = "shipping", TableName = "deliveries" },
            PrimaryKeyColumns = ["tenant_id", "delivery_id"],
            Columns =
            [
                new TableColumn { ColumnName = "tenant_id", DataType = "integer" },
                new TableColumn { ColumnName = "delivery_id", DataType = "bigint" },
                new TableColumn { ColumnName = "status", DataType = "varchar" }
            ]
        };

        var xml = ODataCsdlGenerator.GenerateMetadataXml(new[] { table });

        xml.ShouldContain("<PropertyRef Name=\"tenant_id\" />");
        xml.ShouldContain("<PropertyRef Name=\"delivery_id\" />");
        xml.ShouldContain("<Property Name=\"tenant_id\" Type=\"Edm.Int32\" Nullable=\"false\" />");
        xml.ShouldContain("<Property Name=\"delivery_id\" Type=\"Edm.Int64\" Nullable=\"false\" />");
        xml.ShouldContain("<Property Name=\"status\" Type=\"Edm.String\" />");
    }

    [Fact]
    public void ODataCsdlGenerator_TableWithoutColumns_RendersFallbackId()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "empty_table"),
            Table = new Table { SchemaName = "dbo", TableName = "empty_table" },
            Columns = Array.Empty<TableColumn>()
        };

        var xml = ODataCsdlGenerator.GenerateMetadataXml(new[] { table });

        xml.ShouldContain("<Property Name=\"id\" Type=\"Edm.Int32\" Nullable=\"false\" />");
    }

    [Fact]
    public void ODataResponseFormatter_FormatServiceDocument_HandlesTrailingSlashInRoot()
    {
        var tables = new List<TableMetadata> { CreateSampleTable() };

        var doc1 = (Dictionary<string, object?>)ODataResponseFormatter.FormatServiceDocument("https://gateway/odata/v4", tables);
        var doc2 = (Dictionary<string, object?>)ODataResponseFormatter.FormatServiceDocument("https://gateway/odata/v4/", tables);

        doc1["@odata.context"].ShouldBe("https://gateway/odata/v4/$metadata");
        doc2["@odata.context"].ShouldBe("https://gateway/odata/v4/$metadata");

        var value1 = (List<object>)doc1["value"]!;
        value1.Count.ShouldBe(1);
        var entry = (Dictionary<string, object?>)value1[0];
        entry["name"].ShouldBe("sales_dbo_invoices");
        entry["kind"].ShouldBe("EntitySet");
        entry["url"].ShouldBe("sales/dbo/invoices");
    }

    [Fact]
    public void ODataResponseFormatter_FormatEntitySetResponse_WithAndWithoutCount()
    {
        var table = new TableIdentifier("sales", "dbo", "invoices");
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["amount"] = 99.9m }
        };

        var respWithCount = (Dictionary<string, object?>)ODataResponseFormatter.FormatEntitySetResponse("https://gateway/odata/v4/", table, rows, totalCount: 1);
        respWithCount["@odata.context"].ShouldBe("https://gateway/odata/v4/$metadata#sales_dbo_invoices");
        respWithCount["@odata.count"].ShouldBe(1);
        respWithCount["value"].ShouldBe(rows);

        var respWithoutCount = (Dictionary<string, object?>)ODataResponseFormatter.FormatEntitySetResponse("https://gateway/odata/v4", table, rows, totalCount: null);
        respWithoutCount.ContainsKey("@odata.count").ShouldBeFalse();
        respWithoutCount["value"].ShouldBe(rows);
    }

    [Fact]
    public void ODataResponseFormatter_FormatErrorResponse_StructuresODataV4Envelope()
    {
        var errorObj = ODataResponseFormatter.FormatErrorResponse("InvalidQueryOption", "Detailed message");
        var json = System.Text.Json.JsonSerializer.Serialize(errorObj);

        json.ShouldContain("\"error\":{\"code\":\"InvalidQueryOption\",\"message\":\"Detailed message\"}");
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_DefaultPagination_Applies100And0()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var logger = NullLogger<ODataHandler>.Instance;

        var decision = TableAccessDecision.Allowed(
            new TableIdentifier("sales", "dbo", "invoices"),
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true
        );

        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((Array.Empty<IReadOnlyDictionary<string, object?>>(), decision)));

        var handler = new ODataHandler(metadataRepo, execService, logger);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: null,
            skip: null,
            select: null,
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeTrue();
        await execService.Received().ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<TableIdentifier>(),
            first: 100,
            after: 0,
            queryArguments: null,
            requestedFields: null,
            requestHeaders: null,
            ct: Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_ClampsLargeTopTo1000()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var logger = NullLogger<ODataHandler>.Instance;

        var decision = TableAccessDecision.Allowed(
            new TableIdentifier("sales", "dbo", "invoices"),
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true
        );

        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((Array.Empty<IReadOnlyDictionary<string, object?>>(), decision)));

        var handler = new ODataHandler(metadataRepo, execService, logger);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: 5000,
            skip: 10,
            select: null,
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeTrue();
        await execService.Received().ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<TableIdentifier>(),
            first: 1000,
            after: 10,
            queryArguments: null,
            requestedFields: null,
            requestHeaders: null,
            ct: Arg.Any<CancellationToken>()
        );
    }

    [Theory]
    [InlineData(-1, 0, "$top")]
    [InlineData(10, -5, "$skip")]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_NegativeTopOrSkip_Returns400(int? top, int? skip, string paramName)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: top,
            skip: skip,
            select: null,
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        result.ErrorCode.ShouldBe("InvalidQueryOption");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain(paramName);
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_SelectParsingAndTrimming()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var decision = TableAccessDecision.Allowed(
            new TableIdentifier("sales", "dbo", "invoices"),
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true
        );

        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((Array.Empty<IReadOnlyDictionary<string, object?>>(), decision)));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: 10,
            skip: 0,
            select: "  id ,   customer , amount  ",
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeTrue();
        await execService.Received().ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            requestedFields: Arg.Is<IReadOnlyList<string>?>(f => f != null && f.SequenceEqual(new[] { "id", "customer", "amount" })),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Theory]
    [InlineData("id; DROP TABLE users")]
    [InlineData("id--")]
    [InlineData("id/*comment*/")]
    [InlineData("user name")]
    [InlineData("col@domain")]
    [InlineData("table.col")]
    [InlineData("amount+1")]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_SelectWithInvalidCharacters_Returns400(string invalidSelect)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: 10,
            skip: 0,
            select: invalidSelect,
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        result.ErrorCode.ShouldBe("InvalidQueryOption");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("contains invalid characters");
    }

    private sealed class TestDbTimeoutException(string message) : System.Data.Common.DbException(message);

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_TimeoutException_Returns504()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>>(
                _ => throw new TimeoutException("Database command timed out."));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: 10,
            skip: 0,
            select: null,
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(504);
        result.ErrorCode.ShouldBe("ExecutionTimeout");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("execution time limit");
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_DbTimeoutException_Returns504()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>>(
                _ => throw new TestDbTimeoutException("SqlException: Execution Timeout Expired. The timeout period elapsed."));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: new TableIdentifier("sales", "dbo", "invoices"),
            top: 10,
            skip: 0,
            select: null,
            includeCount: false,
            headers: null
        );

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(504);
        result.ErrorCode.ShouldBe("ExecutionTimeout");
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_UnexpectedException_Returns500()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>>(
                _ => throw new InvalidOperationException("Fatal unexpected engine crash"));

        var handlerDev = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance, DevEnv("Development"));
        var resultDev = await handlerDev.ExecuteEntitySetQueryAsync(null, "https://gateway/odata/v4", new TableIdentifier("sales", "dbo", "invoices"), 10, 0, null, false, null);

        resultDev.Success.ShouldBeFalse();
        resultDev.StatusCode.ShouldBe(500);
        resultDev.ErrorCode.ShouldBe("INTERNAL_ERROR");
        resultDev.ErrorMessage.ShouldNotBeNull().ShouldContain("Fatal unexpected engine crash");

        var handlerProd = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance, DevEnv("Production"));
        var resultProd = await handlerProd.ExecuteEntitySetQueryAsync(null, "https://gateway/odata/v4", new TableIdentifier("sales", "dbo", "invoices"), 10, 0, null, false, null);

        resultProd.Success.ShouldBeFalse();
        resultProd.StatusCode.ShouldBe(500);
        resultProd.ErrorCode.ShouldBe("INTERNAL_ERROR");
        resultProd.ErrorMessage.ShouldNotBeNull().ShouldNotContain("Fatal unexpected engine crash");
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_UnauthorizedException_Returns401()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>>(
                _ => throw new Autheris.Domain.Exceptions.GatewayUnauthorizedException("Authentication required"));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(null, "https://gateway/odata/v4", new TableIdentifier("sales", "dbo", "invoices"), 10, 0, null, false, null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(401);
        result.ErrorCode.ShouldBe("UNAUTHORIZED");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("Authentication required");
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_SecurityException_Returns403()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>>(
                _ => throw new Autheris.Domain.Exceptions.GatewaySecurityException("Zero-Trust policy violation"));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance, DevEnv("Development"));

        var result = await handler.ExecuteEntitySetQueryAsync(null, "https://gateway/odata/v4", new TableIdentifier("sales", "dbo", "invoices"), 10, 0, null, false, null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(403);
        result.ErrorCode.ShouldBe("ACCESS_DENIED");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("Zero-Trust policy violation");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_NullOrEmptyServiceRoot_Throws(string? rootUrl)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        await Should.ThrowAsync<ArgumentException>(() =>
            handler.ExecuteEntitySetQueryAsync(null, rootUrl!, new TableIdentifier("sales", "dbo", "invoices"), 10, 0, null, false, null));
    }

    [Fact]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_ForwardsHeaders()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var decision = TableAccessDecision.Allowed(
            new TableIdentifier("sales", "dbo", "invoices"),
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true
        );

        execService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>((Array.Empty<IReadOnlyDictionary<string, object?>>(), decision)));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);
        var headers = new Dictionary<string, string[]> { ["X-Tenant-ID"] = ["tenant_abc"] };

        var result = await handler.ExecuteEntitySetQueryAsync(null, "https://gateway/odata/v4", new TableIdentifier("sales", "dbo", "invoices"), 10, 0, null, false, headers);

        result.Success.ShouldBeTrue();
        await execService.Received().ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            requestHeaders: headers, Arg.Any<CancellationToken>());
    }

    private static ClaimsPrincipal CreateAuthenticatedUser(string tenant = "sales")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
            new("tenant_id", tenant)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public async Task ODataHandler_GetMetadataCsdlAsync_ZeroTrust_OmitsDeniedColumns()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var sampleTable = CreateSampleTable();
        var user = CreateAuthenticatedUser("sales");

        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { sampleTable }));

        // Decision: allow id, customer, amount; deny invoice_date, created_at
        var decision = TableAccessDecision.Allowed(
            sampleTable.Identifier,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["customer"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear,
                ["invoice_date"] = ColumnAccessLevel.Deny,
                ["created_at"] = ColumnAccessLevel.Deny
            }
        );

        execService.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), sampleTable.Identifier, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(decision));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var xml = await handler.GetMetadataCsdlAsync(user);

        xml.ShouldContain("<Property Name=\"id\"");
        xml.ShouldContain("<Property Name=\"customer\"");
        xml.ShouldContain("<Property Name=\"amount\"");
        xml.ShouldNotContain("<Property Name=\"invoice_date\"");
        xml.ShouldNotContain("<Property Name=\"created_at\"");
    }

    [Fact]
    public async Task ODataHandler_GetServiceDocumentAsync_ZeroTrust_ExcludesDeniedTables()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var table1 = CreateSampleTable();
        var table2 = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "salaries"),
            Table = new Table { SchemaName = "dbo", TableName = "salaries" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
        };
        var user = CreateAuthenticatedUser("sales");

        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { table1, table2 }));

        // table1 allowed, table2 denied
        execService.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), table1.Identifier, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table1.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        execService.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), table2.Identifier, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Denied(table2.Identifier, "Forbidden")));

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var doc = (Dictionary<string, object?>)await handler.GetServiceDocumentAsync("https://gateway/odata/v4", user);

        var value = (List<object>)doc["value"]!;
        value.Count.ShouldBe(1);
        var entry = (Dictionary<string, object?>)value[0];
        entry["name"].ShouldBe("sales_dbo_invoices");
    }
}

