namespace Autheris.Tests.Unit.Sql.TimeZones;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.TimeZones;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static Autheris.Tests.Unit.Sql.ClientListFixture;

/// <summary>REQ-SQL-130: the conversion through the whole governed pipeline (state, mapper, audit, logs).</summary>
public sealed class ConvertTimeZoneWebSqlTests
{
    private const string Good = "SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(status AS timestamp)) AS t FROM orders";
    private const string BadZone = "SELECT convert_timezone('Europe/Evil', 'UTC', CAST(status AS timestamp)) AS t FROM orders";

    private sealed class FixedState(bool available) : ITimeZoneConversionState
    {
        public bool IsAvailable(TargetSqlDialect dialect) => available;
    }

    private static Task Validate(ClientListFixture f, string sql) =>
        f.Service.ValidateAsync(sql, User(), new TenantId(TenantA));

    private static string Message(Exception ex)
    {
        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        return $"{mapped.Status} {mapped.Code} {mapped.Title}";
    }

    // ---- T-24 ----

    [Fact]
    public async Task T24_SqliteIsUnsupportedWithTheFixedBodyThroughTheMapper()
    {
        await using var f = await CreateAsync(timeZoneState: new FixedState(true));
        var ex = await Should.ThrowAsync<SqlTimeZoneException>(() => f.QueryAsync(Good, null));

        ex.Code.ShouldBe(GovernedSqlErrorCodes.FunctionUnsupportedDialect);
        Message(ex).ShouldBe("501 SQL_FUNCTION_UNSUPPORTED_DIALECT The function is not supported for this data source dialect.");
    }

    // ---- T-23: the state decides, fail closed ----

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public async Task T23_AnAvailableStateLetsTheRewriterPassTheFunction(string provider)
    {
        await using var f = await CreateAsync(provider: provider, timeZoneState: new FixedState(true));
        await Should.NotThrowAsync(() => Validate(f, Good));

        var invalid = await Should.ThrowAsync<SqlTimeZoneException>(() => Validate(f, BadZone));
        invalid.Code.ShouldBe(GovernedSqlErrorCodes.TimeZoneInvalid);
        Message(invalid).ShouldBe("400 SQL_TIMEZONE_INVALID The time zone is not supported.");
    }

    [Theory]
    [InlineData("SELECT CAST(status AS timestamp) AT TIME ZONE 'UTC' AS t FROM orders")]
    [InlineData("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(status AS timestamp)) AS t, CAST(status AS timestamp) AT TIME ZONE 'UTC' AS u FROM orders")]
    [InlineData("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(status AS timestamp) AT TIME ZONE 'UTC') AS t FROM orders")]
    [Trait("Category", "Security")]
    public async Task ST_130_12_TheOperatorFormIsRejectedByThePreAnalysis(string sql)
    {
        await using var f = await CreateAsync(timeZoneState: new FixedState(true));
        await Should.ThrowAsync<TrinoSqlEngine.Ast.Builder.AstBuildException>(() => Validate(f, sql));
    }

    [Theory]
    [InlineData("PostgreSql", false)]
    [InlineData("PostgreSql", true)]
    [InlineData("SqlServer", false)]
    [Trait("Category", "Security")]
    public async Task T23_AnUnavailableOrMissingStateIsRefusedStatically(string provider, bool missing)
    {
        await using var f = await CreateAsync(provider: provider, timeZoneState: missing ? null : new FixedState(false));
        var ex = await Should.ThrowAsync<SqlTimeZoneException>(() => Validate(f, Good));
        ex.Code.ShouldBe(GovernedSqlErrorCodes.TimeZoneUnavailable);
        Message(ex).ShouldBe("503 SQL_TIMEZONE_UNAVAILABLE Time zone conversion is temporarily unavailable.");
    }

    [Fact]
    public void T23_TheFactoryDefaultIsFalseAndOnlyTheRewriterPathPassesTrue()
    {
        var policy = new TrinoSqlEngine.GovernancePolicy
        {
            RowFilters = new NoRowFilters(),
            Masks = new NoMasks(),
            Catalog = new TrinoSqlEngine.Governance.InMemoryTableCatalog([], "dbo"),
            Tenant = new TrinoSqlEngine.Governance.TenantBinding("__autheris_tenant", "t", SqlParameterType.String)
        };
        var options = new WebSqlOptions();
        var functions = new HashSet<string> { "lower" };

        CompileRequestFactory.Create(policy, TargetSqlDialect.PostgreSql, options, false, 10, functions).TimeZoneConversionAvailable.ShouldBeFalse();
        CompileRequestFactory.Create(policy, TargetSqlDialect.PostgreSql, options, false, 10, functions, null, true).TimeZoneConversionAvailable.ShouldBeTrue();
    }

    [Fact]
    public void T23_NoProductionCodeBuildsACompileRequestOutsideTheFactory()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "Autheris.sln"))) root = root.Parent;
        var users = System.IO.Directory.GetFiles(System.IO.Path.Combine(root!.FullName, "src"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => System.IO.File.ReadAllText(p).Contains("new CompileRequest\n", StringComparison.Ordinal) || System.IO.File.ReadAllText(p).Contains("new CompileRequest ", StringComparison.Ordinal)
                        || System.IO.File.ReadAllText(p).Contains("new CompileRequest{", StringComparison.Ordinal))
            .Select(p => System.IO.Path.GetRelativePath(root.FullName, p).Replace('\\', '/'))
            .ToList();
        users.ShouldBe(["src/Autheris.Application/Sql/Policy/CompileRequestFactory.cs"]);
    }

    private sealed class NoRowFilters : TrinoSqlEngine.Governance.IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TrinoSqlEngine.Governance.TableIdentity table) => false;

        public TrinoSqlEngine.Governance.PolicyPredicate GetPredicate(TrinoSqlEngine.Governance.TableIdentity table) => TrinoSqlEngine.Governance.PolicyPredicate.DenyAll;
    }

    private sealed class NoMasks : TrinoSqlEngine.Governance.IColumnMaskProvider
    {
        public bool HasMask(TrinoSqlEngine.Governance.TableIdentity table, string column) => false;

        public TrinoSqlEngine.Governance.MaskSpec GetMask(TrinoSqlEngine.Governance.TableIdentity table, string column) => throw new InvalidOperationException();
    }

    // ---- T-28 / ST-130-18 audit and logs ----

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_130_18_ARejectedConversionIsAuditedWithTheCodeOnlyAndNeverLogsTheZone()
    {
        var log = new ListCapturingLogger<Autheris.Application.Sql.Services.GovernedSqlExecutionService>();
        await using var f = await CreateAsync(logger: log, provider: "PostgreSql", timeZoneState: new FixedState(true));

        var ex = await Should.ThrowAsync<SqlTimeZoneException>(() => f.QueryAsync(BadZone, null));
        ex.Code.ShouldBe(GovernedSqlErrorCodes.TimeZoneInvalid);
        ex.ToString().ShouldNotContain("Evil");

        var denied = f.Audit.Single(e => e.EventType == "WEBSQL_QUERY_DENIED");
        denied.DetailsJson!.ShouldContain(GovernedSqlErrorCodes.TimeZoneInvalid);
        foreach (string leak in new[] { "Europe/Evil", "Evil", "Europe", "Berlin" })
        {
            denied.DetailsJson.ShouldNotContain(leak);
            log.Entries.ShouldAllBe(line => !line.Contains(leak, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_130_18_NoCatchBetweenTheValidatorAndTheMapperChangesTheStatus()
    {
        await using var f = await CreateAsync(provider: "PostgreSql", timeZoneState: new FixedState(true));
        foreach (var (sql, code, status) in new[]
                 {
                     (BadZone, "SQL_TIMEZONE_INVALID", 400),
                     ("SELECT convert_timezone('UTC', 'Europe/Berlin') AS t FROM orders", "SQL_TIMEZONE_ARGUMENT", 422)
                 })
        {
            var ex = await Should.ThrowAsync<Exception>(() => f.QueryAsync(sql, null));
            ex.ShouldBeOfType<SqlTimeZoneException>();
            SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
            (mapped.Status, mapped.Code).ShouldBe((status, code));
        }

        // an accepted conversion appears in no audit field either
        f.Audit.ShouldAllBe(e => e.DetailsJson == null || !e.DetailsJson.Contains("Europe/Berlin", StringComparison.Ordinal));
    }
}
