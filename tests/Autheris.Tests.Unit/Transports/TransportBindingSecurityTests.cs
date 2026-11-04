namespace Autheris.Tests.Unit.Transports;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Application.Interfaces;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #447 WP-3, SEC-ATR-05 / REQ-ATR-10: a statement id or Flight ticket is only served over the ingress that created it, every page is
/// re-checked against the live source configuration, and the denial is audited. Statement manager and Flight server, no host.
/// </summary>
[Trait("Category", "Security")]
public sealed class TransportBindingSecurityTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly Sid Owner = new("user-abc");

    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "user-abc"), new Claim(ClaimTypes.Name, "Alice"), DefaultTenantCaller.Claim], "TestAuth"));

    private sealed class Harness
    {
        public IDataSourceTransportGate Gate { get; } = Substitute.For<IDataSourceTransportGate>();

        public ITransportDenialRecorder Recorder { get; } = Substitute.For<ITransportDenialRecorder>();

        public IGovernedSqlExecutionService Sql { get; } = Substitute.For<IGovernedSqlExecutionService>();

        public IServiceScopeFactory ScopeFactory { get; } = Substitute.For<IServiceScopeFactory>();

        public List<DataSourceTransportFlags> Stamped { get; } = [];

        public Harness()
        {
            var provider = Substitute.For<IServiceProvider>();
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(provider);
            ScopeFactory.CreateScope().Returns(scope);

            var context = Substitute.For<IIngressTransportContext>();
            context.When(c => c.Set(Arg.Any<DataSourceTransportFlags>())).Do(call => Stamped.Add(call.Arg<DataSourceTransportFlags>()));
            provider.GetService(typeof(IIngressTransportContext)).Returns(context);
            provider.GetService(typeof(IDataSourceTransportGate)).Returns(Gate);
            provider.GetService(typeof(ITransportDenialRecorder)).Returns(Recorder);
            provider.GetService(typeof(IGovernedSqlExecutionService)).Returns(Sql);
            Gate.CheckedSources.Returns(["src_s"]);
            Sql.ExecuteQueryBufferedAsync(default!, default!, default, default).ReturnsForAnyArgs(
                Task.FromResult(new GovernedSqlResult("SELECT 1", ["c"], [new Dictionary<string, object?> { ["c"] = 1 }], 1, 1)));
        }
    }

    [Fact]
    public async Task WebSql_StatementIdOnTrinoRoute_Denied()
    {
        var h = new Harness();
        using var manager = new WebSqlStatementManager(h.ScopeFactory, NullLogger<WebSqlStatementManager>.Instance, retentionPeriod: TimeSpan.FromMinutes(5));
        var submitted = await manager.SubmitOrWaitAsync(
            new GovernedSqlQueryRequest("SELECT 1"), User, Owner, Tenant, TimeSpan.FromSeconds(2), DataSourceTransportFlags.WebSql);
        submitted.State.ShouldBe("FINISHED");

        var fetched = await manager.GetStatusOrWaitAsync(submitted.StatementId, User, Owner, Tenant, TimeSpan.Zero, DataSourceTransportFlags.Trino);

        fetched.State.ShouldBe("FAILED");
        fetched.ErrorCode.ShouldBe("TRANSPORT_NOT_ALLOWED");
        fetched.Data.ShouldBeNull();
        await h.Recorder.Received(1).RecordDenialAsync(
            Arg.Is<TransportDenial>(d => d.Reason == TransportDecisionReason.IngressMismatch && d.Transport == DataSourceTransportFlags.Trino),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trino_PageAfterReloadDenies()
    {
        var h = new Harness();
        using var manager = new WebSqlStatementManager(h.ScopeFactory, NullLogger<WebSqlStatementManager>.Instance, retentionPeriod: TimeSpan.FromMinutes(5));
        var submitted = await manager.SubmitOrWaitAsync(
            new GovernedSqlQueryRequest("SELECT 1"), User, Owner, Tenant, TimeSpan.FromSeconds(2), DataSourceTransportFlags.Trino);

        // the reload removed Trino from the source: the live gate refuses the sources the statement used, under the stored ingress
        h.Gate.EnsureCatalogSourcesAsync(Arg.Any<IEnumerable<string?>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new TransportNotAllowedException("src_s", DataSourceTransportFlags.Trino)));

        var page = await manager.GetStatusOrWaitAsync(submitted.StatementId, User, Owner, Tenant, TimeSpan.Zero, DataSourceTransportFlags.Trino);

        page.State.ShouldBe("FAILED");
        page.ErrorCode.ShouldBe("TRANSPORT_NOT_ALLOWED");
        page.Data.ShouldBeNull();
        await h.Gate.Received().EnsureCatalogSourcesAsync(
            Arg.Is<IEnumerable<string?>>(names => names.SequenceEqual(new string?[] { "src_s" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatementManager_BackgroundScopeCarriesSubmittingIngress_AndUnstampedSubmitStaysUnset()
    {
        var h = new Harness();
        using var manager = new WebSqlStatementManager(h.ScopeFactory, NullLogger<WebSqlStatementManager>.Instance, retentionPeriod: TimeSpan.FromMinutes(5));

        await manager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 1"), User, Owner, Tenant, TimeSpan.FromSeconds(2), DataSourceTransportFlags.Trino);
        h.Stamped.ShouldBe([DataSourceTransportFlags.Trino]);

        h.Stamped.Clear();
        await manager.SubmitOrWaitAsync(new GovernedSqlQueryRequest("SELECT 2"), User, Owner, Tenant, TimeSpan.FromSeconds(2));
        h.Stamped.ShouldBeEmpty(); // None is never stamped: the gate denies the unset scope
    }

    private const string SigningKey = "unit-test-flight-ticket-signing-key-0123456789";

    private static ArrowFlightSqlServer FlightServer(IServiceScopeFactory? scopes) => new(
        Substitute.For<IArrowExportService>(),
        Substitute.For<ITableMetadataRepository>(),
        Options.Create(new GatewayOptions { Arrow = new ArrowExportOptions { FlightTicketSigningKey = SigningKey } }),
        NullLogger<ArrowFlightSqlServer>.Instance,
        scopeFactory: scopes);

    private static ClaimsPrincipal FlightUser() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "u"), new Claim(ClaimTypes.PrimarySid, "user-abc"), new Claim("tid", "tenant-1")], "Bearer"));

    [Fact]
    public async Task Flight_TicketOnRestRoute_Denied()
    {
        var h = new Harness();
        var server = FlightServer(h.ScopeFactory);
        var info = await server.GetFlightInfoAsync("SELECT 1", FlightUser(), Tenant);
        info.Ticket.Ingress.ShouldBe(DataSourceTransportFlags.ArrowFlight);

        foreach (var wrongRoute in new[] { DataSourceTransportFlags.Rest, DataSourceTransportFlags.WebSql, DataSourceTransportFlags.Mcp, DataSourceTransportFlags.None })
        {
            await Should.ThrowAsync<TransportNotAllowedException>(async () =>
            {
                await foreach (var _ in server.DoGetStreamAsync(info.Ticket, FlightUser(), Tenant, wrongRoute))
                {
                }
            });
        }

        await h.Sql.DidNotReceiveWithAnyArgs().ExecuteQueryBufferedAsync(default!, default!, default, default);
        await h.Recorder.Received(4).RecordDenialAsync(
            Arg.Is<TransportDenial>(d => d.Reason == TransportDecisionReason.IngressMismatch), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Flight_TicketWithoutIngress_Denied_AndIngressIsPartOfTheSignature()
    {
        var h = new Harness();
        var server = FlightServer(h.ScopeFactory);
        var info = await server.GetFlightInfoAsync("SELECT 1", FlightUser(), Tenant);

        // a ticket whose ingress was removed or changed no longer verifies (signature)
        var stripped = info.Ticket with { Ingress = DataSourceTransportFlags.None };
        await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(stripped, FlightUser(), Tenant))
            {
            }
        });

        // a validly signed ticket that carries no ingress (issued before the binding existed) is never served
        var now = DateTimeOffset.UtcNow;
        var unbound = new FlightSqlTicket(
            "t-old", "tenant-1", "SELECT 1", now,
            ArrowFlightSqlServer.ComputeSignature("t-old", "tenant-1", "SELECT 1", now, SigningKey, "user-abc"), "user-abc");
        await Should.ThrowAsync<TransportNotAllowedException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(unbound, FlightUser(), Tenant))
            {
            }
        });

        var retargeted = info.Ticket with { Ingress = DataSourceTransportFlags.Rest };
        await Should.ThrowAsync<System.Security.SecurityException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(retargeted, FlightUser(), Tenant, DataSourceTransportFlags.Rest))
            {
            }
        });
    }

    [Fact]
    public async Task Flight_TicketRedeemOnTheIssuingRoute_RunsTheGovernedPipeline()
    {
        var h = new Harness();
        var server = FlightServer(h.ScopeFactory);
        var info = await server.GetFlightInfoAsync("SELECT 1", FlightUser(), Tenant);

        // positive control: the issuing ingress reaches the governed pipeline (the choke points then re-check the live configuration)
        await foreach (var _ in server.DoGetStreamAsync(info.Ticket, FlightUser(), Tenant, DataSourceTransportFlags.ArrowFlight))
        {
        }

        await h.Sql.ReceivedWithAnyArgs(1).ExecuteQueryBufferedAsync(default!, default!, default, default);
        h.Stamped.ShouldContain(DataSourceTransportFlags.ArrowFlight);
    }
}
