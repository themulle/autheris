namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ArrowExportSecurityTests
{
    private static IOptions<GatewayOptions> CreateOptions(int maxRows = 1000)
    {
        return Options.Create(new GatewayOptions
        {
            Arrow = new ArrowExportOptions
            {
                Enabled = true,
                MaxExportRows = maxRows,
                BatchSize = 1000
            },
            Rebac = new RebacOptions
            {
                Enabled = true
            }
        });
    }

    [Fact]
    public void ArrowExport_BuildRecordBatch_CreatesValidTypedArrays()
    {
        // Arrange
        var options = CreateOptions();
        var service = new ArrowExportService(options, NullLogger<ArrowExportService>.Instance);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["name"] = "Alice",
                ["score"] = 98.5,
                ["active"] = true,
                ["created_at"] = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
            },
            new Dictionary<string, object?>
            {
                ["id"] = 2,
                ["name"] = "Bob",
                ["score"] = 72.0,
                ["active"] = false,
                ["created_at"] = new DateTimeOffset(2026, 10, 2, 14, 30, 0, TimeSpan.Zero)
            }
        };

        // Act
        var batch = service.BuildRecordBatch(rows);

        // Assert
        batch.Length.ShouldBe(2);
        batch.ColumnCount.ShouldBe(5);

        var idArray = batch.Column("id").ShouldBeOfType<Int32Array>();
        idArray.GetValue(0).ShouldBe(1);
        idArray.GetValue(1).ShouldBe(2);

        var nameArray = batch.Column("name").ShouldBeOfType<StringArray>();
        nameArray.GetString(0).ShouldBe("Alice");
        nameArray.GetString(1).ShouldBe("Bob");

        var scoreArray = batch.Column("score").ShouldBeOfType<DoubleArray>();
        scoreArray.GetValue(0).ShouldBe(98.5);

        var activeArray = batch.Column("active").ShouldBeOfType<BooleanArray>();
        activeArray.GetValue(0).ShouldBe(true);
        activeArray.GetValue(1).ShouldBe(false);
    }

    [Fact]
    public void ArrowExport_NullAndTypeSafety_CorrectlyBuildsNullBitmaps()
    {
        // Arrange
        var options = CreateOptions();
        var service = new ArrowExportService(options, NullLogger<ArrowExportService>.Instance);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["val_int"] = 42,
                ["val_str"] = "hello",
                ["val_double"] = 3.14
            },
            new Dictionary<string, object?>
            {
                ["val_int"] = null,
                ["val_str"] = null,
                ["val_double"] = null
            }
        };

        // Act
        var batch = service.BuildRecordBatch(rows);

        // Assert
        batch.Length.ShouldBe(2);

        var intArray = batch.Column("val_int").ShouldBeOfType<Int32Array>();
        intArray.IsNull(0).ShouldBeFalse();
        intArray.IsNull(1).ShouldBeTrue();
        intArray.NullCount.ShouldBe(1);

        var strArray = batch.Column("val_str").ShouldBeOfType<StringArray>();
        strArray.IsNull(0).ShouldBeFalse();
        strArray.IsNull(1).ShouldBeTrue();
        strArray.NullCount.ShouldBe(1);

        var dblArray = batch.Column("val_double").ShouldBeOfType<DoubleArray>();
        dblArray.IsNull(0).ShouldBeFalse();
        dblArray.IsNull(1).ShouldBeTrue();
        dblArray.NullCount.ShouldBe(1);
    }

    [Fact]
    public async Task ArrowExport_SerializesToValidIpcStream_CanBeReadBack()
    {
        // Arrange
        var options = CreateOptions();
        var service = new ArrowExportService(options, NullLogger<ArrowExportService>.Instance);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["sku"] = "A100", ["qty"] = 10 },
            new Dictionary<string, object?> { ["sku"] = "B200", ["qty"] = 25 }
        };

        // Act: Serialize to byte array
        var bytes = await service.ExportToBytesAsync(rows);
        bytes.Length.ShouldBeGreaterThan(0);

        // Assert: Read back using ArrowStreamReader
        using var stream = new MemoryStream(bytes);
        using var reader = new ArrowStreamReader(stream);

        var readBatch = await reader.ReadNextRecordBatchAsync();
        readBatch.ShouldNotBeNull();
        readBatch.Length.ShouldBe(2);

        var skuCol = readBatch.Column("sku").ShouldBeOfType<StringArray>();
        skuCol.GetString(0).ShouldBe("A100");
        skuCol.GetString(1).ShouldBe("B200");

        var qtyCol = readBatch.Column("qty").ShouldBeOfType<Int32Array>();
        qtyCol.GetValue(0).ShouldBe(10);
        qtyCol.GetValue(1).ShouldBe(25);
    }

    [Fact]
    public async Task ArrowExport_EnforcesColumnMasking_MasksSensitiveFields()
    {
        // Arrange: Row has already passed column masking provider
        var options = CreateOptions();
        var service = new ArrowExportService(options, NullLogger<ArrowExportService>.Instance);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["account_id"] = "ACC-99",
                ["iban"] = "[REDACTED]",
                ["salary"] = "[CONFIDENTIAL]"
            }
        };

        // Act
        var batch = service.BuildRecordBatch(rows);

        // Assert: Masked values are preserved in Arrow array
        var ibanArray = batch.Column("iban").ShouldBeOfType<StringArray>();
        ibanArray.GetString(0).ShouldBe("[REDACTED]");

        var salaryArray = batch.Column("salary").ShouldBeOfType<StringArray>();
        salaryArray.GetString(0).ShouldBe("[CONFIDENTIAL]");
    }

    [Fact]
    public async Task ArrowExport_EnforcesMaxRowLimit_ThrowsOnExcessiveRows()
    {
        // Arrange: MaxExportRows is set to 2
        var options = CreateOptions(maxRows: 2);
        var service = new ArrowExportService(options, NullLogger<ArrowExportService>.Instance);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["id"] = 2 },
            new Dictionary<string, object?> { ["id"] = 3 }
        };

        // Act & Assert: DoS guardrail fails closed
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var ms = new MemoryStream();
            await service.ExportToStreamAsync(rows, ms);
        });
    }

    [Fact]
    public async Task ArrowExportEndpoint_UnauthenticatedCaller_Returns401()
    {
        // Arrange
        var options = CreateOptions();
        var service = new ArrowExportService(options, NullLogger<ArrowExportService>.Instance);

        var httpContext = new DefaultHttpContext();
        // user is unauthenticated

        // Act
        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            httpContext,
            service,
            sqlExecutionService: null,
            CancellationToken.None);

        // Assert
        result.ShouldBeOfType<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result;
        problem.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ArrowExportEndpoint_MissingTableAndSql_Returns400BadRequest()
    {
        // Arrange
        var options = CreateOptions();
        var service = new ArrowExportService(options, NullLogger<ArrowExportService>.Instance);

        var httpContext = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user:alice"),
            new("tenant_id", "tenant-1")
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        // Act: No table or sql provided in query or body
        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            httpContext,
            service,
            sqlExecutionService: null,
            CancellationToken.None);

        // Assert
        result.ShouldBeOfType<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result;
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext CreateAuthenticatedArrowContext(string table)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user:alice"),
            new("tenant_id", "tenant-1")
        }, "TestAuth"));
        httpContext.Request.QueryString = new QueryString($"?table={table}");
        return httpContext;
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status500InternalServerError)]
    [InlineData(typeof(System.Security.SecurityException), StatusCodes.Status403Forbidden)]
    [InlineData(typeof(ArgumentException), StatusCodes.Status400BadRequest)]
    public async Task RR_L3_03_ArrowExportEndpoint_NeverEchoesExceptionMessages(Type exceptionType, int expectedStatus)
    {
        const string secret = "Npgsql: relation \"hr.salaries_secret\" does not exist; Host=db-prod-01";
        var service = new ArrowExportService(CreateOptions(), NullLogger<ArrowExportService>.Instance);
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<Task<GovernedSqlResult>>(_ => throw (Exception)Activator.CreateInstance(exceptionType, secret)!);

        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            CreateAuthenticatedArrowContext("finance.invoices"), service, sql, CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(expectedStatus);
        (problem.ProblemDetails.Detail ?? string.Empty).ShouldNotContain("salaries_secret");
        (problem.ProblemDetails.Detail ?? string.Empty).ShouldNotContain("db-prod-01");
    }

    [Fact]
    public async Task RR_L3_03_ArrowExportEndpoint_WithoutSqlService_Returns503()
    {
        var service = new ArrowExportService(CreateOptions(), NullLogger<ArrowExportService>.Instance);

        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            CreateAuthenticatedArrowContext("finance.invoices"), service, sqlExecutionService: null, CancellationToken.None);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }
}
