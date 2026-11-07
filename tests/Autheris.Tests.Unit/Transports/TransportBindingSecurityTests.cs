namespace Autheris.Tests.Unit.Transports;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Serialization;
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
}
