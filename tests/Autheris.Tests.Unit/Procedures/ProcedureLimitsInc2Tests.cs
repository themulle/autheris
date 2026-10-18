using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Procedures;

/// <summary>
/// PLAN-PROC-152-INC2 WP-A: row budget decided before the invoke, scan cap, limit applied only after the row scope
/// (SEC-PROC152I2-01..06 and -14).
/// </summary>
public class ProcedureLimitsInc2Tests
{
    private const string ScopedHeader = """
        -- @name get_cranes
        -- @procedure tem.usp_Cranes
        -- @result-table md.crane
        -- @row-scope-key serial_number
        """;

    private const string UnscopedHeader = """
        -- @name get_cranes
        -- @procedure tem.usp_Cranes
        -- @result-table md.crane
        """;

    private static readonly TenantId Tenant = new("tenant-a");

    private sealed class Fixture
    {
        public InMemoryProcedureRegistry Registry { get; } = new();
        public IProcedureInvoker Invoker { get; } = Substitute.For<IProcedureInvoker>();
        public IProcedureRowScopeResolver RowScope { get; } = Substitute.For<IProcedureRowScopeResolver>();
        public ITableMetadataRepository Tables { get; } = Substitute.For<ITableMetadataRepository>();
        public IConsentRepository Consents { get; } = Substitute.For<IConsentRepository>();
        public IConsentResolutionService Resolution { get; } = Substitute.For<IConsentResolutionService>();
        public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();
        public List<AuditLogEntry> AuditEntries { get; } = [];
        public List<int> Budgets { get; } = [];
        public ProcedureEndpointsOptions Procedures { get; set; } = new() { MaxRows = 10 };

        public GovernedProcedureExecutionService Create()
        {
            Audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(AuditEntries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var options = new GatewayOptions { SqlEndpoints = new SqlEndpointsOptions { Procedures = Procedures } };
            return new GovernedProcedureExecutionService(
                Registry, Invoker, Options.Create(options), Audit, Tables, Consents, Resolution, masking: null,
                policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
                concurrencyGate: global::Autheris.Tests.Unit.TestDoubles.GrantAllReadConcurrencyGate.Instance,
                rowScope: RowScope);
        }

        /// <summary>The invoker double honours the budget like the real invoker (reads at most the budget, flags one more).</summary>
        public void RawRows(int total)
        {
            Invoker.ExecuteReadAsync(
                    Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    int budget = ci.ArgAt<int>(3);
                    Budgets.Add(budget);
                    var rows = Enumerable.Range(0, Math.Min(total, budget)).Select(i => new object?[] { "k" + i, "n" + i }).ToList();
                    return Task.FromResult(new RawProcedureResult(["serial_number", "name"], rows, total > budget));
                });
        }

        public void Allow(IEnumerable<int> indexes)
        {
            var keys = indexes.Select(i => RowScopeKeys.Normalize(["k" + i])!).ToHashSet();
            RowScope.GetAllowedKeysAsync(
                    Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<string>>(keys));
        }
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LWE-DAVID")], "test"));

    private static Fixture Active(bool scoped, int total, int maxRows = 10, int? maxScanRows = null, ProcedureMode mode = ProcedureMode.Read, IReadOnlyList<ProcedureErrorCode>? errorCodes = null)
    {
        var f = new Fixture { Procedures = new ProcedureEndpointsOptions { MaxRows = maxRows, MaxScanRows = maxScanRows } };
        var def = ProcedureDefinitionParser.Parse(scoped ? ScopedHeader : UnscopedHeader, "x", false, 60) with { Mode = mode, ErrorCodes = errorCodes ?? [] };
        f.Registry.Register(def);
        f.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["serial_number", "name"], ["md.crane"], new Dictionary<string, string>()));
        f.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = new TableIdentifier("default", "md", "crane"),
            Columns = [new TableColumn { ColumnName = "serial_number" }, new TableColumn { ColumnName = "name" }]
        });
        f.Consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());
        var columns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["serial_number"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear
        };
        string? filter = scoped ? "[autheris_target].[is_delivered] IS NULL" : null;
        f.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), columns, filter));
        f.RawRows(total);
        return f;
    }

    private static Task<GovernedProcedureResult> Run(Fixture f) => f.Create().ExecuteAsync("get_cranes", null, User(), Tenant);

    // ---------- service: budget, scan cap, cut after scope ----------

    [Fact]
    public async Task RowScope_3Of100Allowed_MaxRows10_Returns3_NotTruncated()
    {
        var f = Active(scoped: true, total: 100);
        f.Allow([5, 50, 95]);

        var result = await Run(f);

        result.RowCount.ShouldBe(3);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task RowScope_ScanCapReachedWithUnreadRows_TruncatedTrue()
    {
        var f = Active(scoped: true, total: 200); // scan cap = 10 * 10 = 100
        f.Allow([5, 50]);

        var result = await Run(f);

        result.RowCount.ShouldBe(2);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task RowScope_ScopedRowsExceedLimit_CutAfterScope_TruncatedTrue()
    {
        var f = Active(scoped: true, total: 100);
        f.Allow(Enumerable.Range(40, 50));

        var result = await Run(f);

        result.RowCount.ShouldBe(10);
        result.Rows.Select(r => r["serial_number"]).ShouldBe(Enumerable.Range(40, 10).Select(i => "k" + i));
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task RowScope_ScopedRowsExactlyLimit_NotTruncated()
    {
        var f = Active(scoped: true, total: 100);
        f.Allow(Enumerable.Range(40, 10));

        var result = await Run(f);

        result.RowCount.ShouldBe(10);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_01_RowScope_Active_InvokerReceivesMaxScanRowsBudget_NeverEffectiveLimit()
    {
        var f = Active(scoped: true, total: 5);
        f.Allow([1]);
        await Run(f);
        f.Budgets.ShouldBe([100]); // 10 * MaxRows

        var explicitCap = Active(scoped: true, total: 5, maxRows: 10, maxScanRows: 37);
        explicitCap.Allow([1]);
        await Run(explicitCap);
        explicitCap.Budgets.ShouldBe([37]);

        // Single source: the budget and the scope branch agree (resolver called exactly when the scan budget was used).
        await f.RowScope.ReceivedWithAnyArgs(1).GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
        var unscoped = Active(scoped: false, total: 5);
        await Run(unscoped);
        unscoped.Budgets.ShouldBe([10]);
        await unscoped.RowScope.DidNotReceiveWithAnyArgs().GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task NoRowScope_InvokerReceivesEffectiveLimit_ExtraRowSetsTruncated()
    {
        var f = Active(scoped: false, total: 25);

        var result = await Run(f);

        f.Budgets.ShouldBe([10]);
        result.RowCount.ShouldBe(10);
        result.Truncated.ShouldBeTrue();
        await f.RowScope.DidNotReceiveWithAnyArgs().GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task NoRowScope_ExactlyLimitRows_NotTruncated()
    {
        var f = Active(scoped: false, total: 10);

        var result = await Run(f);

        result.RowCount.ShouldBe(10);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_02_RowScope_NeverReturnsOtherScopeRows_WhenRawExceedsLimit()
    {
        // 100 raw rows, 3 in scope, limit 10; the in-scope rows lie beyond the first 10 raw rows (a pre-scope cut would lose them
        // or, worse, return unscoped rows).
        var f = Active(scoped: true, total: 100);
        f.Allow([15, 50, 95]);

        var result = await Run(f);

        result.Rows.Select(r => (string)r["serial_number"]!).ShouldBe(["k15", "k50", "k95"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_03_RowScope_ResolverFails_Returns503_NoFallbackToUnscoped()
    {
        var f = Active(scoped: true, total: 100);
        f.RowScope.GetAllowedKeysAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlySet<string>>>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<ProcedureUnavailableException>(() => Run(f));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_04_Response_ContainsNoScopeCountsOrScanCapFlag_AuditDoes()
    {
        var f = Active(scoped: true, total: 200);
        f.Allow(Enumerable.Range(0, 30));

        var result = await Run(f);

        var names = typeof(GovernedProcedureResult).GetProperties().Select(p => p.Name).ToList();
        names.ShouldBe(["Columns", "Rows", "RowCount", "Truncated", "ElapsedMilliseconds"], ignoreOrder: true);
        result.Truncated.ShouldBeTrue();

        var details = f.AuditEntries.Single(a => a.EventType == "PROCEDURE_EXECUTE").DetailsJson;
        details.ShouldContain("\"truncated\":true");
        details.ShouldContain("\"scanCapReached\":true");
        details.ShouldContain("\"effectiveLimit\":10");
        details.ShouldContain("\"rowsRemovedByScope\":70");
    }

    [Fact]
    public async Task Audit_NoScan_ScanCapReachedFalse()
    {
        var f = Active(scoped: false, total: 3);

        await Run(f);

        var details = f.AuditEntries.Single(a => a.EventType == "PROCEDURE_EXECUTE").DetailsJson;
        details.ShouldContain("\"scanCapReached\":false");
        details.ShouldContain("\"effectiveLimit\":10");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_07_RowScope_ByteBudgetExceededDuringScan_ReturnsResponseTooLarge_FixedBody_NoRows()
    {
        var f = Active(scoped: true, total: 100);
        f.Invoker.ExecuteReadAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<RawProcedureResult>>(_ => throw new GatewaySecurityException("Response size exceeds the configured limit of 1024 bytes.", "RESPONSE_TOO_LARGE"));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Run(f));

        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
        await f.RowScope.DidNotReceiveWithAnyArgs().GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
        f.AuditEntries.ShouldNotContain(a => a.EventType == "PROCEDURE_EXECUTE");
    }

    // ---------- invoker: row budget ----------

    private sealed class InvokerHarness
    {
        public int GetValueCalls;
        public int Reads;
        public DbCommand ExecCmd = null!;
        public MssqlProcedureInvoker Invoker = null!;
        public ProcedureDefinition Definition = null!;
    }

    private static InvokerHarness BuildInvoker(int totalRows, long maxResponseBytes = 10485760, string value = "x")
    {
        var h = new InvokerHarness();
        var connFactory = Substitute.For<ISqlConnectionFactory>();
        var conn = Substitute.For<DbConnection>();

        var initCmd = Substitute.For<DbCommand>();
        initCmd.Parameters.Returns(Substitute.For<DbParameterCollection>());
        initCmd.CreateParameter().Returns(Substitute.For<DbParameter>());
        initCmd.ExecuteNonQueryAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0));

        var execCmd = Substitute.For<DbCommand>();
        execCmd.Parameters.Returns(Substitute.For<DbParameterCollection>());
        execCmd.CreateParameter().Returns(Substitute.For<DbParameter>());
        var reader = Substitute.For<DbDataReader>();
        reader.FieldCount.Returns(1);
        reader.GetName(0).Returns("v");
        reader.IsDBNull(0).Returns(_ =>
        {
            h.GetValueCalls++; // one call per value read (the extra row must never reach it)
            return false;
        });
        reader.GetFieldType(0).Returns(typeof(string));
        reader.GetChars(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<char[]>(), Arg.Any<int>(), Arg.Any<int>()).Returns(ci =>
        {
            long offset = ci.ArgAt<long>(1);
            var buffer = ci.ArgAt<char[]>(2);
            int count = (int)Math.Max(0, Math.Min(ci.ArgAt<int>(4), value.Length - offset));
            value.CopyTo((int)offset, buffer, ci.ArgAt<int>(3), count);
            return (long)count;
        });
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(++h.Reads <= totalRows));
        execCmd.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(reader));
        conn.CreateCommand().Returns(initCmd, execCmd);
        connFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(conn));

        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["default"] = new() { ConnectionString = "Server=localhost;", Provider = "SqlServer" }
                }
            },
            SqlEndpoints = new SqlEndpointsOptions
            {
                // MaxRows is deliberately tiny: the invoker must ignore it (no fallback to the setting).
                Procedures = new ProcedureEndpointsOptions { Enabled = true, ConnectionName = "default", MaxRows = 1, MaxResponseBytes = maxResponseBytes }
            }
        });
        h.ExecCmd = execCmd;
        h.Invoker = new MssqlProcedureInvoker(new ProcedureConnectionProvider(connFactory, options), options);
        h.Definition = ProcedureDefinitionParser.Parse(UnscopedHeader, "x", false, 60);
        return h;
    }

    private static Task<RawProcedureResult> Invoke(InvokerHarness h, int budget) =>
        h.Invoker.ExecuteReadAsync(h.Definition, new Dictionary<string, object?>(), new ProcedureSecurityContext("t-1", "s-1", null), budget, CancellationToken.None);

    [Theory]
    [InlineData(4, 5, 4, false)]   // budget - 1 raw rows
    [InlineData(5, 5, 5, false)]   // budget raw rows
    [InlineData(6, 5, 5, true)]    // budget + 1 raw rows
    [InlineData(50, 5, 5, true)]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_05_Invoker_RowBudget_Boundaries(int raw, int budget, int expectedRows, bool more)
    {
        var h = BuildInvoker(raw);

        var result = await Invoke(h, budget);

        result.Rows.Count.ShouldBe(expectedRows);
        result.MoreRowsAvailable.ShouldBe(more);
        h.GetValueCalls.ShouldBe(expectedRows); // the extra row's value is never read
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_05_Invoker_NonPositiveBudget_Throws_BeforeAnyRead(int budget)
    {
        var h = BuildInvoker(3);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Invoke(h, budget));

        h.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Invoker_StopsAfterRowBudget_ReportsMoreRowsAvailable_AndCancelsTheCommand()
    {
        var h = BuildInvoker(20);

        var result = await Invoke(h, 7);

        result.Rows.Count.ShouldBe(7);
        result.MoreRowsAvailable.ShouldBeTrue();
        h.Reads.ShouldBe(8);
        h.ExecCmd.Received(1).Cancel();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_07_Invoker_ByteBudgetExceededDuringScan_ThrowsResponseTooLarge()
    {
        var h = BuildInvoker(100, maxResponseBytes: 1024, value: new string('a', 200));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Invoke(h, 100));

        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
    }

    // ---------- options ----------

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(ProcedureEndpointsOptions procedures)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var validator = new Autheris.Api.Configuration.Validation.GatewayStorageOptionsValidator(env);
        return validator.Validate(null, new GatewayOptions { SqlEndpoints = new SqlEndpointsOptions { Procedures = procedures } });
    }

    [Theory]
    [InlineData(5000, 4999, false)]   // below MaxRows
    [InlineData(5000, 5000, true)]
    [InlineData(5000, 100000, true)]
    [InlineData(5000, 100001, false)] // above the hard cap
    [InlineData(5000, 0, false)]
    [InlineData(5000, -1, false)]
    [Trait("Category", "Security")]
    public void T_PROC152I2_06_Options_MaxScanRows_Boundaries_FailStartup(int maxRows, int maxScanRows, bool valid)
    {
        Validate(new ProcedureEndpointsOptions { MaxRows = maxRows, MaxScanRows = maxScanRows }).Succeeded.ShouldBe(valid);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152I2_06_Options_MaxRows100000_ScanNull_Valid()
    {
        var procedures = new ProcedureEndpointsOptions { MaxRows = 100000 };

        procedures.EffectiveMaxScanRows.ShouldBe(100000);
        Validate(procedures).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(5000, 50000)]
    [InlineData(10, 100)]
    [InlineData(20000, 100000)]
    [InlineData(100000, 100000)]
    public void Options_MaxScanRowsNull_DefaultsTo10xMaxRowsCapped100000(int maxRows, int expected)
    {
        new ProcedureEndpointsOptions { MaxRows = maxRows }.EffectiveMaxScanRows.ShouldBe(expected);
    }

    // ---------- WP-B: reserved `limit` input (SEC-PROC152I2-07, -08, -15) ----------

    private static Dictionary<string, object?> Inputs(object? limit)
        => new(StringComparer.OrdinalIgnoreCase) { ["limit"] = limit };

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Task<GovernedProcedureResult> RunWith(Fixture f, IReadOnlyDictionary<string, object?>? inputs)
        => f.Create().ExecuteAsync("get_cranes", inputs, User(), Tenant);

    public static TheoryData<object?, int> ValidLimits() => new()
    {
        { "3", 3 },
        { "10", 10 },
        { "999", 10 },
        { "99999999999999999999", 10 },
        { "007", 7 },
        { 3, 3 },
        { 3L, 3 },
        { 99999999999L, 10 },
        { Json("4"), 4 },
        { Json("99999999999"), 10 },
        { Json("\"5\""), 5 },
        { Json("12345678901234567890123"), 10 },
    };

    [Theory]
    [MemberData(nameof(ValidLimits))]
    public async Task Limit_Valid_FeedsTheRowBudget_AndClampsToMaxRows(object? limit, int expectedBudget)
    {
        var f = Active(scoped: false, total: 100);

        await RunWith(f, Inputs(limit));

        f.Budgets.ShouldBe([expectedBudget]);
    }

    [Fact]
    public async Task Limit_Valid_ReturnsAtMostLimitRows_AndFlagsTruncated()
    {
        var f = Active(scoped: false, total: 100);

        var result = await RunWith(f, Inputs("3"));

        result.RowCount.ShouldBe(3);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Limit_Scoped_IsAppliedAfterTheRowScope_ScanCapUnchanged()
    {
        var f = Active(scoped: true, total: 100);
        f.Allow([5, 50, 95]);

        var result = await RunWith(f, Inputs("2"));

        result.RowCount.ShouldBe(2);
        result.Truncated.ShouldBeTrue();
        f.Budgets.ShouldBe([100]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("JSON-NULL")]
    public async Task Limit_NullIsAbsent_UsesMaxRows(string? marker)
    {
        var f = Active(scoped: false, total: 100);

        await RunWith(f, Inputs(marker == null ? null : Json("null")));

        f.Budgets.ShouldBe([10]);
    }

    public static TheoryData<object?> InvalidLimits() => new()
    {
        "0", "-1", "+1", "1.5", "1e2", "", " ", "abc", "1,2", "0x10", "１２", "1 ", " 1",
        0, -5, int.MinValue, 1.5, true, false,
        Json("0"), Json("-1"), Json("1.0"), Json("1.5"), Json("1e2"), Json("true"), Json("false"),
        Json("\"abc\""), Json("\"\""), Json("\"-3\""), Json("{}"), Json("[1]"), Json("[]"), Json("-99999999999"),
    };

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_08_Limit_Invalid_Is400_ConstantText_NeverEchoed(object? limit)
    {
        var f = Active(scoped: false, total: 100);

        var ex = await Should.ThrowAsync<ArgumentException>(() => RunWith(f, Inputs(limit)));

        ex.Message.ShouldBe("Invalid 'limit'.");
        f.Budgets.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_08_Limit_Invalid_ValueNotEchoed()
    {
        var f = Active(scoped: false, total: 100);

        var ex = await Should.ThrowAsync<ArgumentException>(() => RunWith(f, Inputs("SECRET-VALUE-77")));

        ex.Message.ShouldNotContain("SECRET");
        ex.ToString().ShouldNotContain("SECRET-VALUE-77");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("3")]
    [InlineData("0")]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_08_Limit_InvisibleCaller_Is404_Identical_ForValidAndInvalid(string limit)
    {
        var f = Active(scoped: false, total: 100);
        f.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Denied(ci.ArgAt<TableIdentifier>(3), "no consent"));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => RunWith(f, Inputs(limit)));

        var baseline = await Should.ThrowAsync<KeyNotFoundException>(() => RunWith(f, null));
        ex.Message.ShouldBe(baseline.Message);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_08_Limit_Invalid_UnavailableProcedure_Is503_NotValidation()
    {
        var f = Active(scoped: false, total: 100);
        f.Registry.MarkDisabled("get_cranes", "broken");

        await Should.ThrowAsync<ProcedureUnavailableException>(() => RunWith(f, Inputs("abc")));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_08_Limit_MixedCaseDuplicateKeys_CaseInsensitiveDictionaryKeepsLast_AndMaxRowsBounds()
    {
        var f = Active(scoped: false, total: 100);
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["limit"] = "1" };
        inputs["LIMIT"] = "2";

        await RunWith(f, inputs);

        f.Budgets.ShouldBe([2]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_08_Limit_MixedCaseDuplicateKeys_OrdinalDictionary_Is400()
    {
        var f = Active(scoped: false, total: 100);
        var inputs = new Dictionary<string, object?> { ["limit"] = "1", ["LIMIT"] = "2" };

        var ex = await Should.ThrowAsync<ArgumentException>(() => RunWith(f, inputs));

        ex.Message.ShouldBe("Invalid 'limit'.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_09_Limit_NeverInParameterHashes_OrSessionContext_OrInvokerValues()
    {
        var f = Active(scoped: false, total: 100);

        await RunWith(f, Inputs("3"));

        await f.Invoker.Received(1).ExecuteReadAsync(
            Arg.Any<ProcedureDefinition>(),
            Arg.Is<IReadOnlyDictionary<string, object?>>(d => !d.ContainsKey("limit") && !d.ContainsKey("Limit") && d.Count == 0),
            Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        f.AuditEntries.ShouldNotBeEmpty();
        f.AuditEntries.Single(a => a.EventType == "PROCEDURE_EXECUTE").DetailsJson.ShouldContain("\"parameterHashes\":{}");
    }

    [Fact]
    public async Task Limit_DoesNotMaskUnknownParameterCheck()
    {
        var f = Active(scoped: false, total: 100);
        var inputs = Inputs("3");
        inputs["bogus"] = "x";

        var ex = await Should.ThrowAsync<ArgumentException>(() => RunWith(f, inputs));

        ex.Message.ShouldStartWith("Unknown parameter 'bogus'");
    }

    // ---------- WP-B: reserved name in the parsers and the catalog ----------

    [Theory]
    [InlineData("-- @param limit int optional")]
    [InlineData("-- @param Limit int required")]
    [InlineData("-- @param LIMIT nvarchar(10) optional")]
    [InlineData("-- @context tenant_id -> @limit")]
    [InlineData("-- @context user_sid -> @Limit")]
    [InlineData("-- @context purpose -> LIMIT")]
    [Trait("Category", "Security")]
    public void T_PROC152I2_10_Parser_Header_ParameterNamedLimit_Rejected(string line)
    {
        string header = UnscopedHeader + "\n" + line;

        var ex = Should.Throw<FormatException>(() => ProcedureDefinitionParser.Parse(header, "x", false, 60));

        ex.Message.ShouldBe("Parameter name 'limit' is reserved.");
    }

    [Theory]
    [InlineData("parameters:\n  - name: limit\n    type: int")]
    [InlineData("parameters:\n  - name: '@Limit'\n    type: int")]
    [InlineData("parameters:\n  - name: LIMIT\n    context: tenant_id")]
    [InlineData("context:\n  tenant_id: '@limit'")]
    [InlineData("context_bindings:\n  - key: user_sid\n    parameter: Limit")]
    [Trait("Category", "Security")]
    public void T_PROC152I2_10_Parser_Yaml_ParameterNamedLimit_Rejected(string fragment)
    {
        string yaml = "name: x\nprocedure: tem.p\nvalidation: declared\nresult_table: md.crane\n" + fragment + "\n";

        var ex = Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(yaml, "x", false, 30));

        ex.Message.ShouldBe("Parameter name 'limit' is reserved.");
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("@Limit")]
    [InlineData("LIMIT")]
    [Trait("Category", "Security")]
    public void T_PROC152I2_10_Catalog_ParameterNamedLimit_Rejected(string dbParameterName)
    {
        var errors = StoredProcedureCatalogValidator.ReservedParameterErrors([dbParameterName]);

        errors.ShouldBe(["Procedure parameter '@limit' uses the reserved name 'limit'."]);
    }

    [Fact]
    public void Catalog_OtherParameterNames_NotRejected()
    {
        StoredProcedureCatalogValidator.ReservedParameterErrors(["limits", "p_limit", "CustomerId"]).ShouldBeEmpty();
    }

    // ---------- WP-B: OpenAPI ----------

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_17_OpenApi_LimitParameter_OnlyForVisibleProcedures_NoScanCap()
    {
        var registry = new InMemoryProcedureRegistry();
        var visible = ProcedureDefinitionParser.Parse(UnscopedHeader, "x", false, 60);
        var hidden = ProcedureDefinitionParser.Parse(UnscopedHeader.Replace("get_cranes", "hidden_cranes"), "y", false, 60);
        foreach (var d in new[] { visible, hidden })
        {
            registry.Register(d);
            registry.MarkActive(d.Name, new ProcedureValidationResult(true, [], ["serial_number"], ["md.crane"], new Dictionary<string, string>()));
        }

        var service = Substitute.For<IProcedureExecutionService>();
        service.IsVisibleAsync(Arg.Any<RegisteredProcedure>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.ArgAt<RegisteredProcedure>(0).Definition.Name == "get_cranes"));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = User() };
        var options = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions { Procedures = new ProcedureEndpointsOptions { MaxRows = 321, MaxScanRows = 4321 } }
        });

        var result = await Autheris.Api.Endpoints.ProcedureEndpointRoutes.HandleOpenApi(http, registry, service, options);

        var ok = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Dictionary<string, object>>>();
        var paths = (Dictionary<string, object>)ok.Value!["paths"];
        paths.Keys.Count.ShouldBe(1);
        string json = JsonSerializer.Serialize(ok.Value);
        json.ShouldContain("\"name\":\"limit\"");
        json.ShouldContain("\"maximum\":321");
        json.ShouldContain("\"minimum\":1");
        json.ShouldNotContain("4321");
        json.ShouldNotContain("hidden_cranes");
    }

    // ---------- WP-C: write-mode guard (SEC-PROC152I2-10) ----------

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("3")]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_13_WriteModeDefinition_InRegistry_Returns404_SameBodyAsUnknown(string? limit)
    {
        var f = Active(scoped: false, total: 100, mode: ProcedureMode.Write);
        var service = f.Create();
        var inputs = limit == null ? null : Inputs(limit);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => service.ExecuteAsync("get_cranes", inputs, User(), Tenant));
        var unknown = await Should.ThrowAsync<KeyNotFoundException>(() => service.ExecuteAsync("nope", null, User(), Tenant));

        ex.Message.ShouldBe(unknown.Message);
        f.Budgets.ShouldBeEmpty();
        await f.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
        f.AuditEntries.Count(e => JsonSerializer.Serialize(e).Contains("write-mode")).ShouldBe(1);
    }

    // ---------- WP-D: declared error map (D-7, SEC-PROC152I2-11, -12) ----------

    private const string MapYaml = """
        name: get_cranes
        procedure: tem.usp_Cranes
        result_table: md.crane
        error_codes:
          - number: 50001
            code: CRANE_LOCKED
            message: The crane is locked.
          - sqlstate: ZZ001
            code: CRANE_GONE
            message: The crane no longer exists.
        """;

    private static string YamlWithErrorCodes(string entries) =>
        "name: get_cranes\nprocedure: tem.usp_Cranes\nresult_table: md.crane\nerror_codes:\n" + entries;

    [Fact]
    public void Parser_ErrorCodes_Valid_Parsed()
    {
        var def = ProcedureDefinitionParser.ParseYaml(MapYaml, "x", false, 60);

        def.ErrorCodes.Count.ShouldBe(2);
        def.ErrorCodes[0].ShouldBe(new ProcedureErrorCode(50001, null, "CRANE_LOCKED", "The crane is locked."));
        def.ErrorCodes[1].ShouldBe(new ProcedureErrorCode(null, "ZZ001", "CRANE_GONE", "The crane no longer exists."));
    }

    [Fact]
    public void Parser_NoErrorCodes_EmptyMap()
    {
        ProcedureDefinitionParser.ParseYaml("name: a\nprocedure: tem.usp_A\n", "x", false, 60).ErrorCodes.ShouldBeEmpty();
    }

    public static TheoryData<string> MalformedErrorCodes()
    {
        var data = new TheoryData<string>
        {
            "  - number: 50001\n    sqlstate: ZZ001\n    code: ABC\n    message: m\n",
            "  - code: ABC\n    message: m\n",
            "  - number: 49999\n    code: ABC\n    message: m\n",
            "  - number: 60000\n    code: ABC\n    message: m\n",
            "  - number: 50001\n    code: abc\n    message: m\n",
            "  - number: 50001\n    code: AB\n    message: m\n",
            "  - number: 50001\n    code: 1ABC\n    message: m\n",
            "  - number: 50001\n    code: ABC\n    message: ''\n",
            "  - number: 50001\n    code: ABC\n    message: " + new string('x', 201) + "\n",
            "  - number: 50001\n    code: ABC\n    message: \"hello {0}\"\n",
            "  - number: 50001\n    code: ABC\n    message: \"100%\"\n",
            "  - number: 50001\n    code: ABC\n    message: \"a}b\"\n",
            "  - number: 50001\n    code: ABC\n    message: \"line\\nbreak\"\n",
            "  - number: 50001\n    code: ABC\n    message: m\n  - number: 50001\n    code: DEF\n    message: m\n",
            "  - sqlstate: ZZ001\n    code: ABC\n    message: m\n  - sqlstate: ZZ001\n    code: DEF\n    message: m\n",
            "  - sqlstate: zz001\n    code: ABC\n    message: m\n",
            "  - sqlstate: ZZ00\n    code: ABC\n    message: m\n",
            string.Concat(Enumerable.Range(0, 65).Select(i => $"  - number: {50000 + i}\n    code: CODE_{i}\n    message: m\n"))
        };
        foreach (var cls in new[] { "00", "01", "02", "08", "28", "40", "53", "54", "55", "57", "58", "F0", "XX", "HV" })
        {
            data.Add($"  - sqlstate: {cls}123\n    code: ABC\n    message: m\n");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedErrorCodes))]
    [Trait("Category", "Security")]
    public void T_PROC152I2_15_Parser_ErrorCodes_Malformed_Rejected(string entries)
    {
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(YamlWithErrorCodes(entries), "x", false, 60));
    }

    [Fact]
    public void Parser_ErrorCodes_Exactly64_Accepted()
    {
        var entries = string.Concat(Enumerable.Range(0, 64).Select(i => $"  - number: {50000 + i}\n    code: CODE_{i}\n    message: m\n"));

        ProcedureDefinitionParser.ParseYaml(YamlWithErrorCodes(entries), "x", false, 60).ErrorCodes.Count.ShouldBe(64);
    }

    private sealed class FakeNumberException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class FakeSqlStateException(string sqlState, string message) : DbException(message)
    {
        public override string SqlState { get; } = sqlState;
    }

    private static ProcedureDefinition MapDefinition() => ProcedureDefinitionParser.ParseYaml(MapYaml, "x", false, 60);

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152I2_15_DeclaredSqlState_TreatedAsBusiness_UndeclaredIsNot()
    {
        var def = MapDefinition();

        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlStateException("ZZ001", "secret"), def, out var number, out var state).ShouldBeTrue();
        number.ShouldBeNull();
        state.ShouldBe("ZZ001");
        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlStateException("ZZ002", "secret"), def, out _, out _).ShouldBeFalse();
        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlStateException("ZZ001", "secret"), null, out _, out _).ShouldBeFalse();
        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlStateException("P0001", "secret"), def, out _, out _).ShouldBeTrue();
        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlStateException("40001", "secret"), def, out _, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152I2_15_NumberAndSqlStateEntries_DoNotCrossMatch()
    {
        var def = MapDefinition() with { ErrorCodes = [new ProcedureErrorCode(null, "ZZ001", "A_B", "m")] };

        // A number exception never matches a sqlstate entry, and a sqlstate exception never matches a number entry.
        MssqlProcedureInvoker.TryGetBusinessError(new FakeNumberException(1205, "x"), def, out _, out _).ShouldBeFalse();
        var numberDef = def with { ErrorCodes = [new ProcedureErrorCode(1205, null, "A_B", "m")] };
        MssqlProcedureInvoker.TryGetBusinessError(new FakeNumberException(1205, "x"), numberDef, out _, out _).ShouldBeFalse();
        MssqlProcedureInvoker.TryGetBusinessError(new FakeSqlStateException("1205X", "x"), numberDef, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task BusinessError_Mapped_Returns422WithDeclaredCodeAndMessage()
    {
        var f = Active(scoped: false, total: 1, errorCodes: MapDefinition().ErrorCodes);
        f.Invoker.ExecuteReadAsync(default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new ProcedureBusinessException(50001, null));

        var ex = await Should.ThrowAsync<ProcedureBusinessException>(() => Run(f));

        ex.MappedCode.ShouldBe("CRANE_LOCKED");
        ex.MappedMessage.ShouldBe("The crane is locked.");
        ex.TraceId.ShouldNotBeNull();
        var details = f.AuditEntries.Single(a => a.EventType == "PROCEDURE_REJECTED").DetailsJson;
        details.ShouldContain("CRANE_LOCKED");
        details.ShouldNotContain("The crane is locked.");
    }

    [Fact]
    public async Task BusinessError_MappedBySqlState_ResolvesEntry()
    {
        var f = Active(scoped: false, total: 1, errorCodes: MapDefinition().ErrorCodes);
        f.Invoker.ExecuteReadAsync(default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new ProcedureBusinessException(null, "ZZ001"));

        var ex = await Should.ThrowAsync<ProcedureBusinessException>(() => Run(f));

        ex.MappedCode.ShouldBe("CRANE_GONE");
        ex.MappedMessage.ShouldBe("The crane no longer exists.");
    }

    [Fact]
    public async Task BusinessError_NotInMap_ReturnsGenericProcedureRejected()
    {
        var f = Active(scoped: false, total: 1, errorCodes: MapDefinition().ErrorCodes);
        f.Invoker.ExecuteReadAsync(default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new ProcedureBusinessException(50099, null));

        var ex = await Should.ThrowAsync<ProcedureBusinessException>(() => Run(f));

        ex.MappedCode.ShouldBeNull();
        ex.MappedMessage.ShouldBeNull();
        f.AuditEntries.Single(a => a.EventType == "PROCEDURE_REJECTED").DetailsJson.ShouldContain("\"mappedCode\":null");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_14_UndeclaredSqlState_DbException_StaysOn500Path_NoMapCode()
    {
        var f = Active(scoped: false, total: 1, errorCodes: MapDefinition().ErrorCodes);
        f.Invoker.ExecuteReadAsync(default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new FakeSqlStateException("ZZ002", "Value 4711 in dbo.Secret"));

        var ex = await Should.ThrowAsync<FakeSqlStateException>(() => Run(f));

        ex.ShouldNotBeAssignableTo<ProcedureBusinessException>();
        f.AuditEntries.ShouldNotContain(a => a.EventType == "PROCEDURE_REJECTED");
        f.AuditEntries.Single(a => a.EventType == "PROCEDURE_FAILED").DetailsJson.ShouldNotContain("4711");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_14_BusinessError_DbTextWithValueAndTable_NeverInResponseOrAudit(bool mapped)
    {
        const string secret = "Value 4711 in dbo.Secret";
        var def = MapDefinition();
        var f = Active(scoped: false, total: 1, errorCodes: def.ErrorCodes);
        // The real invoker converts the DbException into number/SQLSTATE only; verify that seam first.
        MssqlProcedureInvoker.TryGetBusinessError(new FakeNumberException(mapped ? 50001 : 50099, secret), def, out var number, out var state).ShouldBeTrue();
        f.Invoker.ExecuteReadAsync(default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new ProcedureBusinessException(number, state));

        var ex = await Should.ThrowAsync<ProcedureBusinessException>(() => Run(f));

        ex.Message.ShouldNotContain("4711");
        (ex.MappedMessage ?? string.Empty).ShouldNotContain("4711");
        foreach (var entry in f.AuditEntries)
        {
            (entry.DetailsJson ?? string.Empty).ShouldNotContain("4711");
            (entry.DetailsJson ?? string.Empty).ShouldNotContain("dbo.Secret");
        }

        ex.MappedCode.ShouldBe(mapped ? "CRANE_LOCKED" : null);
    }

    [Fact]
    public async Task Route_MappedBusinessError_Writes422WithDeclaredCodeAndMessage()
    {
        var service = Substitute.For<IProcedureExecutionService>();
        service.ExecuteAsync(default!, default, default!, default, default)
            .ThrowsAsyncForAnyArgs(new ProcedureBusinessException(50001, null, "abc123", "CRANE_LOCKED", "The crane is locked."));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = User() };
        http.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging().BuildServiceProvider();
        http.Response.Body = new MemoryStream();

        await Autheris.Api.Endpoints.ProcedureEndpointRoutes.HandleGet(
            "x", http, service, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        http.Response.StatusCode.ShouldBe(422);
        http.Response.Body.Position = 0;
        new StreamReader(http.Response.Body).ReadToEnd()
            .ShouldBe("{\"error\":\"The crane is locked.\",\"code\":\"CRANE_LOCKED\",\"traceId\":\"abc123\"}");
    }

    // ---------- WP-E: ddl_hash requirement for declared mode ----------

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private const string DeclaredYamlNoHash = "name: x\nprocedure: api.usp_X\nvalidation: declared\nresult_table: md.crane\noutputs:\n  - {name: a, type: int}\n";
    private const string DeclaredYamlWithHash = DeclaredYamlNoHash + "integrity:\n  ddl_hash: \"sha256:abc12345\"\n";
    private const string CatalogYamlNoHash = "name: x\nprocedure: api.usp_X\nresult_table: md.crane\n";

    private static (ProcedureDefinitionLoader Loader, InMemoryProcedureRegistry Registry, CapturingLogger<ProcedureDefinitionLoader> Log, string Dir) CreateDdlLoader(
        string? environmentName, bool? requireDdlHash = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "autheris-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    Directory = dir,
                    AllowedSchemas = ["api"],
                    EnableHotReload = false,
                    AllowDeclaredValidation = true,
                    RequireDdlHashForDeclared = requireDdlHash
                }
            }
        });
        IHostEnvironment? env = null;
        if (environmentName != null)
        {
            env = Substitute.For<IHostEnvironment>();
            env.EnvironmentName.Returns(environmentName);
        }

        var registry = new InMemoryProcedureRegistry();
        var log = new CapturingLogger<ProcedureDefinitionLoader>();
        return (new ProcedureDefinitionLoader(registry, options, env, log), registry, log, dir);
    }

    [Fact]
    public void Declared_NoDdlHash_LogsWarningNamingProcedure()
    {
        var (loader, registry, log, dir) = CreateDdlLoader("Development");
        try
        {
            string file = Path.Combine(dir, "x.proc.yaml");
            File.WriteAllText(file, DeclaredYamlNoHash);

            loader.TryLoadFile(file, dir).ShouldBeTrue();

            registry.GetAll().Count.ShouldBe(1);
            log.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("Procedure 'x' uses validation: declared without integrity.ddl_hash")).ShouldBe(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Declared_NoDdlHash_RequireTrue_NotRegistered()
    {
        var (loader, registry, _, dir) = CreateDdlLoader("Development", requireDdlHash: true);
        try
        {
            string file = Path.Combine(dir, "x.proc.yaml");
            File.WriteAllText(file, DeclaredYamlNoHash);

            loader.TryLoadFile(file, dir).ShouldBeFalse();

            registry.GetAll().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Declared_NoDdlHash_HotReload_WarnsAgain()
    {
        var (loader, _, log, dir) = CreateDdlLoader("Development");
        try
        {
            string file = Path.Combine(dir, "x.proc.yaml");
            File.WriteAllText(file, DeclaredYamlNoHash);

            loader.TryLoadFile(file, dir);
            loader.TryLoadFile(file, dir);

            log.Entries.Count(e => e.Message.Contains("without integrity.ddl_hash")).ShouldBe(2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Catalog_NoDdlHash_NoWarning()
    {
        var (loader, registry, log, dir) = CreateDdlLoader("Production", requireDdlHash: true);
        try
        {
            string file = Path.Combine(dir, "x.proc.yaml");
            File.WriteAllText(file, CatalogYamlNoHash);

            loader.TryLoadFile(file, dir).ShouldBeTrue();

            registry.GetAll().Count.ShouldBe(1);
            log.Entries.ShouldNotContain(e => e.Message.Contains("ddl_hash"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Declared_WithDdlHash_RequireTrue_Registers()
    {
        var (loader, registry, _, dir) = CreateDdlLoader("Production");
        try
        {
            string file = Path.Combine(dir, "x.proc.yaml");
            File.WriteAllText(file, DeclaredYamlWithHash);

            loader.TryLoadFile(file, dir).ShouldBeTrue();

            registry.GetAll().Count.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RequireDdlHash_Null_TrueOutsideDevelopment_FalseInDevelopment()
    {
        var dev = CreateDdlLoader("Development");
        var prod = CreateDdlLoader("Production");
        try
        {
            File.WriteAllText(Path.Combine(dev.Dir, "x.proc.yaml"), DeclaredYamlNoHash);
            File.WriteAllText(Path.Combine(prod.Dir, "x.proc.yaml"), DeclaredYamlNoHash);

            dev.Loader.TryLoadFile(Path.Combine(dev.Dir, "x.proc.yaml"), dev.Dir).ShouldBeTrue();
            prod.Loader.TryLoadFile(Path.Combine(prod.Dir, "x.proc.yaml"), prod.Dir).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dev.Dir, true);
            Directory.Delete(prod.Dir, true);
        }
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("QA-Custom")]
    [InlineData("")]
    [InlineData(null)]
    [Trait("Category", "Security")]
    public void T_PROC152I2_16_DdlHash_Required_ForStagingProductionAndUnknownEnvironment_NotDevelopment(string? environmentName)
    {
        var (loader, registry, _, dir) = CreateDdlLoader(environmentName);
        try
        {
            string file = Path.Combine(dir, "x.proc.yaml");
            File.WriteAllText(file, DeclaredYamlNoHash);

            loader.TryLoadFile(file, dir).ShouldBeFalse();

            registry.GetAll().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152I2_16_Declared_NoHash_HotReload_Unregisters_PreviousVersion()
    {
        var (loader, registry, _, dir) = CreateDdlLoader("Production");
        try
        {
            string file = Path.Combine(dir, "x.proc.yaml");
            File.WriteAllText(file, DeclaredYamlWithHash);
            loader.TryLoadFile(file, dir).ShouldBeTrue();
            registry.GetAll().Count.ShouldBe(1);

            File.WriteAllText(file, DeclaredYamlNoHash);
            loader.TryLoadFile(file, dir).ShouldBeFalse();

            registry.GetAll().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_PROC152I2_16_Explicit_RequireFalse_Production_LogsStartupWarning()
    {
        var (loader, _, log, dir) = CreateDdlLoader("Production", requireDdlHash: false);
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.proc.yaml"), DeclaredYamlNoHash);

            loader.LoadFromDirectory();

            var startup = log.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("RequireDdlHashForDeclared")).ToList();
            startup.Count.ShouldBe(1);
            startup[0].Message.ShouldNotContain("x.proc");
            startup[0].Message.ShouldNotContain("api.usp_X");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RequireDdlHash_ExplicitFalse_Development_NoStartupWarning()
    {
        var (loader, _, log, dir) = CreateDdlLoader("Development", requireDdlHash: false);
        try
        {
            loader.LoadFromDirectory();

            log.Entries.ShouldNotContain(e => e.Message.Contains("RequireDdlHashForDeclared"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
