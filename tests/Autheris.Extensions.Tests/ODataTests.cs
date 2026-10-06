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
}
