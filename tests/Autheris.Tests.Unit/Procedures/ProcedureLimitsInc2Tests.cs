using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
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

    private static Fixture Active(bool scoped, int total, int maxRows = 10, int? maxScanRows = null)
    {
        var f = new Fixture { Procedures = new ProcedureEndpointsOptions { MaxRows = maxRows, MaxScanRows = maxScanRows } };
        var def = ProcedureDefinitionParser.Parse(scoped ? ScopedHeader : UnscopedHeader, "x", false, 60);
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
}
