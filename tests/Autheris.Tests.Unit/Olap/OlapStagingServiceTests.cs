namespace Autheris.Tests.Unit.Olap;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-07: table authorization, staging audit and engine call of the OLAP endpoint live in the use case.</summary>
public sealed class OlapStagingServiceTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableMetadata Meta = new() { Identifier = Orders, Columns = [new TableColumn { ColumnName = "id", DataType = "int" }] };

    private static SecurityPrincipalContext Caller() => new()
    {
        UserSid = new Sid("S-1-5-21-USER"),
        TenantId = new TenantId("tenant-1"),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer"
    };

    private sealed record Fixture(
        OlapStagingService Sut,
        IAuditWriter Audit,
        IDuckDbOlapEngine Engine,
        ICrossDomainAccessResolver Resolver,
        IAutherisConnectorRegistry Registry);

    private static Fixture Create(TableAccessDecision decision)
    {
        var meta = Substitute.For<ITableMetadataRepository>();
        meta.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Meta);
        var resolver = Substitute.For<ICrossDomainAccessResolver>();
        resolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(decision);
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        var engine = Substitute.For<IDuckDbOlapEngine>();
        engine.ExecuteOlapQueryAsync(Arg.Any<OlapQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OlapQueryResult(["x"], [new List<object?> { 1 }], 1, TimeSpan.FromMilliseconds(1)));
        var audit = Substitute.For<IAuditWriter>();
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowedDataSources = ["sales"] },
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true }
        });
        var sut = new OlapStagingService(engine, meta, registry, resolver, Substitute.For<IColumnMaskingProvider>(), options, audit, NullLogger<OlapStagingService>.Instance);
        return new Fixture(sut, audit, engine, resolver, registry);
    }

    private static OlapQueryCommand Command(params string[] tables) => new("SELECT 1", tables, null);

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("user_sid", "S-1-5-21-USER")], "Bearer"));

    [Fact]
    public async Task CallerWithoutTableAccess_IsDenied_AndNoStagingAuditIsWritten()
    {
        var f = Create(TableAccessDecision.Denied(Orders, "no grant"));

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command("sales.public.orders"), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        outcome.Error.ShouldBe("Access denied to table 'sales.public.orders'.");
        outcome.Result.ShouldBeNull();
        f.Registry.DidNotReceiveWithAnyArgs().TryGetConnectorForTable(default!, out _);
        await f.Audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
        await f.Engine.DidNotReceiveWithAnyArgs().ExecuteOlapQueryAsync(default!, default);
    }

    [Fact]
    public async Task AuditStoreDown_Returns503_NoRowsReturned()
    {
        var f = Create(TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        f.Audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new AuditUnavailableException("down"));

        // No staged table: the OLAP_QUERY audit is the first required write and runs after the engine call.
        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command(), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        outcome.Error.ShouldBe("Audit unavailable; the analytical request was rejected.");
        outcome.Result.ShouldBeNull();
    }

    [Fact]
    public async Task MissingTenant_IsForbidden()
    {
        var f = Create(TableAccessDecision.Denied(Orders));
        var caller = Caller() with { TenantId = default };

        var outcome = await f.Sut.ExecuteAsync(User(), caller, Command(), isDevelopment: false, "trace-1", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        outcome.Error.ShouldBe("Forbidden: Missing tenant identity.");
    }

    [Fact]
    public async Task Success_AuditsQueryWithTraceId_BeforeReturningTheResult()
    {
        var f = Create(TableAccessDecision.Denied(Orders));

        var outcome = await f.Sut.ExecuteAsync(User(), Caller(), Command(), isDevelopment: false, "trace-9", CancellationToken.None);

        outcome.StatusCode.ShouldBe(StatusCodes.Status200OK);
        outcome.Result!.TotalRowCount.ShouldBe(1);
        await f.Audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == "OLAP_QUERY" && e.TraceId == "trace-9" && e.Decision == "ALLOW"), Arg.Any<CancellationToken>());
    }
}
