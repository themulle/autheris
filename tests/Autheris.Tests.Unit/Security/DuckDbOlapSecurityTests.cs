using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

public sealed class DuckDbOlapSecurityTests
{
    private readonly DuckDbOlapOptions _defaultOptions = new()
    {
        Enabled = true,
        MaxMemory = "512MB",
        MaxStagedRowsPerTable = 1000,
        QueryTimeoutSeconds = 15,
        MaxThreads = 2
    };

    [Fact]
    public async Task SEC_OLAP_01_ExternalAccess_Disabled_ThrowsException_On_FileAccess()
    {
        // Arrange
        var engine = new DuckDbOlapEngine(_defaultOptions, NullLogger<DuckDbOlapEngine>.Instance);
        var table = new TableIdentifier("sales", "public", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "double" }
            ]
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["amount"] = 99.5 }
        };

        var request = new OlapQueryRequest(
            Sql: "SELECT * FROM read_csv('/etc/passwd')",
            Sources: [new OlapTableSource(table, rows, meta)]
        );

        // Act & Assert (DuckDB must reject external file reads)
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await engine.ExecuteOlapQueryAsync(request));
        Assert.True(
            ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("read_csv", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("enable_external_access", StringComparison.OrdinalIgnoreCase),
            $"Expected external access error, got: {ex.Message}");
    }

    [Fact]
    public async Task SEC_OLAP_02_MaxStagedRows_Exceeded_Throws_InvalidOperationException()
    {
        // Arrange: limit is 10 rows
        var strictOptions = new DuckDbOlapOptions
        {
            MaxStagedRowsPerTable = 10
        };
        var engine = new DuckDbOlapEngine(strictOptions, NullLogger<DuckDbOlapEngine>.Instance);

        var table = new TableIdentifier("sales", "public", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (int i = 0; i < 20; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["id"] = i });
        }

        var request = new OlapQueryRequest(
            Sql: "SELECT count(*) FROM orders",
            Sources: [new OlapTableSource(table, rows, meta)]
        );

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.ExecuteOlapQueryAsync(request));
        Assert.Contains("MaxStagedRowsPerTable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SEC_OLAP_03_Session_Is_Completely_Isolated_Between_Requests()
    {
        // Arrange
        var engine = new DuckDbOlapEngine(_defaultOptions, NullLogger<DuckDbOlapEngine>.Instance);
        var table = new TableIdentifier("finance", "public", "transactions");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "secret_token", DataType = "string" }
            ]
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["secret_token"] = "TENANT_A_SECRET" }
        };

        // Query 1: Tenant A stages transactions
        var requestA = new OlapQueryRequest(
            Sql: "SELECT secret_token FROM transactions WHERE id = 1",
            Sources: [new OlapTableSource(table, rows, meta)]
        );
        var resultA = await engine.ExecuteOlapQueryAsync(requestA);
        Assert.Single(resultA.Rows);
        Assert.Equal("TENANT_A_SECRET", resultA.Rows[0][0]?.ToString());

        // Query 2: Tenant B runs query without staging transactions table
        var requestB = new OlapQueryRequest(
            Sql: "SELECT * FROM transactions",
            Sources: [] // Tenant B stages nothing!
        );

        // Act & Assert: transactions table does NOT exist in Tenant B's isolated session
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await engine.ExecuteOlapQueryAsync(requestB));
        Assert.True(
            ex.Message.Contains("transactions", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Catalog Error", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase),
            $"Expected table not found error, got: {ex.Message}");
    }

    [Fact]
    public async Task SEC_OLAP_04_Preserves_PII_Masking_And_Computes_WindowFunctions()
    {
        // Arrange
        var engine = new DuckDbOlapEngine(_defaultOptions, NullLogger<DuckDbOlapEngine>.Instance);
        var table = new TableIdentifier("crm", "public", "customers");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "string" },
                new TableColumn { ColumnName = "revenue", DataType = "double" }
            ]
        };

        // Masked rows from governance layer
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 101, ["email"] = "j***@corp.com", ["revenue"] = 500.0 },
            new Dictionary<string, object?> { ["id"] = 102, ["email"] = "a***@corp.com", ["revenue"] = 1200.0 },
            new Dictionary<string, object?> { ["id"] = 103, ["email"] = "b***@corp.com", ["revenue"] = 800.0 }
        };

        // Analytical query with window function & aggregation
        var sql = @"
            SELECT 
                id, 
                email, 
                revenue, 
                RANK() OVER (ORDER BY revenue DESC) as rev_rank,
                SUM(revenue) OVER () as total_rev
            FROM customers 
            ORDER BY rev_rank ASC";

        var request = new OlapQueryRequest(
            Sql: sql,
            Sources: [new OlapTableSource(table, rows, meta)]
        );

        // Act
        var result = await engine.ExecuteOlapQueryAsync(request);

        // Assert
        Assert.Equal(3, result.TotalRowCount);
        Assert.Contains("id", result.Columns);
        Assert.Contains("email", result.Columns);
        Assert.Contains("revenue", result.Columns);
        Assert.Contains("rev_rank", result.Columns);
        Assert.Contains("total_rev", result.Columns);

        // Top rank should be customer 102 with masked email
        var topRow = result.Rows[0];
        Assert.Equal(102, Convert.ToInt32(topRow[0]));
        Assert.Equal("a***@corp.com", topRow[1]?.ToString()); // PII masking strictly preserved!
        Assert.Equal(1200.0, Convert.ToDouble(topRow[2]));
        Assert.Equal(1, Convert.ToInt64(topRow[3])); // rev_rank
        Assert.Equal(2500.0, Convert.ToDouble(topRow[4])); // total_rev
    }

    [Fact]
    public async Task SEC_OLAP_05_Handles_Null_Values_Correctly()
    {
        // Arrange
        var engine = new DuckDbOlapEngine(_defaultOptions, NullLogger<DuckDbOlapEngine>.Instance);
        var table = new TableIdentifier("sales", "public", "deals");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "note", DataType = "string" },
                new TableColumn { ColumnName = "closed_amount", DataType = "double" }
            ]
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["note"] = null, ["closed_amount"] = 300.0 },
            new Dictionary<string, object?> { ["id"] = 2, ["note"] = "Won", ["closed_amount"] = null }
        };

        var request = new OlapQueryRequest(
            Sql: "SELECT id, note, closed_amount FROM deals ORDER BY id",
            Sources: [new OlapTableSource(table, rows, meta)]
        );

        // Act
        var result = await engine.ExecuteOlapQueryAsync(request);

        // Assert
        Assert.Equal(2, result.Rows.Count);
        Assert.Null(result.Rows[0][1]); // null note
        Assert.Equal(300.0, Convert.ToDouble(result.Rows[0][2]));

        Assert.Equal("Won", result.Rows[1][1]?.ToString());
        Assert.Null(result.Rows[1][2]); // null closed_amount
    }

    [Fact]
    public async Task SEC_OLAP_06_Performs_MultiTable_CrossDomain_Join()
    {
        // Arrange
        var engine = new DuckDbOlapEngine(_defaultOptions, NullLogger<DuckDbOlapEngine>.Instance);

        var customersTable = new TableIdentifier("crm", "public", "customers");
        var customersMeta = new TableMetadata
        {
            Identifier = customersTable,
            Columns = [
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "company_name", DataType = "string" }
            ]
        };
        var customerRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["customer_id"] = 1, ["company_name"] = "Acme Corp" },
            new Dictionary<string, object?> { ["customer_id"] = 2, ["company_name"] = "Globex" }
        };

        var invoicesTable = new TableIdentifier("billing", "public", "invoices");
        var invoicesMeta = new TableMetadata
        {
            Identifier = invoicesTable,
            Columns = [
                new TableColumn { ColumnName = "invoice_id", DataType = "int" },
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "total", DataType = "double" }
            ]
        };
        var invoiceRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["invoice_id"] = 1001, ["customer_id"] = 1, ["total"] = 450.0 },
            new Dictionary<string, object?> { ["invoice_id"] = 1002, ["customer_id"] = 1, ["total"] = 550.0 },
            new Dictionary<string, object?> { ["invoice_id"] = 1003, ["customer_id"] = 2, ["total"] = 200.0 }
        };

        var sql = @"
            SELECT 
                c.company_name, 
                COUNT(i.invoice_id) as invoice_count, 
                SUM(i.total) as total_spent
            FROM customers c
            JOIN invoices i ON c.customer_id = i.customer_id
            GROUP BY c.company_name
            ORDER BY total_spent DESC";

        var request = new OlapQueryRequest(
            Sql: sql,
            Sources: [
                new OlapTableSource(customersTable, customerRows, customersMeta),
                new OlapTableSource(invoicesTable, invoiceRows, invoicesMeta)
            ]
        );

        // Act
        var result = await engine.ExecuteOlapQueryAsync(request);

        // Assert
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Acme Corp", result.Rows[0][0]?.ToString());
        Assert.Equal(2, Convert.ToInt64(result.Rows[0][1]));
        Assert.Equal(1000.0, Convert.ToDouble(result.Rows[0][2]));

        Assert.Equal("Globex", result.Rows[1][0]?.ToString());
        Assert.Equal(1, Convert.ToInt64(result.Rows[1][1]));
        Assert.Equal(200.0, Convert.ToDouble(result.Rows[1][2]));
    }

    [Fact]
    public async Task SEC_OLAP_07_Endpoint_Unauthenticated_Returns401()
    {
        // Arrange
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Response.Body = new System.IO.MemoryStream();
        // Unauthenticated user
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity()); // IsAuthenticated = false

        var options = Options.Create(new GatewayOptions { Profile = "Strict" });
        var engine = new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance);

        // Act
        await Autheris.Api.Endpoints.DuckDbOlapEndpoints.HandleOlapQueryAsync(
            httpContext,
            engine,
            metadataRepository: null!,
            connectorRegistry: null!,
            accessResolver: null!,
            maskingProvider: null!,
            rebacEvaluator: null!,
            gatewayOptions: options,
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        // Assert
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task SEC_OLAP_08_Endpoint_InvalidJson_Returns400()
    {
        // Arrange
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Response.Body = new System.IO.MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "testuser")], "Bearer"));

        var invalidJsonBytes = System.Text.Encoding.UTF8.GetBytes("{ broken json: ");
        httpContext.Request.Body = new System.IO.MemoryStream(invalidJsonBytes);

        var options = Options.Create(new GatewayOptions { Profile = "Strict" });
        var engine = new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance);

        // Act
        await Autheris.Api.Endpoints.DuckDbOlapEndpoints.HandleOlapQueryAsync(
            httpContext,
            engine,
            metadataRepository: null!,
            connectorRegistry: null!,
            accessResolver: null!,
            maskingProvider: null!,
            rebacEvaluator: null!,
            gatewayOptions: options,
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        // Assert
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task RR_L3_01_DuckDb_Configuration_IsLocked_CannotOverrideLimits()
    {
        // Arrange
        var engine = new DuckDbOlapEngine(_defaultOptions, NullLogger<DuckDbOlapEngine>.Instance);
        var table = new TableIdentifier("sales", "public", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var request = new OlapQueryRequest(
            Sql: "PRAGMA max_memory = '100GB'; SELECT 1 as id;",
            Sources: [new OlapTableSource(table, new List<IReadOnlyDictionary<string, object?>>(), meta)]
        );

        // Act & Assert: Attempting to modify locked configuration must be rejected by DuckDB
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await engine.ExecuteOlapQueryAsync(request));
        Assert.True(
            ex.Message.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("cannot be changed", StringComparison.OrdinalIgnoreCase),
            $"Expected configuration lock exception, got: {ex.Message}");
    }

    [Fact]
    public async Task SQL202_DuckDbOlap_TableExceedingMaxStagedRows_FailsWith400()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER")], "Bearer"));

        var json = JsonSerializer.Serialize(new
        {
            sql = "SELECT COUNT(*) FROM sales.orders",
            tableNames = new[] { "sales.orders" }
        });
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var table = new TableIdentifier("sales", "public", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);

        // Connector that returns 11 rows when limit is 11
        var connector = Substitute.For<IAutherisConnector>();
        var splitManager = Substitute.For<IConnectorSplitManager>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        connector.SplitManager.Returns(splitManager);
        connector.RecordSource.Returns(recordSource);

        var split = new ConnectorSplit("s1", new Dictionary<string, object?>());
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([split]);

        // MaxStagedRowsPerTable is configured as 10. The endpoint requests limit = 11.
        // If the table has 11 rows, recordSource returns 11 rows.
        var rows = Enumerable.Range(1, 11).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i }).ToList();
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(rows);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(table, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = connector; return true; });

        var accessResolver = Substitute.For<ICrossDomainAccessResolver>();
        accessResolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var rebac = Substitute.For<IRebacEvaluator>();
        rebac.IsEnabled.Returns(false);

        var options = Options.Create(new GatewayOptions
        {
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true, MaxStagedRowsPerTable = 10 }
        });
        var engine = Substitute.For<IDuckDbOlapEngine>();

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            httpContext,
            engine,
            metadataRepo,
            registry,
            accessResolver,
            Substitute.For<IColumnMaskingProvider>(),
            rebac,
            options,
            NullLoggerFactory.Instance);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseText = new StreamReader(httpContext.Response.Body).ReadToEnd();
        responseText.ShouldContain("exceeds maximum allowed staging rows (10)");
    }
}

