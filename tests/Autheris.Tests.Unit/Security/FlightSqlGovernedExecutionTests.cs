namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Autheris.Application.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// WebSQL findings 4.1: Flight SQL runs the ticket's statement through the governed WebSQL pipeline (catalog, row
/// filters, masks, Gateway:RowLimits:FlightSql) instead of returning fixed sample rows.
/// </summary>
public sealed class FlightSqlGovernedExecutionTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly ClaimsPrincipal User =
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"), new Claim("tenant_id", "tenant-1")], "Bearer"));

    private static (ArrowFlightSqlServer Server, IGovernedSqlExecutionService Sql) CreateServer(GatewayOptions? options = null)
    {
        options ??= new GatewayOptions
        {
            Arrow = new ArrowExportOptions { FlightTicketSigningKey = "0123456789abcdef0123456789abcdef", MaxExportRows = 1000000 },
            WebSql = new WebSqlOptions { DefaultMaxRows = 1000, MaxAllowedRows = 10000 },
            RowLimits = new TransportRowLimitsOptions { FlightSql = new ChannelRowLimitOptions { MaxAllowedRows = 100000 } }
        };
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sql);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var wrapped = Options.Create(options);
        var server = new ArrowFlightSqlServer(
            new ArrowExportService(wrapped, NullLogger<ArrowExportService>.Instance),
            Substitute.For<ITableMetadataRepository>(),
            wrapped,
            NullLogger<ArrowFlightSqlServer>.Instance,
            scopeFactory: scopeFactory);
        return (server, sql);
    }

    private static async Task<List<RecordBatch>> StreamAsync(ArrowFlightSqlServer server, FlightSqlTicket ticket)
    {
        var batches = new List<RecordBatch>();
        await foreach (var batch in server.DoGetStreamAsync(ticket, User, Tenant))
        {
            batches.Add(batch);
        }

        return batches;
    }

    [Fact]
    public async Task Stream_ExecutesTheTicketQueryGoverned_WithFlightSqlRowLimit()
    {
        var (server, sql) = CreateServer();
        const string query = "SELECT id, name FROM lwetem_prod.md.crane LIMIT 5";
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult(query, query, ["id", "name"],
                [new Dictionary<string, object?> { ["id"] = 7L, ["name"] = "LTM 1300" }], 1, 1));

        var info = await server.GetFlightInfoAsync(query, User, Tenant);
        var batches = await StreamAsync(server, info.Ticket);

        var batch = batches.ShouldHaveSingleItem();
        batch.Length.ShouldBe(1);
        batch.Schema.FieldsList.Select(f => f.Name).ShouldBe(["id", "name"]);
        ((StringArray)batch.Column("name")).GetString(0).ShouldBe("LTM 1300");
        await sql.Received(1).ExecuteQueryBufferedAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.Sql == query && r.RowLimit == new SqlRowLimit(1000, 100000)),
            User, Tenant, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stream_RowLimit_IsCappedByArrowMaxExportRows()
    {
        var (server, sql) = CreateServer(new GatewayOptions
        {
            Arrow = new ArrowExportOptions { FlightTicketSigningKey = "0123456789abcdef0123456789abcdef", MaxExportRows = 500 },
            WebSql = new WebSqlOptions { DefaultMaxRows = 1000, MaxAllowedRows = 0 }
        });
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", "q", ["id"], [], 0, 1));

        var info = await server.GetFlightInfoAsync("SELECT id FROM t", User, Tenant);
        await StreamAsync(server, info.Ticket);

        await sql.Received(1).ExecuteQueryBufferedAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.RowLimit == new SqlRowLimit(500, 500)), User, Tenant, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stream_EmptyResult_KeepsTheResultColumns()
    {
        var (server, sql) = CreateServer();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", "q", ["id", "name"], [], 0, 1));

        var info = await server.GetFlightInfoAsync("SELECT id, name FROM t", User, Tenant);
        var batch = (await StreamAsync(server, info.Ticket)).ShouldHaveSingleItem();

        batch.Length.ShouldBe(0);
        batch.Schema.FieldsList.Select(f => f.Name).ShouldBe(["id", "name"]);
    }

    [Fact]
    public async Task Stream_TruncatedResult_IsMarkedInTheSchemaMetadata()
    {
        var (server, sql) = CreateServer();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", "q", ["id"], [new Dictionary<string, object?> { ["id"] = 1 }], 1, 1, Truncated: true));

        var info = await server.GetFlightInfoAsync("SELECT id FROM t LIMIT 1", User, Tenant);
        var batch = (await StreamAsync(server, info.Ticket)).ShouldHaveSingleItem();

        batch.Schema.Metadata[ArrowFlightSqlServer.TruncatedMetadataKey].ShouldBe("true");
    }

    [Fact]
    public async Task Stream_DeniedTable_IsRejected()
    {
        var (server, sql) = CreateServer();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<Task<GovernedSqlResult>>(_ => throw new WebSqlPolicyException("Access to table 'hr.salaries' is denied."));

        var info = await server.GetFlightInfoAsync("SELECT * FROM hr.salaries", User, Tenant);

        await Should.ThrowAsync<SecurityException>(() => StreamAsync(server, info.Ticket));
    }

    [Fact]
    public async Task FlightInfo_ValidatesTheStatementAndAnnouncesNoFakeSchema()
    {
        var (server, sql) = CreateServer();
        sql.RewriteSqlAsync("SELECT * FROM hr.salaries", User, Tenant, Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new WebSqlPolicyException("denied"));

        await Should.ThrowAsync<SecurityException>(() => server.GetFlightInfoAsync("SELECT * FROM hr.salaries", User, Tenant).AsTask());

        var info = await server.GetFlightInfoAsync("SELECT id FROM t", User, Tenant);
        info.Columns.ShouldBeEmpty();
        info.EstimatedRowCount.ShouldBe(-1);
        info.SchemaJson.ShouldNotContain("\"value\"");
    }

    [Fact]
    public async Task Stream_WithoutGovernedExecution_Returns501InsteadOfSampleRows()
    {
        var options = Options.Create(new GatewayOptions { Arrow = new ArrowExportOptions { FlightTicketSigningKey = "0123456789abcdef0123456789abcdef" } });
        var server = new ArrowFlightSqlServer(
            new ArrowExportService(options, NullLogger<ArrowExportService>.Instance), Substitute.For<ITableMetadataRepository>(), options, NullLogger<ArrowFlightSqlServer>.Instance);
        var info = await server.GetFlightInfoAsync("SELECT 1", User, Tenant);

        await Should.ThrowAsync<GatewayNotImplementedException>(() => StreamAsync(server, info.Ticket));
    }
}
