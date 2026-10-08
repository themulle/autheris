namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ArrowFlightSqlSecurityTests
{
    private readonly IArrowExportService _exportService = Substitute.For<IArrowExportService>();
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IOptions<GatewayOptions> _options = Options.Create(new GatewayOptions());

    [Fact]
    public async Task GetFlightInfo_Unauthenticated_ThrowsSecurityException()
    {
        // Arrange
        var server = new ArrowFlightSqlServer(
            _exportService,
            _metadataRepo,
            _options,
            NullLogger<ArrowFlightSqlServer>.Instance);

        var unauth = new ClaimsPrincipal(new ClaimsIdentity());

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await server.GetFlightInfoAsync("SELECT * FROM customers", unauth, new TenantId("tenant-1"));
        });
    }

    [Fact]
    public async Task GetFlightInfo_Authenticated_IssuesCryptographicallySignedTicket()
    {
        // Arrange
        var tableMeta = new TableMetadata
        {
            Identifier = new TableIdentifier("corp", "sales", "orders"),
            Table = new Table { SourceName = "corp", SchemaName = "sales", TableName = "orders" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { tableMeta }));

        var server = new ArrowFlightSqlServer(
            _exportService,
            _metadataRepo,
            _options,
            NullLogger<ArrowFlightSqlServer>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "bi-analyst"), new Claim(ClaimTypes.Role, "DataAnalyst")], "Bearer"));

        // Act
        var flightInfo = await server.GetFlightInfoAsync("SELECT * FROM orders", principal, new TenantId("tenant-1"));

        // Assert
        flightInfo.ShouldNotBeNull();
        flightInfo.Ticket.ShouldNotBeNull();
        flightInfo.Ticket.TenantId.ShouldBe("tenant-1");
        flightInfo.Ticket.Signature.Length.ShouldBe(64); // SHA-256 hex
    }

    [Fact]
    public async Task DoGetStream_TamperedSignature_ThrowsSecurityException()
    {
        // Arrange
        var server = new ArrowFlightSqlServer(
            _exportService,
            _metadataRepo,
            _options,
            NullLogger<ArrowFlightSqlServer>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "bi-analyst"), new Claim(ClaimTypes.Role, "DataAnalyst")], "Bearer"));

        var tamperedTicket = new FlightSqlTicket(
            TicketId: "t-1",
            TenantId: "tenant-1",
            Query: "SELECT * FROM secrets",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Signature: "0000000000000000000000000000000000000000000000000000000000000000" // Tampered
        );

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(tamperedTicket, principal))
            {
            }
        });
    }

    [Fact]
    public async Task DoGetStream_ExpiredTicket_ThrowsSecurityException()
    {
        // Arrange
        var server = new ArrowFlightSqlServer(
            _exportService,
            _metadataRepo,
            _options,
            NullLogger<ArrowFlightSqlServer>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "bi-analyst"), new Claim(ClaimTypes.Role, "DataAnalyst")], "Bearer"));

        // Ticket created 2 hours ago (expired beyond 30-min window)
        var expiredTime = DateTimeOffset.UtcNow.AddHours(-2);
        var signature = ArrowFlightSqlServer.ComputeSignature("t-1", "tenant-1", "SELECT 1", expiredTime, "default-secret");

        var expiredTicket = new FlightSqlTicket(
            TicketId: "t-1",
            TenantId: "tenant-1",
            Query: "SELECT 1",
            CreatedAtUtc: expiredTime,
            Signature: signature
        );

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(expiredTicket, principal))
            {
            }
        });
    }

    [Fact]
    public async Task GetTables_TenantIsolation_ReturnsOnlyAllowedTables()
    {
        // Arrange
        var table1 = new TableMetadata
        {
            Identifier = new TableIdentifier("tenant-1", "sales", "orders"),
            Table = new Table { SourceName = "tenant-1", SchemaName = "sales", TableName = "orders" }
        };
        var tableOther = new TableMetadata
        {
            Identifier = new TableIdentifier("tenant-2", "finance", "salaries"),
            Table = new Table { SourceName = "tenant-2", SchemaName = "finance", TableName = "salaries" }
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { table1, tableOther }));

        var server = new ArrowFlightSqlServer(
            _exportService,
            _metadataRepo,
            _options,
            NullLogger<ArrowFlightSqlServer>.Instance);

        // Wunsch 9: a GovernanceAdmin sees the whole catalog of the tenant, but still never another tenant's tables
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "user"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "Bearer"));

        // Act
        var tables = await server.GetTablesAsync(principal, new TenantId("tenant-1"));

        // Assert (SEC M-7: Strict tenant isolation; only tenant-1 returned, never tenant-2)
        tables.Count.ShouldBe(1);
        tables[0].TableName.ShouldBe("orders");
        tables[0].Schema.ShouldBe("sales");
    }
}
