namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#138 INC-1 (P1): the one catalog visibility service. Section 5 of the plan plus SEC-CAT138-01..09, -13..17.</summary>
public sealed class CatalogVisibilityServiceTests
{
    private static readonly TableIdentifier T1 = new("sales", "dbo", "t1");
    private static readonly TableIdentifier T2 = new("sales", "dbo", "t2");

    private sealed class FakeRebac : IRebacEvaluator
    {
        private int _inFlight;
        private int _maxInFlight;
        public bool IsEnabled { get; set; } = true;
        public HashSet<string> Allowed { get; } = [];
        public bool AllowAll { get; set; }
        public Func<RebacBatchCheckRequest, RebacBatchCheckResult?>? Override { get; set; }
        public ConcurrentBag<RebacBatchCheckRequest> Requests { get; } = [];
        public int CheckCalls;
        public int MaxInFlight => _maxInFlight;
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public ValueTask<RebacCheckResult> CheckAsync(RebacCheckRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CheckCalls);
            return ValueTask.FromResult(RebacCheckResult.Denied);
        }

        public async ValueTask<RebacBatchCheckResult> BatchCheckAsync(RebacBatchCheckRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = _maxInFlight) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }

                ct.ThrowIfCancellationRequested();
                if (Override?.Invoke(request) is { } custom)
                {
                    return custom;
                }

                return new RebacBatchCheckResult(request.Checks.ToDictionary(c => c, c => AllowAll || Allowed.Contains(c.Object)));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void RegisterInheritance(string baseRelation, params string[] inheritedBy) { }

        public Task InvalidateTenantCacheAsync(string tenantId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger<CatalogVisibilityService>
    {
        public ConcurrentBag<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static ClaimsPrincipal Subject(string sid = "S-USER") => Wp0.User(sid, "tenant-a");

    private static TableMetadata Row(TableIdentifier id) => Wp0.Table(id.Domain, id.TableName, "Short " + id.TableName, ["id"]);

    private static TableMetadata[] Rows(int count) =>
        Enumerable.Range(0, count).Select(i => Row(new TableIdentifier("sales", "dbo", $"tab{i:D4}"))).ToArray();

    private static IConsentRepository ConsentsFor(IEnumerable<TableMetadata> tables, string sid = "S-USER") =>
        Wp0.Consents(tables.Select(t => Wp0.Allow(t.Identifier, sid, Wp0.TenantA, "id")).ToArray());

    private static GatewayOptions Opts(TenancyShape mode = TenancyShape.Multi, bool rebacEnabled = false) =>
        new()
        {
            Tenancy = mode.Options(),
            Catalog = new DataCatalogOptions { SharedTables = [] },
            Rebac = new RebacOptions { Enabled = rebacEnabled }
        };

    private static CatalogVisibilityService Svc(
        ITableMetadataRepository? tables, IConsentRepository? consents, IRebacEvaluator? rebac = null,
        GatewayOptions? options = null, ILogger<CatalogVisibilityService>? logger = null)
        => CatalogVisibilityTestFactory.Create(tables, consents, options ?? Opts(rebacEnabled: rebac != null), rebac, logger,
            omitEvaluator: rebac == null && options?.Rebac?.Enabled == true);

    private static CatalogCaller Caller(ClaimsPrincipal? p) => CatalogVisibilityTestFactory.Caller(p);

    // ------------------------------------------------------------------------------------------------------------------------------
    // SEC-CAT138-01 / T-CAT138-01
    // ------------------------------------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> DeniedPrincipals()
    {
        yield return ["null"];
        yield return ["unauthenticated"];
        yield return ["name-only-no-sid"];
        yield return ["anonymous-mcp-sid"];
        yield return ["invalid-tenant-claim"];
    }

    private static ClaimsPrincipal? DeniedPrincipal(string kind) => kind switch
    {
        "null" => null,
        "unauthenticated" => new ClaimsPrincipal(new ClaimsIdentity()),
        "name-only-no-sid" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant_id", "tenant-a")], "Bearer")),
        "anonymous-mcp-sid" => Wp0.User("ANONYMOUS_MCP_CLIENT", "tenant-a"),
        _ => Wp0.User("S-USER", "bad tenant!!")
    };

    [Theory]
    [MemberData(nameof(DeniedPrincipals))]
    [Trait("Category", "Security")]
    public async Task NullPrincipal_NoSid_AnonymousMcpSid_ScopeNone_ReturnEmpty_AllThreeMethods(string kind)
    {
        var tables = Wp0.Tables(Row(T1));
        var consents = Wp0.Consents(Wp0.Allow(T1, "S-USER", Wp0.TenantA, "id"));
        var rebac = new FakeRebac { AllowAll = true };
        var service = Svc(tables, consents, rebac);
        var denied = DeniedPrincipal(kind);
        var caller = kind == "invalid-tenant-claim" ? new CatalogCaller(denied, Wp0.TenantA, CatalogChannel.Rest) : Caller(denied);

        (await service.GetVisibleAsync(caller)).Tables.ShouldBeEmpty();
        (await service.FindVisibleAsync(caller, T1)).ShouldBeNull();
        (await service.FilterVisibleAsync(caller, [T1])).ShouldBeEmpty();

        await tables.DidNotReceiveWithAnyArgs().GetAllTablesAsync(default);
        await tables.DidNotReceiveWithAnyArgs().GetTableMetadataAsync(default, default);
        consents.ReceivedCalls().ShouldBeEmpty();
        rebac.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("global", "ClusterAdmin", "tenant-a")]
    [InlineData("tenant", "GovernanceAdmin", "tenant-a")]
    [InlineData("subject", "", "tenant-a")]
    [InlineData("single-unrestricted", "GovernanceAdmin", null)]
    [Trait("Category", "Security")]
    public async Task NameOnlyPrincipal_NoSid_Empty_AllClasses_NoRepositoryOrRebacCall(string label, string role, string? tenant)
    {
        _ = label;
        var claims = new List<Claim> { new(ClaimTypes.Name, "alice") };
        if (tenant != null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        if (role.Length > 0)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var tables = Wp0.Tables(Row(T1));
        var consents = Wp0.Consents();
        var rebac = new FakeRebac { AllowAll = true };
        var mode = tenant == null ? TenancyShape.Single : TenancyShape.Multi;
        var service = Svc(tables, consents, rebac, Opts(mode, rebacEnabled: true));

        CallerScopeResolver.Resolve(principal, mode.Owner()).ShouldBe(CallerScope.None);
        (await service.GetVisibleAsync(Caller(principal))).Tables.ShouldBeEmpty();
        (await service.FindVisibleAsync(Caller(principal), T1)).ShouldBeNull();
        (await service.FilterVisibleAsync(Caller(principal), [T1])).ShouldBeEmpty();
        await tables.DidNotReceiveWithAnyArgs().GetAllTablesAsync(default);
        rebac.Requests.ShouldBeEmpty();
        consents.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ClaimTypes.PrimarySid)]
    [InlineData("http://schemas.microsoft.com/identity/claims/objectidentifier")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    [InlineData("appid")]
    [InlineData("client_id")]
    [InlineData("azp")]
    [InlineData("cert-prefix")]
    [Trait("Category", "Security")]
    public async Task RealAuthPaths_CertForwardAuthBasicApiKey_HaveSid_CatalogNotEmpty(string claimType)
    {
        var sidValue = claimType == "cert-prefix" ? "cert:ab12cd" : "S-REAL";
        var type = claimType == "cert-prefix" ? ClaimTypes.PrimarySid : claimType;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(type, sidValue), new Claim("tenant_id", "tenant-a")], "Bearer"));
        var row = Row(T1);
        var consents = Wp0.Consents(Wp0.Allow(T1, principal.GetUserSid()!.Value.Value, Wp0.TenantA, "id"));

        var set = await Svc(Wp0.Tables(row), consents).GetVisibleAsync(Caller(principal));

        set.Tables.Select(t => t.Identifier).ShouldBe([T1]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MissingDependencies_AnswerEmpty()
    {
        var consents = Wp0.Consents(Wp0.Allow(T1, "S-USER", Wp0.TenantA, "id"));
        (await Svc(null, consents).GetVisibleAsync(Caller(Subject()))).Tables.ShouldBeEmpty();
        (await Svc(Wp0.Tables(Row(T1)), null).GetVisibleAsync(Caller(Subject()))).Tables.ShouldBeEmpty();
        (await CatalogVisibilityTestFactory.Create(Wp0.Tables(Row(T1)), consents, null).GetVisibleAsync(Caller(Subject()))).Tables.ShouldNotBeEmpty();
        var noOptions = new CatalogVisibilityService(Wp0.Tables(Row(T1)), consents, null);
        (await noOptions.GetVisibleAsync(Caller(Subject()))).Tables.ShouldBeEmpty();
        (await noOptions.FindVisibleAsync(Caller(Subject()), T1)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConsentRepositoryThrows_Empty_AllThreeMethods()
    {
        var tables = Wp0.Tables(Row(T1));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs<Task<IReadOnlyList<Consent>>>(_ => throw new InvalidOperationException("db down S-USER sales.dbo.t1"));
        var service = Svc(tables, consents);

        (await service.GetVisibleAsync(Caller(Subject()))).Tables.ShouldBeEmpty();
        (await service.FindVisibleAsync(Caller(Subject()), T1)).ShouldBeNull();
        (await service.FilterVisibleAsync(Caller(Subject()), [T1])).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FilterVisible_EmptyCandidates_ReturnsEmptyWithoutRepository()
    {
        var tables = Wp0.Tables(Row(T1));
        (await Svc(tables, Wp0.Consents()).FilterVisibleAsync(Caller(Subject()), [])).ShouldBeEmpty();
        await tables.DidNotReceiveWithAnyArgs().GetAllTablesAsync(default);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // ReBAC (SEC-CAT138-02..04, 06)
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RebacDisabled_EqualsConsentAndTierSet()
    {
        var rows = new[] { Row(T1), Row(T2) };
        var rebac = new FakeRebac { IsEnabled = false };

        var set = await Svc(Wp0.Tables(rows), ConsentsFor(rows), rebac, Opts()).GetVisibleAsync(Caller(Subject()));

        set.Tables.Select(t => t.Identifier).ShouldBe([T1, T2], ignoreOrder: true);
        rebac.Requests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RebacEnabled_IsIntersection_NeverUnion()
    {
        var rows = new[] { Row(T1), Row(T2) };
        var rebac = new FakeRebac();
        rebac.Allowed.Add(RebacTableGate.ObjectId(T1));
        rebac.Allowed.Add(RebacTableGate.ObjectId(new TableIdentifier("sales", "dbo", "unconsented")));

        // t1: consent and tuple; t2: consent only; "unconsented": tuple only (not in the candidate catalog of the caller).
        var all = rows.Append(Row(new TableIdentifier("sales", "dbo", "unconsented"))).ToArray();
        var set = await Svc(Wp0.Tables(all), ConsentsFor(rows), rebac).GetVisibleAsync(Caller(Subject()));

        set.Tables.Select(t => t.Identifier).ShouldBe([T1]);
    }

    [Theory]
    [InlineData("ClusterAdmin")]
    [InlineData("GovernanceAdmin")]
    [Trait("Category", "Security")]
    public async Task RebacAppliesToAdminClasses(string role)
    {
        var rows = new[] { Row(T1), Row(T2) };
        var rebac = new FakeRebac();
        rebac.Allowed.Add(RebacTableGate.ObjectId(T1));
        var admin = Wp0.User("S-ADMIN", "tenant-a", [role]);
        var consents = ConsentsFor(rows, "S-OTHER");

        var set = await Svc(Wp0.Tables(rows), consents, rebac).GetVisibleAsync(Caller(admin));

        set.Tables.Select(t => t.Identifier).ShouldBe([T1]);
        set.IsAdminView.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RebacEvaluatorNullOrDisabledResultMissingKey_Denies()
    {
        var rows = new[] { Row(T1) };
        var principal = Caller(Subject());

        // enabled by configuration but no evaluator resolved
        (await Svc(Wp0.Tables(rows), ConsentsFor(rows), null, Opts(rebacEnabled: true)).GetVisibleAsync(principal)).Tables.ShouldBeEmpty();
        (await Svc(Wp0.Tables(rows), ConsentsFor(rows), null, Opts(rebacEnabled: true)).FindVisibleAsync(principal, T1)).ShouldBeNull();

        // evaluator answers without the requested key
        var missing = new FakeRebac { Override = _ => new RebacBatchCheckResult(new Dictionary<RebacCheckRequest, bool>()) };
        (await Svc(Wp0.Tables(rows), ConsentsFor(rows), missing).GetVisibleAsync(principal)).Tables.ShouldBeEmpty();
        (await Svc(Wp0.Tables(rows), ConsentsFor(rows), missing).FindVisibleAsync(principal, T1)).ShouldBeNull();

        // evaluator disabled and configuration disabled: no ReBAC term
        var off = new FakeRebac { IsEnabled = false };
        (await Svc(Wp0.Tables(rows), ConsentsFor(rows), off, Opts(rebacEnabled: false)).GetVisibleAsync(principal)).Tables.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RebacChunkThrows_OnlyThatChunkHidden_CancellationPropagates_NoPartialMemo()
    {
        var rows = Rows(70); // chunks 32 / 32 / 6
        var first = rows[0];
        var calls = 0;
        var rebac = new FakeRebac
        {
            AllowAll = true,
            Override = req =>
            {
                if (req.Checks.Any(c => c.Object == RebacTableGate.ObjectId(first.Identifier)))
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("boom S-USER table:sales.dbo.tab0000");
                }

                return null;
            }
        };
        var service = Svc(Wp0.Tables(rows), ConsentsFor(rows), rebac);

        var set = await service.GetVisibleAsync(Caller(Subject()));

        set.Tables.Count.ShouldBe(70 - 32);
        set.Contains(first.Identifier).ShouldBeFalse();

        // cancellation is not a deny: it propagates and is never memoised as a (partial) set
        var cancelling = new FakeRebac { AllowAll = true, Delay = TimeSpan.FromSeconds(5) };
        var svc2 = Svc(Wp0.Tables(rows), ConsentsFor(rows), cancelling);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(async () => await svc2.GetVisibleAsync(Caller(Subject()), null, cts.Token));
        cancelling.Delay = TimeSpan.Zero;
        (await svc2.GetVisibleAsync(Caller(Subject()))).Tables.Count.ShouldBe(70);
    }

    [Fact]
    public async Task Cancellation_Propagates_BeforeAnyWork()
    {
        var rows = new[] { Row(T1) };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Svc(Wp0.Tables(rows), ConsentsFor(rows)).GetVisibleAsync(Caller(Subject()), null, cts.Token));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Batching_Chunk32_Parallel8_Enforced_RelationCandidatesCappedAndDeduped()
    {
        var rows = Rows(32 * 12);
        var rebac = new FakeRebac { AllowAll = true, Delay = TimeSpan.FromMilliseconds(25) };
        var service = Svc(Wp0.Tables(rows), ConsentsFor(rows), rebac);

        var set = await service.GetVisibleAsync(Caller(Subject()));

        set.Tables.Count.ShouldBe(rows.Length);
        rebac.Requests.Count.ShouldBe(12);
        rebac.Requests.Max(r => r.Checks.Count).ShouldBe(32);
        rebac.MaxInFlight.ShouldBeLessThanOrEqualTo(8);
        rebac.MaxInFlight.ShouldBeGreaterThan(1);
        rebac.CheckCalls.ShouldBe(0);

        // FilterVisible: duplicates collapse, anything beyond the cap of 256 is dropped (deny), not evaluated.
        var many = Rows(300);
        var rebac2 = new FakeRebac { AllowAll = true };
        var svc2 = Svc(Wp0.Tables(many), ConsentsFor(many), rebac2);
        var candidates = many.Select(t => t.Identifier).Concat(many.Take(10).Select(t => t.Identifier)).ToList();

        var visible = await svc2.FilterVisibleAsync(Caller(Subject()), candidates);

        visible.Count.ShouldBe(256);
        rebac2.Requests.Sum(r => r.Checks.Count).ShouldBe(256);
        visible.ShouldBe(many.Take(256).Select(t => t.Identifier), ignoreOrder: true);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UsesBatchCheck_RebacSubjectIsPrincipalSid_TenantIsCallerTenant_NoPerTableCheckAsync()
    {
        var rows = new[] { Row(T1) };
        var rebac = new FakeRebac { AllowAll = true };

        await Svc(Wp0.Tables(rows), ConsentsFor(rows), rebac).GetVisibleAsync(Caller(Subject()));

        var request = rebac.Requests.Single();
        request.TenantId.ShouldBe("tenant-a");
        var check = request.Checks.Single();
        check.User.ShouldBe("S-USER");
        check.Relation.ShouldBe(RebacTableGate.Relation);
        check.Object.ShouldBe(RebacTableGate.ObjectId(T1));
        check.TenantId.ShouldBe("tenant-a");
        rebac.CheckCalls.ShouldBe(0);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Memo (SEC-CAT138-05)
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SameScope_EvaluatesCatalogOnce()
    {
        var rows = new[] { Row(T1) };
        var tables = Wp0.Tables(rows);
        var service = Svc(tables, ConsentsFor(rows));
        var caller = Caller(Subject());

        var first = await service.GetVisibleAsync(caller);
        var second = await service.GetVisibleAsync(caller);

        second.ShouldBeSameAs(first);
        await tables.Received(1).GetAllTablesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Memo_KeyedBySidTenantContractSurface_NotReferenceOnly_ReplacedPrincipalReEvaluates()
    {
        var rows = new[] { Row(T1), Row(T2) };
        var consents = Wp0.Consents(Wp0.Allow(T1, "S-A", Wp0.TenantA, "id"), Wp0.Allow(T2, "S-B", Wp0.TenantA, "id"));
        var tables = Wp0.Tables(rows);
        var service = Svc(tables, consents);

        var a = await service.GetVisibleAsync(Caller(Subject("S-A")));
        var b = await service.GetVisibleAsync(Caller(Subject("S-B")));
        a.Tables.Select(t => t.Identifier).ShouldBe([T1]);
        b.Tables.Select(t => t.Identifier).ShouldBe([T2]);

        // same SID, new principal object -> same key; different tenant claim -> different key; contract -> different key
        var a2 = await service.GetVisibleAsync(Caller(Subject("S-A")));
        a2.ShouldBeSameAs(a);
        var otherTenant = await service.GetVisibleAsync(Caller(Wp0.User("S-A", "tenant-b")));
        otherTenant.Tables.ShouldBeEmpty();
        var contract = new SchemaContractDefinition("c1", allowedTables: ["sales.dbo.nothing"]);
        var withContract = await service.GetVisibleAsync(Caller(Subject("S-A")), contract);
        withContract.ShouldNotBeSameAs(a);
        withContract.Tables.ShouldBeEmpty();

        // same SID but another role / group set is not the same key
        var elevated = await service.GetVisibleAsync(Caller(Wp0.User("S-A", "tenant-a", ["GovernanceAdmin"])));
        elevated.ShouldNotBeSameAs(a);
        elevated.IsAdminView.ShouldBeTrue();
        a2.IsAdminView.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Memo_NotSharedAcrossServiceInstances()
    {
        var rows = new[] { Row(T1) };
        var tables = Wp0.Tables(rows);
        var consents = ConsentsFor(rows);

        await Svc(tables, consents).GetVisibleAsync(Caller(Subject()));
        await Svc(tables, consents).GetVisibleAsync(Caller(Subject()));

        await tables.Received(2).GetAllTablesAsync(Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Find / Filter
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Security")]
    public async Task FindVisible_UnknownHiddenInactiveRebacDenied_AllNull()
    {
        var inactive = Wp0.Table("sales", "inactive", active: false);
        var hidden = Row(new TableIdentifier("sales", "dbo", "nocons"));
        var denied = Row(T2);
        var ok = Row(T1);
        var rebac = new FakeRebac();
        rebac.Allowed.Add(RebacTableGate.ObjectId(T1));
        rebac.Allowed.Add(RebacTableGate.ObjectId(inactive.Identifier));
        var consents = Wp0.Consents(
            Wp0.Allow(ok.Identifier, "S-USER", Wp0.TenantA, "id"),
            Wp0.Allow(denied.Identifier, "S-USER", Wp0.TenantA, "id"),
            Wp0.Allow(inactive.Identifier, "S-USER", Wp0.TenantA, "id"));
        var service = Svc(Wp0.Tables(ok, hidden, denied, inactive), consents, rebac);
        var caller = Caller(Subject());

        (await service.FindVisibleAsync(caller, new TableIdentifier("sales", "dbo", "unknown"))).ShouldBeNull();
        (await service.FindVisibleAsync(caller, hidden.Identifier)).ShouldBeNull();
        (await service.FindVisibleAsync(caller, denied.Identifier)).ShouldBeNull();
        (await service.FindVisibleAsync(caller, inactive.Identifier)).ShouldBeNull();
        (await service.FindVisibleAsync(caller, ok.Identifier)).ShouldNotBeNull();
    }

    [Fact]
    public async Task FindVisible_OneRebacCheck()
    {
        var rows = Rows(5);
        var rebac = new FakeRebac { AllowAll = true };
        var service = Svc(Wp0.Tables(rows), ConsentsFor(rows), rebac);

        var row = await service.FindVisibleAsync(Caller(Subject()), rows[2].Identifier);

        row.ShouldNotBeNull();
        rebac.Requests.Single().Checks.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FilterVisible_ChecksCandidatesOnly()
    {
        var rows = Rows(10);
        var rebac = new FakeRebac { AllowAll = true };
        var service = Svc(Wp0.Tables(rows), ConsentsFor(rows), rebac);

        var visible = await service.FilterVisibleAsync(Caller(Subject()), [rows[1].Identifier, rows[3].Identifier, new TableIdentifier("x", "y", "z")]);

        visible.ShouldBe([rows[1].Identifier, rows[3].Identifier], ignoreOrder: true);
        rebac.Requests.Sum(r => r.Checks.Count).ShouldBe(2);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Shape (SEC-CAT138-13, -15)
    // ------------------------------------------------------------------------------------------------------------------------------

    private static TableMetadata WithView(string name, CatalogView view) => Row(new TableIdentifier("sales", "dbo", name)) with { CatalogView = view };

    [Fact]
    [Trait("Category", "Security")]
    public void CatalogVisibleRow_NeverCarriesNotFound()
    {
        var row = Row(T1);

        Should.Throw<ArgumentOutOfRangeException>(() => new CatalogVisibleRow(row, CatalogOutputShape.NotFound));
        Should.Throw<ArgumentOutOfRangeException>(() => new CatalogVisibleRow(row, (CatalogOutputShape)42));
        Should.Throw<ArgumentNullException>(() => new CatalogVisibleRow(null!, CatalogOutputShape.Full));
        new CatalogVisibleRow(row, CatalogOutputShape.Summary).Shape.ShouldBe(CatalogOutputShape.Summary);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Pipeline_NonAdminUnmarkedRow_DroppedBeforeRebacAndMemo()
    {
        var unmarked = WithView("unmarked_t", CatalogView.Unmarked);
        var unknown = WithView("unknown_t", (CatalogView)42);
        var granted = WithView("granted_t", CatalogView.Granted);
        var rebac = new FakeRebac { AllowAll = true };
        var service = CatalogVisibilityTestFactory.Create(
            Wp0.Tables(unmarked, unknown, granted), Wp0.Consents(), Opts(rebacEnabled: true), rebac,
            engineOutput: (_, _, _) => Task.FromResult<IReadOnlyList<TableMetadata>>([unmarked, unknown, granted]));

        var set = await service.GetVisibleAsync(Caller(Subject()));

        set.Tables.Select(t => t.Identifier.TableName).ShouldBe(["granted_t"]);
        set.ShapeOf(unmarked.Identifier).ShouldBe(CatalogOutputShape.NotFound);
        rebac.Requests.SelectMany(r => r.Checks).Select(c => c.Object).ShouldBe([RebacTableGate.ObjectId(granted.Identifier)]);
        (await service.FindVisibleAsync(Caller(Subject()), unmarked.Identifier)).ShouldBeNull();
        (await service.FilterVisibleAsync(Caller(Subject()), [unmarked.Identifier, unknown.Identifier])).ShouldBeEmpty();
        (await service.FindVisibleAsync(Caller(Subject()), granted.Identifier))!.Value.Shape.ShouldBe(CatalogOutputShape.Full);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SummaryRow_ShapeSummary_AllThreeMethods_KeepsCatalogViewSummary()
    {
        var summary = WithView("sum_t", CatalogView.Summary);
        var service = CatalogVisibilityTestFactory.Create(
            Wp0.Tables(summary), Wp0.Consents(), Opts(),
            engineOutput: (_, _, _) => Task.FromResult<IReadOnlyList<TableMetadata>>([summary]));
        var caller = Caller(Subject());

        var set = await service.GetVisibleAsync(caller);
        set.ShapeOf(summary.Identifier).ShouldBe(CatalogOutputShape.Summary);
        set.Find(summary.Identifier)!.CatalogView.ShouldBe(CatalogView.Summary);
        var found = await service.FindVisibleAsync(caller, summary.Identifier);
        found!.Value.Shape.ShouldBe(CatalogOutputShape.Summary);
        found.Value.Table.CatalogView.ShouldBe(CatalogView.Summary);
        (await service.FilterVisibleAsync(caller, [summary.Identifier])).ShouldBe([summary.Identifier]);
    }

    [Theory]
    [InlineData("ClusterAdmin", true)]
    [InlineData("GovernanceAdmin", true)]
    [InlineData("", false)]
    [Trait("Category", "Security")]
    public async Task Shape_ComesFromServiceResolver_NotRecomputedByChannel(string role, bool admin)
    {
        var row = Row(T1);
        var principal = Wp0.User("S-USER", "tenant-a", role.Length == 0 ? null : [role]);
        var consents = Wp0.Consents(Wp0.Allow(T1, "S-USER", Wp0.TenantA, "id"));

        var set = await Svc(Wp0.Tables(row), consents).GetVisibleAsync(Caller(principal));

        set.IsAdminView.ShouldBe(admin);
        set.IsAdminView.ShouldBe(CallerScopeResolver.IsAdminView(CallerScopeResolver.Resolve(principal, TenancyShape.Multi.Owner())));
        set.Tables.Count.ShouldBe(1);
        set.ShapeOf(T1).ShouldBe(CatalogOutputShape.Full);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Multi_Mode_NoTenantClaim_HasNoCatalogStanding()
    {
        var rows = new[] { Row(T1), Row(T2) };
        var consents = Wp0.Consents(Wp0.Allow(T1, "S-GOV", Autheris.Tests.Common.TestTenants.Default, "id"));
        var claimless = Wp0.User("S-GOV", null, ["GovernanceAdmin"]);

        var set = await Svc(Wp0.Tables(rows), consents, options: Opts(TenancyShape.Multi)).GetVisibleAsync(Caller(claimless));

        set.Scope.ShouldBe(CallerScope.None);
        set.IsAdminView.ShouldBeFalse();
        set.Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Single_Mode_GovernanceAdminWithoutTenant_CatalogOwner_SeesWholeCatalog()
    {
        var rows = new[] { Row(T1), Row(T2) };
        var admin = Wp0.User("S-GOV", null, ["GovernanceAdmin"]);

        var set = await Svc(Wp0.Tables(rows), Wp0.Consents(), options: Opts(TenancyShape.Single)).GetVisibleAsync(Caller(admin));

        set.Scope.ShouldBe(CallerScope.CatalogOwner);
        set.Tables.Count.ShouldBe(2);
        set.IsAdminView.ShouldBeTrue();
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Logs (SEC-CAT138-09, T-CAT138-13) and source guards (SEC-CAT138-17)
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Security")]
    public async Task Logs_Metrics_NoSidTenantTableName_OnChunkFailureAndDeny()
    {
        var rows = Rows(40);
        var logger = new CapturingLogger();
        var rebac = new FakeRebac
        {
            Override = req => throw new InvalidOperationException($"fail for S-USER tenant-a {req.Checks[0].Object}")
        };
        var service = Svc(Wp0.Tables(rows), ConsentsFor(rows), rebac, logger: logger);

        (await service.GetVisibleAsync(Caller(Subject()))).Tables.ShouldBeEmpty();

        logger.Entries.ShouldNotBeEmpty();
        foreach (var (_, message, exception) in logger.Entries)
        {
            exception.ShouldBeNull();
            message.ShouldNotContain("S-USER");
            message.ShouldNotContain("tenant-a");
            message.ShouldNotContain("tab0");
            message.ShouldNotContain("sales");
        }

        // dependency failure of the consent store: same rule
        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs<Task<IReadOnlyList<Consent>>>(_ => throw new InvalidOperationException("S-USER sales.dbo.tab0000"));
        var logger2 = new CapturingLogger();
        (await Svc(Wp0.Tables(rows), consents, logger: logger2).GetVisibleAsync(Caller(Subject()))).Tables.ShouldBeEmpty();
        logger2.Entries.ShouldNotBeEmpty();
        logger2.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("S-USER") && !e.Message.Contains("tab0000"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void EngineOutputForTesting_NotAssignedInProduction()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull();
        // Every non-test project (src, benchmarks, tools, samples): no assignment of the seam, whatever the spelling (`x.Seam = ...` or an initializer).
        var assignment = new System.Text.RegularExpressions.Regex(@"\bEngineOutputForTesting\s*=(?![=>])");
        var testRoot = $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}";
        var offenders = Directory.EnumerateFiles(current.FullName, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains(testRoot, StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .Where(f => assignment.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty();

        var property = typeof(CatalogVisibilityService).GetProperty("EngineOutputForTesting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property.ShouldNotBeNull();
        property.GetMethod!.IsAssembly.ShouldBeTrue();
        property.SetMethod!.IsAssembly.ShouldBeTrue();
    }
}
