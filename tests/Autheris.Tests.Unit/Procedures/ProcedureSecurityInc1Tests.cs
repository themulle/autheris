using System.Data.Common;
using System.Diagnostics;
using System.Security;
using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Procedures;

/// <summary>PLAN-PROC-152-INC1: governed procedures INC-1 security slice (SEC-PROC152-01..11).</summary>
public class ProcedureSecurityInc1Tests
{
    private const string NotFound = "Procedure endpoint not found.";
    private static readonly TenantId Tenant = new("tenant-a");

    private const string Header = """
        -- @name get_orders
        -- @procedure api.usp_GetOrders
        -- @param customer_id int required Customer number
        -- @param note nvarchar(40) optional Free text
        -- @result-table sales.orders
        """;

    // ---------- test doubles ----------

    private sealed class CountingGate : ITableReadConcurrencyGate
    {
        private int _outstanding;
        public int Outstanding => Volatile.Read(ref _outstanding);
        public List<(string Key, int Max)> Calls { get; } = [];
        public bool DenyAll { get; set; }
        public int? DenyOnCall { get; set; }

        public IDisposable? TryEnter(string key, int maxConcurrent)
        {
            Calls.Add((key, maxConcurrent));
            if (DenyAll || DenyOnCall == Calls.Count)
            {
                return null;
            }

            Interlocked.Increment(ref _outstanding);
            return new Lease(this);
        }

        private sealed class Lease(CountingGate owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._outstanding);
                }
            }
        }
    }

    private sealed class CapturingLogger : ILogger<GovernedProcedureExecutionService>, ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));

        public string AllText => string.Join("\n", Entries.Select(e => e.Message + "|" + e.Exception));
    }

    private sealed class FakeSqlException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class Env
    {
        public InMemoryProcedureRegistry Registry { get; } = new();
        public IProcedureInvoker Invoker { get; } = Substitute.For<IProcedureInvoker>();
        public ITableMetadataRepository Tables { get; } = Substitute.For<ITableMetadataRepository>();
        public IConsentRepository Consents { get; } = Substitute.For<IConsentRepository>();
        public IConsentResolutionService Resolution { get; } = Substitute.For<IConsentResolutionService>();
        public IColumnMaskingProvider Masking { get; } = Substitute.For<IColumnMaskingProvider>();
        public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();
        public List<AuditLogEntry> AuditEntries { get; } = [];
        public CountingGate Gate { get; } = new();
        public bool UseGate { get; set; } = true;
        public CapturingLogger Logger { get; } = new();
        public GatewayOptions Options { get; set; } = new();
        public ProcedureDenialAuditLimiter? Limiter { get; set; }
        public IPolicyEnforcementService PolicyEnforcement { get; set; } = global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance;
        public Func<string, ColumnAccessLevel, TableAccessDecision>? Decision { get; set; }

        private bool _auditWired;

        public GovernedProcedureExecutionService Create()
        {
            if (!_auditWired)
            {
                _auditWired = true;
                Audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(AuditEntries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            }

            return new GovernedProcedureExecutionService(
                Registry, Invoker, Microsoft.Extensions.Options.Options.Create(Options), Audit, Tables, Consents, Resolution, Masking,
                policyEnforcement: PolicyEnforcement,
                logger: Logger,
                concurrencyGate: UseGate ? Gate : null,
                denialAuditLimiter: Limiter);
        }
    }

    private static ClaimsPrincipal User(params string[] roles) => Principal(ClaimTypes.PrimarySid, "S-1-5-21-1001", roles);

    private static ClaimsPrincipal Principal(string claimType, string value, params string[] roles)
    {
        var claims = new List<Claim> { new(claimType, value) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims.Append(DefaultTenantCaller.Claim), "test"));
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static readonly Dictionary<string, ColumnAccessLevel> DefaultColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["order_id"] = ColumnAccessLevel.Clear,
        ["secret"] = ColumnAccessLevel.Deny,
        ["salary"] = ColumnAccessLevel.Mask
    };

    private static Env Active(
        Func<ProcedureDefinition, ProcedureDefinition>? tweak = null,
        string[]? tables = null,
        bool markActive = true)
    {
        var e = new Env();
        var def = ProcedureDefinitionParser.Parse(Header, "x", false, 60);
        if (tweak != null)
        {
            def = tweak(def);
        }

        e.Registry.Register(def);
        if (markActive)
        {
            e.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["order_id", "secret", "salary", "extra"], tables ?? ["sales.orders"], new Dictionary<string, string>()));
        }

        e.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci => new TableMetadata
        {
            Identifier = ci.Arg<TableIdentifier>(),
            Columns = [new TableColumn { ColumnName = "order_id" }, new TableColumn { ColumnName = "secret" }, new TableColumn { ColumnName = "salary" }]
        });
        e.Consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());
        e.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(DefaultColumns, StringComparer.OrdinalIgnoreCase)));
        e.Masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");
        e.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(
                ["order_id", "secret", "salary", "extra"],
                [new object?[] { 1, "s", 1000m, "x" }, new object?[] { 2, "t", 2000m, "y" }],
                false));
        return e;
    }

    private static Dictionary<string, object?> Valid() => new() { ["customer_id"] = 7 };

    private static Task<GovernedProcedureResult> Run(Env e, string name = "get_orders", Dictionary<string, object?>? inputs = null, ClaimsPrincipal? user = null, CancellationToken ct = default) =>
        e.Create().ExecuteAsync(name, inputs ?? Valid(), user ?? User(), Tenant, ct);

    private static void DenyConsent(Env e) =>
        e.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Denied(ci.ArgAt<TableIdentifier>(3), "no consent"));

    private static GatewayOptions Bypass() => new() { Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true } };

    // ---------- WP-1: visibility stage ----------

    [Fact]
    public async Task Execute_UnknownRoleConsentNoTablesNoSid_AllThrowSameNotFound()
    {
        var messages = new List<string>();

        // unknown
        messages.Add((await Should.ThrowAsync<KeyNotFoundException>(() => Run(Active(), "nope"))).Message);
        // role
        messages.Add((await Should.ThrowAsync<KeyNotFoundException>(() => Run(Active(d => d with { RequiredRoles = ["r"] }), user: User("other")))).Message);
        // consent
        var denied = Active();
        DenyConsent(denied);
        messages.Add((await Should.ThrowAsync<KeyNotFoundException>(() => Run(denied))).Message);
        // no tables
        messages.Add((await Should.ThrowAsync<KeyNotFoundException>(() => Run(Active(d => d with { ResultTable = null }, tables: [])))).Message);
        // no SID
        messages.Add((await Should.ThrowAsync<KeyNotFoundException>(() => Run(Active(), user: Anonymous()))).Message);
        // unavailable and invisible
        messages.Add((await Should.ThrowAsync<KeyNotFoundException>(() => Run(Active(d => d with { RequiredRoles = ["r"] }, markActive: false), user: User("other")))).Message);

        messages.ShouldAllBe(m => m == NotFound);
    }

    [Fact]
    public async Task Execute_InvalidParameters_WithoutVisibility_ThrowsNotFound_NotArgument()
    {
        var e = Active(d => d with { RequiredRoles = ["r"] });
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e, inputs: new Dictionary<string, object?> { ["bogus"] = "secret-value" }, user: User("other")));
    }

    [Fact]
    public async Task Execute_Unavailable_Invisible404_Visible503()
    {
        var invisible = Active(d => d with { RequiredRoles = ["r"] }, markActive: false);
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(invisible, user: User("other")));

        var visible = Active(markActive: false);
        await Should.ThrowAsync<ProcedureUnavailableException>(() => Run(visible));
    }

    [Fact]
    public async Task Execute_CatalogModeWithoutTables_IsNotVisible()
    {
        var e = Active(d => d with { ResultTable = null }, tables: []);
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e));
        await e.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_VisibilityStageThrows_IsNotFound_AndAuditsVisibilityError()
    {
        var e = Active();
        e.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns<Task<TableMetadata?>>(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e));
        e.AuditEntries.ShouldContain(a => a.EventType == "PROCEDURE_DENIED" && a.DetailsJson.Contains("visibility-error"));
        e.Logger.Entries.ShouldContain(l => l.Level == LogLevel.Error && l.Message.Contains("InvalidOperationException"));
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("gateway-security")]
    [InlineData("invalid-operation")]
    [InlineData("key-not-found")]
    [InlineData("cancellation")]
    [Trait("Category", "Security")]
    public async Task T_PROC152_02_VisibilityStage_AnyExceptionKind_Returns404_NotThrownToRoute(string kind)
    {
        var e = Active();
        Exception ex = kind switch
        {
            "argument" => new ArgumentException("param-text-leak"),
            "gateway-security" => new GatewaySecurityException("incomplete filter on dbo.Secret", "VF"),
            "invalid-operation" => new InvalidOperationException("policy missing"),
            "key-not-found" => new KeyNotFoundException("inner"),
            _ => new OperationCanceledException()
        };
        e.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns<Task<TableMetadata?>>(_ => throw ex);

        if (kind == "cancellation")
        {
            await Should.ThrowAsync<OperationCanceledException>(() => Run(e));
            e.AuditEntries.ShouldBeEmpty(); // not audited as a denial
        }
        else
        {
            var thrown = await Should.ThrowAsync<KeyNotFoundException>(() => Run(e));
            thrown.Message.ShouldBe(NotFound);
            e.AuditEntries.ShouldContain(a => a.DetailsJson.Contains("visibility-error"));
        }

        await e.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
    }

    [Theory]
    [InlineData("invisible-unavailable")]      // 404, never 503
    [InlineData("invisible-throttled")]        // 404, never 429 (gate untouched)
    [InlineData("invisible-invalid-params")]   // 404, never 400
    [InlineData("visible-unavailable")]        // 503
    [InlineData("visible-throttled-invalid")]  // 429 before 400
    [InlineData("visible-invalid")]            // 400
    [Trait("Category", "Security")]
    public async Task T_PROC152_03_Order_Invisible_NeverSees503_429_400_Visible_SeesInOrder(string stage)
    {
        var bad = new Dictionary<string, object?> { ["bogus"] = "x" };
        switch (stage)
        {
            case "invisible-unavailable":
                await Should.ThrowAsync<KeyNotFoundException>(() => Run(Active(d => d with { RequiredRoles = ["r"] }, markActive: false), user: User("o")));
                break;
            case "invisible-throttled":
            {
                var e = Active(d => d with { RequiredRoles = ["r"] });
                e.Gate.DenyAll = true;
                await Should.ThrowAsync<KeyNotFoundException>(() => Run(e, user: User("o")));
                e.Gate.Calls.ShouldBeEmpty();
                break;
            }
            case "invisible-invalid-params":
                await Should.ThrowAsync<KeyNotFoundException>(() => Run(Active(d => d with { RequiredRoles = ["r"] }), inputs: bad, user: User("o")));
                break;
            case "visible-unavailable":
                await Should.ThrowAsync<ProcedureUnavailableException>(() => Run(Active(markActive: false)));
                break;
            case "visible-throttled-invalid":
            {
                var e = Active();
                e.Gate.DenyAll = true;
                await Should.ThrowAsync<GatewayThrottledException>(() => Run(e, inputs: bad));
                break;
            }
            default:
                await Should.ThrowAsync<ArgumentException>(() => Run(Active(), inputs: bad));
                break;
        }
    }

    [Fact]
    public async Task Execute_RowScopeKeyUnusable_Throws503()
    {
        // result table has a consent row filter but the procedure declares no scope key: visibility denies (404)
        var e = Active(d => d with { RowScopeKey = ["order_id"], RowScopeKeyTable = ["order_id"] });
        e.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(DefaultColumns, StringComparer.OrdinalIgnoreCase), "region = 'EMEA'"));
        var rowScope = Substitute.For<IProcedureRowScopeResolver>();
        var audit = e.Audit;
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e.AuditEntries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var svc = new GovernedProcedureExecutionService(
            e.Registry, e.Invoker, Microsoft.Extensions.Options.Options.Create(e.Options), audit, e.Tables, e.Consents, e.Resolution, e.Masking,
            policyEnforcement: e.PolicyEnforcement, rowScope: rowScope, concurrencyGate: e.Gate);

        // the key column "order_id" is not in the result columns -> unusable key after visibility -> 503
        e.Invoker.ExecuteReadAsync(default!, default!, default!, default, default).ReturnsForAnyArgs(new RawProcedureResult(["salary"], [new object?[] { 1m }], false));
        await Should.ThrowAsync<ProcedureUnavailableException>(() => svc.ExecuteAsync("get_orders", Valid(), User(), Tenant));
        e.Gate.Outstanding.ShouldBe(0);
    }

    // ---------- audit of unknown names (SEC-PROC152-04/05) ----------

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_04_UnknownName_Anonymous_NotAudited_Authenticated_AuditedAndRateLimited_OneWritePerCall()
    {
        // anonymous (consent bypass) caller: not audited
        var anon = Active();
        anon.Options = Bypass();
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(anon, "nope", user: Anonymous()));
        anon.AuditEntries.ShouldBeEmpty();

        // authenticated caller: exactly one write per call, until the bucket is empty; response never changes
        var e = Active();
        e.Limiter = new ProcedureDenialAuditLimiter(perMinute: 2);
        for (int i = 0; i < 5; i++)
        {
            var before = e.AuditEntries.Count;
            await Should.ThrowAsync<KeyNotFoundException>(() => Run(e, "nope"));
            (e.AuditEntries.Count - before).ShouldBe(i < 2 ? 1 : 0);
        }

        e.AuditEntries.ShouldAllBe(a => a.EventType == "PROCEDURE_DENIED" && a.DetailsJson.Contains("\"reason\":\"unknown\"") && a.TargetTable == "nope");

        // another SID has its own budget
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e, "nope", user: Principal(ClaimTypes.PrimarySid, "S-1-5-21-2002")));
        e.AuditEntries.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("bad name; DROP TABLE x")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("name\r\nforged: log line")]
    [Trait("Category", "Security")]
    public async Task T_PROC152_05_UnknownName_NonIdentifier_StoredAsInvalidMarker_NoRawTextInAuditOrLog(string name)
    {
        var e = Active();
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e, name));

        var entry = e.AuditEntries.Single();
        entry.TargetTable.ShouldBe("<invalid>");
        entry.DetailsJson.ShouldNotContain("DROP");
        entry.DetailsJson.ShouldNotContain("script");
        e.Logger.AllText.ShouldNotContain("DROP");
        e.Logger.AllText.ShouldNotContain("script");
        e.Logger.AllText.ShouldNotContain("forged");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_05_UnknownName_Oversize_StoredAsInvalidMarker()
    {
        var e = Active();
        var name = new string('a', 129);
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e, name));
        e.AuditEntries.Single().TargetTable.ShouldBe("<invalid>");
        JsonContains(e.AuditEntries.Single(), name).ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_UnknownName_AuditsReasonUnknown_WithInvalidMarkerForNonIdentifier()
    {
        var e = Active();
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e, "ok_name-1.v2"));
        e.AuditEntries.Single().TargetTable.ShouldBe("ok_name-1.v2");
    }

    private static bool JsonContains(AuditLogEntry entry, string text) => entry.DetailsJson.Contains(text, StringComparison.Ordinal) || entry.TargetTable.Contains(text, StringComparison.Ordinal);

    [Fact]
    public void DenialAuditLimiter_WindowResets_AndIsBoundedPerKey()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var limiter = new ProcedureDenialAuditLimiter(perMinute: 2, time);
        limiter.TryAcquire("t", "s").ShouldBeTrue();
        limiter.TryAcquire("t", "s").ShouldBeTrue();
        limiter.TryAcquire("t", "s").ShouldBeFalse();
        limiter.TryAcquire("t", "other").ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("t", "s").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152_05_Limiter_AtCapacity_NewKeysDropped_ExistingKeysStillServed_Counted()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var limiter = new ProcedureDenialAuditLimiter(perMinute: 2, time, maxTrackedKeys: 5);
        for (var i = 0; i < 5; i++)
        {
            limiter.TryAcquire("t", "s" + i).ShouldBeTrue();
        }

        limiter.TryAcquire("t", "new").ShouldBeFalse();
        limiter.Dropped.ShouldBe(1);
        limiter.TrackedKeys.ShouldBe(5);
        limiter.TryAcquire("t", "s0").ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("t", "new").ShouldBeTrue();
        limiter.TrackedKeys.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152_05_Limiter_SweepsAtMostOncePerWindow()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var limiter = new ProcedureDenialAuditLimiter(perMinute: 2, time, maxTrackedKeys: 4);
        limiter.TryAcquire("t", "a").ShouldBeTrue(); // sweep at t0, next sweep t0+60
        limiter.TryAcquire("t", "b").ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(30));
        limiter.TryAcquire("t", "c").ShouldBeTrue();
        limiter.TryAcquire("t", "d").ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(31)); // t0+61: a,b expired
        limiter.TryAcquire("t", "e").ShouldBeTrue(); // sweep removes a,b
        limiter.TryAcquire("t", "f").ShouldBeTrue(); // c,d,e,f: at cap
        time.Advance(TimeSpan.FromSeconds(30)); // t0+91: c,d expired but the next sweep is only due at t0+121
        limiter.TryAcquire("t", "g").ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(30)); // t0+121
        limiter.TryAcquire("t", "g").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152_05_Limiter_ConcurrentNewKeys_NeverExceedBound()
    {
        var limiter = new ProcedureDenialAuditLimiter(perMinute: 2, maxTrackedKeys: 50);
        var granted = 0;
        Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            if (limiter.TryAcquire("t", "s" + i))
            {
                Interlocked.Increment(ref granted);
            }
        });

        granted.ShouldBe(50);
        limiter.TrackedKeys.ShouldBe(50);
        limiter.Dropped.ShouldBe(1950);
    }

    // ---------- WP-2: lease ----------

    [Theory]
    [InlineData("success")]
    [InlineData("exception")]
    [InlineData("cancellation")]
    [InlineData("audit-throws")]
    [InlineData("partial-acquire")]
    [Trait("Category", "Security")]
    public async Task T_PROC152_06_Lease_ReleasedOn_Success_Exception_Cancellation_AuditThrows_PartialAcquire(string scenario)
    {
        var e = Active(d => d, tables: ["sales.orders", "sales.items"]);
        switch (scenario)
        {
            case "success":
                (await Run(e)).RowCount.ShouldBe(2);
                break;
            case "exception":
                e.Invoker.ExecuteReadAsync(default!, default!, default!, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("x"));
                await Should.ThrowAsync<InvalidOperationException>(() => Run(e));
                break;
            case "cancellation":
                e.Invoker.ExecuteReadAsync(default!, default!, default!, default, default).ThrowsAsyncForAnyArgs(new OperationCanceledException());
                await Should.ThrowAsync<OperationCanceledException>(() => Run(e));
                break;
            case "audit-throws":
                e.Audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("audit down")));
                var svc = new GovernedProcedureExecutionService(
                    e.Registry, e.Invoker, Microsoft.Extensions.Options.Options.Create(e.Options), e.Audit, e.Tables, e.Consents, e.Resolution, e.Masking,
                    policyEnforcement: e.PolicyEnforcement, concurrencyGate: e.Gate);
                await Should.ThrowAsync<Autheris.Application.Audit.AuditUnavailableException>(() => svc.ExecuteAsync("get_orders", Valid(), User(), Tenant));
                break;
            default:
                e.Gate.DenyOnCall = 2;
                await Should.ThrowAsync<GatewayThrottledException>(() => Run(e));
                e.Gate.Calls.Count.ShouldBe(2);
                break;
        }

        e.Gate.Outstanding.ShouldBe(0);
    }

    [Fact]
    public async Task Execute_OverLimit_Visible_Throws429_Invisible_404_AndAudits()
    {
        var e = Active();
        e.Gate.DenyAll = true;
        var ex = await Should.ThrowAsync<GatewayThrottledException>(() => Run(e));
        ex.RetryAfterSeconds.ShouldBe(2);
        e.AuditEntries.ShouldContain(a => a.EventType == "PROCEDURE_THROTTLED");
        await e.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Lease_TakenAfterVisibility_NotTakenWhenLimitZero()
    {
        var e = Active();
        e.Options = new GatewayOptions { DataSources = new SqlDataSourceOptions { MaxConcurrentReadsPerUserAndTable = 0 } };
        await Run(e);
        e.Gate.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("primary", "S-1-5-21-1001", "tenant-a|s-1-5-21-1001|sales.orders")]
    [InlineData("sub", "user-sub-7", "tenant-a|user-sub-7|sales.orders")]
    [InlineData("anonymous", "", "tenant-a|anonymous|sales.orders")]
    [Trait("Category", "Security")]
    public async Task T_PROC152_07_Lease_KeyEqualsSqlPathKey_PrimarySidThenSubThenAnonymous(string kind, string value, string expectedKey)
    {
        var e = Active();
        ClaimsPrincipal user;
        if (kind == "anonymous")
        {
            e.Options = Bypass();
            user = Anonymous();
        }
        else
        {
            user = Principal(kind == "primary" ? ClaimTypes.PrimarySid : "sub", value);
        }

        await Run(e, user: user);
        e.Gate.Calls.Single().Key.ShouldBe(expectedKey);
        e.Gate.Calls.Single().Max.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_07_Lease_PrimarySidWinsOverSub()
    {
        var e = Active();
        var user = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1001"), new Claim("sub", "other")], "test"));
        await Run(e, user: user);
        e.Gate.Calls.Single().Key.ShouldBe("tenant-a|s-1-5-21-1001|sales.orders");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_08_Lease_LimitPositive_GateNull_FailsClosed500AfterVisibility_404ForInvisible()
    {
        var visible = Active();
        visible.UseGate = false;
        await Should.ThrowAsync<InvalidOperationException>(() => Run(visible));
        await visible.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);

        var invisible = Active(d => d with { RequiredRoles = ["r"] });
        invisible.UseGate = false;
        await Should.ThrowAsync<KeyNotFoundException>(() => Run(invisible, user: User("o")));
    }

    // ---------- WP-3: business error and failure logging ----------

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_09_BusinessAndDbFailure_SecretText_AbsentFromResponseLogsAuditSpans()
    {
        const string secret = "Asset zq4711qz of Mueller dbo.Secret";
        var spans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => spans.Add(a.DisplayName + string.Join(",", a.Tags.Select(t => t.Value)) + string.Join(",", a.Events.Select(ev => ev.Name + string.Join(",", ev.Tags.Select(t => t.Value)))) + a.StatusDescription)
        };
        ActivitySource.AddActivityListener(listener);

        // business error: the invoker converts the DbException into number/SQLSTATE only
        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlException(50001, secret), out var number, out var sqlState).ShouldBeTrue();
        number.ShouldBe(50001);
        sqlState.ShouldBeNull();
        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlException(1205, secret), out _, out _).ShouldBeFalse();

        var business = Active();
        business.Invoker.ExecuteReadAsync(default!, default!, default!, default, default).ThrowsAsyncForAnyArgs(new ProcedureBusinessException(50001, null));
        var bex = await Should.ThrowAsync<ProcedureBusinessException>(() => Run(business));
        bex.Message.ShouldBe("The procedure rejected the request.");
        bex.ErrorNumber.ShouldBe(50001);
        bex.TraceId.ShouldNotBeNullOrWhiteSpace();
        business.AuditEntries.Single(a => a.EventType == "PROCEDURE_REJECTED").DetailsJson.ShouldContain("50001");
        business.AuditEntries.Single(a => a.EventType == "PROCEDURE_REJECTED").TraceId.ShouldBe(bex.TraceId);
        business.Logger.Entries.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.Contains("50001") && l.Exception == null);

        // non-business DbException: logged without the exception object and its text
        var db = Active();
        db.Invoker.ExecuteReadAsync(default!, default!, default!, default, default).ThrowsAsyncForAnyArgs(new FakeSqlException(1205, secret));
        await Should.ThrowAsync<FakeSqlException>(() => Run(db, inputs: new Dictionary<string, object?> { ["customer_id"] = 7, ["note"] = "value-zq4711qz" }));
        db.Logger.Entries.ShouldAllBe(l => l.Exception == null);
        db.Logger.AllText.ShouldNotContain("Mueller");
        db.Logger.AllText.ShouldNotContain("Secret");
        db.Logger.AllText.ShouldNotContain("zq4711qz");
        db.AuditEntries.ShouldAllBe(a => !a.DetailsJson.Contains("Mueller") && !a.DetailsJson.Contains("zq4711qz"));
        db.AuditEntries.ShouldContain(a => a.EventType == "PROCEDURE_FAILED");

        foreach (var all in new[] { business.Logger.AllText, db.Logger.AllText }.Concat(spans))
        {
            all.ShouldNotContain("Mueller");
        }
    }

    // ---------- WP-4: cleared_columns ----------

    private static async Task<GovernedProcedureResult> RunCleared(
        Func<TableAccessDecision, TableAccessDecision> decision,
        Func<TableMetadata, TableMetadata>? meta = null,
        bool abac = false)
    {
        var e = Active(d => d with { ClearedResultColumns = ["salary"] });
        e.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var m = new TableMetadata
            {
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "order_id" }, new TableColumn { ColumnName = "secret" }, new TableColumn { ColumnName = "salary", IsSensitive = true }]
            };
            return meta?.Invoke(m) ?? m;
        });
        e.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => decision(TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase), hasUnconstrainedColumnAllow: true)));
        if (abac)
        {
            var pe = Substitute.For<IPolicyEnforcementService>();
            pe.HasPolicies(Arg.Any<TenantId>()).Returns(true);
            pe.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
                .Returns(ci => ValueTask.FromResult(TableAccessDecision.Allowed(ci.Arg<SecurityEvaluationContext>().TargetTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));
            e.PolicyEnforcement = pe;
        }

        return await Run(e);
    }

    private static TableAccessDecision WithColumn(TableAccessDecision d, ColumnAccessLevel level) =>
        d with { ColumnAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["salary"] = level } };

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_10_ClearedColumns_ExplicitBaseMask_NeverLifted()
    {
        var r = await RunCleared(d => WithColumn(d, ColumnAccessLevel.Mask));
        r.Rows[0]["salary"].ShouldBe("***");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_10_ClearedColumns_Deny_NeverLifted()
    {
        var r = await RunCleared(d => WithColumn(d, ColumnAccessLevel.Deny));
        r.Columns.ShouldNotContain("salary");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_10_ClearedColumns_ScopeSpecificMask_StaysMaskedForScopeA_ClearForScopeB()
    {
        // scope-specific rules end up as explicit column rules of the resolved decision (they only harden)
        var scopeA = await RunCleared(d => WithColumn(d, ColumnAccessLevel.Mask));
        scopeA.Rows[0]["salary"].ShouldBe("***");

        var scopeB = await RunCleared(d => d); // no explicit rule: sensitivity-only mask is lifted
        scopeB.Rows[0]["salary"].ShouldBe(1000m);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_10_ClearedColumns_SensitivityOnlyMask_Lifted()
    {
        var r = await RunCleared(d => d);
        r.Rows[0]["salary"].ShouldBe(1000m);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_10_ClearedColumns_WithAbacRewrite_SensitivityOnlyMaskStillLifted()
    {
        var r = await RunCleared(d => d, abac: true);
        r.Rows[0]["salary"].ShouldBe(1000m);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_10_ClearedColumns_WithAbacRewrite_ExplicitMaskNotLifted()
    {
        var r = await RunCleared(d => WithColumn(d, ColumnAccessLevel.Mask), abac: true);
        r.Rows[0]["salary"].ShouldBe("***");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_11_ClearedColumns_SensitivitySetUnavailable_LiftsNothing()
    {
        // a catalog column without a name makes the sensitivity-only set impossible to compute (fail closed: nothing lifted)
        var r = await RunCleared(d => d, meta: m => m with { Columns = [.. m.Columns, new TableColumn { ColumnName = null! }] });
        r.Rows[0]["salary"].ShouldBe("***");
    }

    // ---------- zero tables, all modes ----------

    [Theory]
    [InlineData("catalog")]
    [InlineData("declared")]
    [InlineData("catalog-bypass")]
    [InlineData("declared-bypass")]
    [Trait("Category", "Security")]
    public async Task T_PROC152_12_ZeroTableProcedure_AllModes_InvisibleAndNeverInvoked(string mode)
    {
        var e = Active(d => d with
        {
            ResultTable = null,
            ValidationMode = mode.StartsWith("declared", StringComparison.Ordinal) ? ProcedureValidationMode.Declared : ProcedureValidationMode.Catalog
        }, tables: []);
        if (mode.EndsWith("bypass", StringComparison.Ordinal))
        {
            e.Options = Bypass();
        }

        await Should.ThrowAsync<KeyNotFoundException>(() => Run(e));
        await e.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
        (await e.Create().IsVisibleAsync(e.Registry.GetAll().Single(), User(), Tenant)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsVisibleAsync_VisibleOnlyWhenActive()
    {
        var active = Active();
        (await active.Create().IsVisibleAsync(active.Registry.GetAll().Single(), User(), Tenant)).ShouldBeTrue();

        var pending = Active(markActive: false);
        (await pending.Create().IsVisibleAsync(pending.Registry.GetAll().Single(), User(), Tenant)).ShouldBeFalse();
    }

    // ---------- WP-5: list and OpenAPI ----------

    private static IProcedureRegistry TwoProcedureRegistry(out InMemoryProcedureRegistry registry)
    {
        registry = new InMemoryProcedureRegistry();
        foreach (var name in new[] { "get_orders", "get_other" })
        {
            var def = ProcedureDefinitionParser.Parse(Header.Replace("get_orders", name), "x", false, 60);
            registry.Register(def);
            registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["order_id"], ["sales.orders"], new Dictionary<string, string>()));
        }

        return registry;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_13_OpenApiAndList_OpenSchemaAnonymous_NoProcedures_OneProcedureFailing_OmittedOnly()
    {
        var registry = TwoProcedureRegistry(out _);
        var service = Substitute.For<IProcedureExecutionService>();
        service.IsVisibleAsync(Arg.Is<RegisteredProcedure>(r => r.Definition.Name == "get_orders"), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        service.IsVisibleAsync(Arg.Is<RegisteredProcedure>(r => r.Definition.Name == "get_other"), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("evaluation failed"));

        // anonymous in open-schema mode: empty paths, no evaluation at all
        var anonymousHttp = new DefaultHttpContext { User = Anonymous() };
        var anon = await ProcedureEndpointRoutes.HandleOpenApi(anonymousHttp, registry, service);
        ((Dictionary<string, object>)anon.ShouldBeOfType<Ok<Dictionary<string, object>>>().Value!["paths"]).ShouldBeEmpty();

        // authenticated: only the visible procedure; the failing one is omitted, the response is not failed
        var http = new DefaultHttpContext { User = User() };
        var open = await ProcedureEndpointRoutes.HandleOpenApi(http, registry, service);
        var paths = (Dictionary<string, object>)open.ShouldBeOfType<Ok<Dictionary<string, object>>>().Value!["paths"];
        paths.Keys.ShouldBe(["/api/v1/procedures/get_orders"]);

        var list = await ProcedureEndpointRoutes.HandleList(http, registry, service);
        var items = list.ShouldBeOfType<Ok<List<object>>>().Value!;
        items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task List_And_OpenApi_ShowOnlyVisibleActive()
    {
        var registry = TwoProcedureRegistry(out _);
        var service = Substitute.For<IProcedureExecutionService>();
        service.IsVisibleAsync(default!, default!, default, default).ReturnsForAnyArgs(Task.FromResult(false));
        var http = new DefaultHttpContext { User = User() };

        var open = await ProcedureEndpointRoutes.HandleOpenApi(http, registry, service);
        ((Dictionary<string, object>)open.ShouldBeOfType<Ok<Dictionary<string, object>>>().Value!["paths"]).ShouldBeEmpty();
        (await ProcedureEndpointRoutes.HandleList(http, registry, service)).ShouldBeOfType<Ok<List<object>>>().Value!.ShouldBeEmpty();
    }

    // ---------- route level ----------

    private sealed record RouteResponse(int Status, string Body, string ContentType, string CacheControl, string? RetryAfter);

    private static async Task<RouteResponse> CallRoute(IProcedureExecutionService service, bool post, string name, string? query, string? body, ILoggerFactory? loggerFactory = null)
    {
        var http = new DefaultHttpContext { User = User() };
        http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        http.Response.Body = new MemoryStream();
        if (query != null)
        {
            http.Request.QueryString = new QueryString("?" + query);
        }

        if (post)
        {
            http.Request.Method = "POST";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
            http.Request.ContentLength = body?.Length ?? 0;
            await ProcedureEndpointRoutes.HandlePost(name, http, service, loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        }
        else
        {
            await ProcedureEndpointRoutes.HandleGet(name, http, service, loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        }

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return new RouteResponse(http.Response.StatusCode, text, http.Response.ContentType ?? string.Empty, http.Response.Headers.CacheControl.ToString(), http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_01_Differential_UnknownRoleConsentNoTableUnavailable_ByteIdenticalStatusBodyHeaders()
    {
        var scenarios = new Dictionary<string, Func<(Env Env, string Name, ClaimsPrincipal User)>>
        {
            ["unknown"] = () => (Active(), "nope", User()),
            ["role"] = () => (Active(d => d with { RequiredRoles = ["r"] }), "get_orders", User("other")),
            ["consent"] = () => { var e = Active(); DenyConsent(e); return (e, "get_orders", User()); },
            ["no-table"] = () => (Active(d => d with { ResultTable = null }, tables: []), "get_orders", User()),
            ["unavailable"] = () => (Active(d => d with { RequiredRoles = ["r"] }, markActive: false), "get_orders", User("other")),
            ["no-sid"] = () => (Active(), "get_orders", Anonymous()),
            ["write-mode"] = () => (Active(d => d with { Mode = ProcedureMode.Write }), "get_orders", User()),
            ["visibility-error"] = () =>
            {
                var e = Active();
                e.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns<Task<TableMetadata?>>(_ => throw new ArgumentException("param leak"));
                return (e, "get_orders", User());
            }
        };

        var variants = new (bool Post, string? Query, string? Body)[]
        {
            (false, "customer_id=7", null), (false, "bogus=1", null), (false, null, null),
            (true, null, "{\"customer_id\":7}"), (true, null, "{\"bogus\":1}"), (true, null, "{}"),
            (false, "limit=abc", null), (false, "limit=1", null), (true, null, "{\"limit\":0}"), (true, null, "{\"limit\":2}")
        };

        for (int v = 0; v < variants.Length; v++)
        {
            RouteResponse? reference = null;
            foreach (var (label, make) in scenarios)
            {
                var (env, name, user) = make();
                var service = new UserOverridingService(env.Create(), user);
                var response = await CallRoute(service, variants[v].Post, name, variants[v].Query, variants[v].Body);
                response.Status.ShouldBe(404, label);
                // NL-T-22: golden bytes of the Indistinguishable body (no traceId, instance or correlationId).
                response.Body.ShouldBe("{\"type\":\"urn:autheris:error:RESOURCE_NOT_FOUND\",\"title\":\"Not Found\",\"status\":404,\"detail\":\"Procedure endpoint not found.\",\"code\":\"RESOURCE_NOT_FOUND\"}", label);
                reference ??= response;
                response.ShouldBe(reference, label + " variant " + v);
            }
        }
    }

    /// <summary>Lets the route test choose the principal (the route reads <c>http.User</c>, which the helper fixes).</summary>
    private sealed class UserOverridingService(IProcedureExecutionService inner, ClaimsPrincipal user) : IProcedureExecutionService
    {
        public Task<GovernedProcedureResult> ExecuteAsync(string name, IReadOnlyDictionary<string, object?>? rawInputs, ClaimsPrincipal _, TenantId tenantId, CancellationToken ct = default) =>
            inner.ExecuteAsync(name, rawInputs, user, tenantId, ct);

        public Task<bool> IsVisibleAsync(RegisteredProcedure procedure, ClaimsPrincipal _, TenantId tenantId, CancellationToken ct = default) =>
            inner.IsVisibleAsync(procedure, user, tenantId, ct);
    }

    [Fact]
    public async Task Routes_Map429WithRetryAfter_And422GenericShape()
    {
        var service = Substitute.For<IProcedureExecutionService>();
        service.ExecuteAsync(default!, default, default!, default, default).ThrowsAsyncForAnyArgs(new GatewayThrottledException(2));
        var throttled = await CallRoute(service, false, "x", null, null);
        throttled.Status.ShouldBe(429);
        throttled.RetryAfter.ShouldBe("2");
        using (var t = System.Text.Json.JsonDocument.Parse(throttled.Body))
        {
            t.RootElement.GetProperty("code").GetString().ShouldBe("TOO_MANY_REQUESTS");
            t.RootElement.GetProperty("detail").GetString().ShouldBe("Too many concurrent requests. Retry later.");
            t.RootElement.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(2);
        }

        service.ExecuteAsync(default!, default, default!, default, default).ThrowsAsyncForAnyArgs(new ProcedureBusinessException(50001, null, "abc123"));
        var business = await CallRoute(service, true, "x", null, "{}");
        business.Status.ShouldBe(422);
        using (var b = System.Text.Json.JsonDocument.Parse(business.Body))
        {
            b.RootElement.GetProperty("code").GetString().ShouldBe("PROCEDURE_REJECTED");
            b.RootElement.GetProperty("detail").GetString().ShouldBe("The procedure rejected the request.");
        }
        business.CacheControl.ShouldBe("no-store");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_14_Route_NoSecurityExceptionBranch_NoParameterValuesInErrorLogs()
    {
        var logger = new CapturingLogger();
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var service = Substitute.For<IProcedureExecutionService>();
        service.ExecuteAsync(default!, default, default!, default, default).ThrowsAsyncForAnyArgs(new SecurityException("legacy"));

        var response = await CallRoute(service, false, "probe-name", "customer_id=value-zq4711qz", null, factory);

        response.Status.ShouldBe(500); // no 403 branch any more
        using (var b = System.Text.Json.JsonDocument.Parse(response.Body))
        {
            b.RootElement.GetProperty("code").GetString().ShouldBe("INTERNAL_ERROR");
            b.RootElement.GetProperty("detail").GetString().ShouldBe("An internal error occurred.");
        }
        logger.AllText.ShouldNotContain("value-zq4711qz");
        logger.AllText.ShouldNotContain("probe-name");
    }

    private const string SecretText = "Asset zq4711qz of Mueller";

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_08_Service_GenericException_NotInLogsAuditOrResponse()
    {
        var e = Active();
        e.Invoker.ExecuteReadAsync(default!, default!, default!, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException(SecretText, new InvalidOperationException(SecretText)));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Run(e));

        ex.Message.ShouldContain("zq4711qz"); // rethrown unchanged; the route maps it to a generic 500
        e.Logger.AllText.ShouldNotContain("zq4711qz");
        e.Logger.AllText.ShouldNotContain("Mueller");
        e.Logger.Entries.ShouldAllBe(x => x.Exception == null);
        e.Logger.AllText.ShouldContain(nameof(InvalidOperationException));
        e.AuditEntries.ShouldAllBe(x => !x.DetailsJson.Contains("zq4711qz") && !x.DetailsJson.Contains("Mueller"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152_08_Route_UnexpectedException_NotInLogsOrResponse()
    {
        var logger = new CapturingLogger();
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var service = Substitute.For<IProcedureExecutionService>();
        service.ExecuteAsync(default!, default, default!, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException(SecretText, new InvalidOperationException(SecretText)));

        var response = await CallRoute(service, false, "probe-name", null, null, factory);

        response.Status.ShouldBe(500);
        response.Body.ShouldNotContain("zq4711qz");
        logger.AllText.ShouldNotContain("zq4711qz");
        logger.AllText.ShouldNotContain("Mueller");
        logger.Entries.ShouldAllBe(x => x.Exception == null);
        logger.AllText.ShouldContain(nameof(InvalidOperationException));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void GatewayStorageOptionsValidator_RejectsOutOfRangeProcedureConcurrency(int limit, bool valid)
    {
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var validator = new Autheris.Api.Configuration.Validation.GatewayStorageOptionsValidator(env);

        var result = validator.Validate(null, new GatewayOptions { DataSources = new SqlDataSourceOptions { MaxConcurrentReadsPerUserAndTable = limit } });

        result.Succeeded.ShouldBe(valid);
    }

    [Fact]
    public void ProcedureBusinessException_HasFixedMessage()
    {
        new ProcedureBusinessException(50001, "P0001").Message.ShouldBe("The procedure rejected the request.");
    }
}
