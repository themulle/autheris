namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System.Security.Claims;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#138 INC-1 P1 review follow-ups: bounded reads, fail-closed key computation, timeout classification, DI lifetime.</summary>
[Trait("Category", "Security")]
public sealed class CatalogVisibilityServiceHardeningTests
{
    private static GatewayOptions Opts(bool rebac = false) => new()
    {
        Tenancy = TenancyShape.Multi.Options(),
        Catalog = new DataCatalogOptions { SharedTables = [] },
        Rebac = new RebacOptions { Enabled = rebac }
    };

    private static TableMetadata[] Rows(int count) =>
        Enumerable.Range(0, count).Select(i => Wp0.Table("sales", $"tab{i:D5}", "t", ["id"])).ToArray();

    private static IConsentRepository ConsentsFor(IEnumerable<TableMetadata> rows) =>
        Wp0.Consents(rows.Select(t => Wp0.Allow(t.Identifier, "S-USER", Wp0.TenantA, "id")).ToArray());

    private static CatalogCaller Caller(ClaimsPrincipal p) => CatalogVisibilityTestFactory.Caller(p);

    // ------------------------------------------------------------------------------------------------------------------------------
    // Review 1: FilterVisibleAsync reads only the candidates
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FilterVisible_LargeCatalog_ReadsOnlyCandidates_NoGetAllTablesCall()
    {
        var rows = Rows(5000);
        var tables = Wp0.Tables(rows);
        var service = CatalogVisibilityTestFactory.Create(tables, ConsentsFor(rows), Opts());
        var wanted = new[] { rows[3].Identifier, rows[4100].Identifier, new TableIdentifier("sales", "dbo", "unknown") };

        var visible = await service.FilterVisibleAsync(Caller(Wp0.User("S-USER", "tenant-a")), wanted);

        visible.OrderBy(t => t.TableName).ShouldBe([rows[3].Identifier, rows[4100].Identifier]);
        await tables.DidNotReceiveWithAnyArgs().GetAllTablesAsync(default);
        await tables.Received(3).GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FilterVisible_AfterGetVisible_UsesTheMemo_NoSecondRead()
    {
        var rows = Rows(20);
        var tables = Wp0.Tables(rows);
        var service = CatalogVisibilityTestFactory.Create(tables, ConsentsFor(rows), Opts());
        var caller = Caller(Wp0.User("S-USER", "tenant-a"));
        await service.GetVisibleAsync(caller);
        tables.ClearReceivedCalls();

        (await service.FilterVisibleAsync(caller, [rows[1].Identifier])).ShouldBe([rows[1].Identifier]);

        tables.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task FilterVisible_InactiveAndRepositoryFailure_AreDenied_OthersKept()
    {
        var ok = Wp0.Table("sales", "ok_t");
        var inactive = Wp0.Table("sales", "inactive_t", active: false);
        var boom = Wp0.Table("sales", "boom_t");
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(ok.Identifier, Arg.Any<CancellationToken>()).Returns(ok);
        tables.GetTableMetadataAsync(inactive.Identifier, Arg.Any<CancellationToken>()).Returns(inactive);
        tables.GetTableMetadataAsync(boom.Identifier, Arg.Any<CancellationToken>())
            .Returns<Task<TableMetadata?>>(_ => throw new InvalidOperationException("db"));
        var service = CatalogVisibilityTestFactory.Create(tables, ConsentsFor([ok, inactive, boom]), Opts());

        var visible = await service.FilterVisibleAsync(Caller(Wp0.User("S-USER", "tenant-a")), [ok.Identifier, inactive.Identifier, boom.Identifier]);

        visible.ShouldBe([ok.Identifier]);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Review 2: an invalid tenant claim never escapes as an exception
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidTenantClaim_ClusterAdmin_AllThreeMethods_Empty_NoThrow()
    {
        var rows = Rows(2);
        var service = CatalogVisibilityTestFactory.Create(Wp0.Tables(rows), ConsentsFor(rows), Opts());
        var admin = Wp0.User("S-ADMIN", "bad tenant!!", ["ClusterAdmin"]);
        var caller = new CatalogCaller(admin, Wp0.TenantA, CatalogChannel.Mcp);

        (await service.GetVisibleAsync(caller)).Tables.ShouldBeEmpty();
        (await service.FindVisibleAsync(caller, rows[0].Identifier)).ShouldBeNull();
        (await service.FilterVisibleAsync(caller, [rows[0].Identifier])).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Review 3: a timeout (TaskCanceledException with an uncancelled token) is a failure, not a cancellation
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EvaluatorTimeout_TaskCanceled_TokenNotCancelled_ChunkHidden_NoThrow()
    {
        var rows = Rows(3);
        var rebac = Substitute.For<IRebacEvaluator>();
        rebac.IsEnabled.Returns(true);
        rebac.BatchCheckAsync(Arg.Any<RebacBatchCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<RebacBatchCheckResult>>(_ => throw new TaskCanceledException("evaluator timeout"));
        var service = CatalogVisibilityTestFactory.Create(Wp0.Tables(rows), ConsentsFor(rows), Opts(rebac: true), rebac);
        var caller = Caller(Wp0.User("S-USER", "tenant-a"));

        (await service.GetVisibleAsync(caller)).Tables.ShouldBeEmpty();
        (await service.FindVisibleAsync(caller, rows[0].Identifier)).ShouldBeNull();
    }

    [Fact]
    public async Task ConsentRepositoryTimeout_TaskCanceled_TokenNotCancelled_Empty_NoThrow()
    {
        var rows = Rows(2);
        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Consent>>>(_ => throw new TaskCanceledException("db timeout"));
        var service = CatalogVisibilityTestFactory.Create(Wp0.Tables(rows), consents, Opts());

        (await service.GetVisibleAsync(Caller(Wp0.User("S-USER", "tenant-a")))).Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task RealCancellation_StillPropagates()
    {
        var rows = Rows(2);
        var rebac = Substitute.For<IRebacEvaluator>();
        rebac.IsEnabled.Returns(true);
        using var cts = new CancellationTokenSource();
        rebac.BatchCheckAsync(Arg.Any<RebacBatchCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<RebacBatchCheckResult>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        var service = CatalogVisibilityTestFactory.Create(Wp0.Tables(rows), ConsentsFor(rows), Opts(rebac: true), rebac);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await service.GetVisibleAsync(Caller(Wp0.User("S-USER", "tenant-a")), contract: null, cts.Token));
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Review 6: the chunk failure log carries the exception type only
    // ------------------------------------------------------------------------------------------------------------------------------

    private sealed class Capture : ILogger<CatalogVisibilityService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception) + (exception == null ? string.Empty : "|EXCEPTION"));
            }
        }
    }

    [Fact]
    public async Task ChunkFailure_Log_NamesTheFailureType_NoMessageNoException()
    {
        var rows = Rows(2);
        var rebac = Substitute.For<IRebacEvaluator>();
        rebac.IsEnabled.Returns(true);
        rebac.BatchCheckAsync(Arg.Any<RebacBatchCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<RebacBatchCheckResult>>(_ => throw new InvalidOperationException("S-USER sales.dbo.tab00000"));
        var log = new Capture();
        var service = CatalogVisibilityTestFactory.Create(Wp0.Tables(rows), ConsentsFor(rows), Opts(rebac: true), rebac, log);

        (await service.GetVisibleAsync(Caller(Wp0.User("S-USER", "tenant-a")))).Tables.ShouldBeEmpty();

        log.Messages.ShouldContain(m => m.Contains("InvalidOperationException", StringComparison.Ordinal));
        log.Messages.ShouldAllBe(m => !m.Contains("S-USER") && !m.Contains("tab0000") && !m.Contains("|EXCEPTION"));
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Review 10: scoped only
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Service_IsScoped_CannotBeResolvedFromTheRootProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<IAuditLogRepository>());
        services.AddSingleton(Wp0.Tables());
        services.AddSingleton(Wp0.Consents());
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(Opts()));
        services.AddAutherisCatalog();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ICatalogVisibilityService>());
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICatalogVisibilityService>().ShouldBeOfType<CatalogVisibilityService>();
        scope.ServiceProvider.GetRequiredService<ICatalogVisibilityService>()
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<ICatalogVisibilityService>());
    }
}
