namespace Autheris.Tests.Unit.Governance;

using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Query analysis of an ad-hoc SQL statement reports the verdict execution gives for the same principal and statement: same stable
/// code, fixed message, position, subject and hint, and the statement is valid only when execution accepts it.
/// </summary>
public sealed class QueryAnalysisSqlCompileParityTests
{
    private const string NestedComment = "SELECT id FROM demo.main.t /* a /* b */ c */";
    private const string Tablesample = "SELECT id FROM demo.main.t TABLESAMPLE BERNOULLI (10)";
    private const string MaskedFilter = "SELECT id FROM demo.main.t WHERE masked_col = 'x'";
    private const string MaskedAggregate = "SELECT MAX(masked_col) FROM demo.main.t";
    private const string PgSleep = "SELECT pg_sleep(1) FROM demo.main.t";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static SqlCompileParityHarness Create() => new(consents: SqlCompileParityHarness.AllowConsent());

    private static QueryValidationDiagnostic Blocking(QueryAnalysisResult result, string code) =>
        result.ValidationReport.Diagnostics.Single(d => d.Code == code && d.BlocksExecution);

    [Fact]
    public async Task NestedBlockComment_SameCodeAsExecution_CommentNotPermitted()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        var result = await h.AnalyzeAsync(NestedComment, user);

        var execution = await h.ExecutionRefusalAsync(NestedComment, user);
        execution.ShouldNotBeNull().Code.ShouldBe("SQL_COMMENT_NOT_PERMITTED");
        Blocking(result, execution.Value.Code).ShouldNotBeNull();
        result.ValidationReport.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task PlainComment_ValidInBoth()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();
        const string sql = "SELECT id FROM demo.main.t -- a note";

        var result = await h.AnalyzeAsync(sql, user);

        (await h.ExecutionRefusalAsync(sql, user)).ShouldBeNull();
        result.ValidationReport.Diagnostics.ShouldNotContain(d => d.BlocksExecution);
        result.ValidationReport.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task FunctionMissingInTargetDialect_FunctionNotPermitted()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();
        var postgres = TrinoSqlEngine.SqlFunctionAllowlists.Build(TrinoSqlEngine.TargetSqlDialect.PostgreSql, null);
        var missing = SqlDialectMapper.AllSafeDialectFunctions
            .Where(f => !postgres.Contains(f) && f.All(c => char.IsLetterOrDigit(c) || c == '_'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .First();
        var sql = $"SELECT {missing}() FROM demo.main.t";

        var result = await h.AnalyzeAsync(sql, user);

        var execution = await h.ExecutionRefusalAsync(sql, user);
        execution.ShouldNotBeNull().Code.ShouldBe("SQL_FUNCTION_NOT_PERMITTED");
        var diagnostic = Blocking(result, execution.Value.Code);
        diagnostic.Function.ShouldBe(execution.Value.Subject?.Name);
        result.ValidationReport.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Tablesample_ConstructNotSupported_WithConstructAndLocation()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        var result = await h.AnalyzeAsync(Tablesample, user);

        var execution = await h.ExecutionRefusalAsync(Tablesample, user);
        execution.ShouldNotBeNull().Code.ShouldBe("SQL_CONSTRUCT_NOT_SUPPORTED");
        var diagnostic = Blocking(result, execution.Value.Code);
        diagnostic.Construct.ShouldBe(execution.Value.Subject?.Name);
        diagnostic.Construct.ShouldNotBeNull();
        diagnostic.Location.ShouldNotBeNull();
        result.ValidationReport.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task AggregateOverMaskedColumn_AggregationOnMaskedColumn_Blocking()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        var result = await h.AnalyzeAsync(MaskedAggregate, user);

        (await h.ExecutionRefusalAsync(MaskedAggregate, user)).ShouldNotBeNull().Code.ShouldBe("AGGREGATION_ON_MASKED_COLUMN");
        Blocking(result, "AGGREGATION_ON_MASKED_COLUMN").ShouldNotBeNull();
        result.ValidationReport.IsValid.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FilterOnMaskedColumn_BlocksExecution_IsValidFalse()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        var result = await h.AnalyzeAsync(MaskedFilter, user);

        (await h.ExecutionRefusalAsync(MaskedFilter, user)).ShouldNotBeNull().Code.ShouldBe("FILTER_ON_MASKED_COLUMN");
        Blocking(result, "FILTER_ON_MASKED_COLUMN").ShouldNotBeNull();
        // the informational diagnostic stays next to the blocking one (the consent request is still offered)
        result.ValidationReport.Diagnostics.ShouldContain(d => d.Code == QueryDiagnosticCodes.MaskedColumnInFilter && !d.BlocksExecution);
        result.ValidationReport.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidStatement_NoCompileDiagnostic_IsValid()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();
        const string sql = "SELECT id, region FROM demo.main.t";

        var result = await h.AnalyzeAsync(sql, user);

        (await h.ExecutionRefusalAsync(sql, user)).ShouldBeNull();
        result.ValidationReport.Diagnostics.ShouldNotContain(d => d.BlocksExecution);
        result.ValidationReport.IsValid.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Simulation_SkipsCompileCheck_WithInformationalDiagnostic()
    {
        var check = new FakeCompileParityCheck(new SqlCompileParityFailure("SQL_POLICY_DENIED", "Denied.", null, null, null, null, null));
        var h = new SqlCompileParityHarness(check, SqlCompileParityHarness.AllowConsent("S-1-TARGET"));
        var caller = SqlCompileParityHarness.Caller(roles: "governance:simulate");

        var result = await h.AnalyzeAsync("SELECT id FROM demo.main.t", caller, SqlCompileParityHarness.Target("S-1-TARGET"));

        check.Calls.ShouldBe(0);
        var info = result.ValidationReport.Diagnostics.Single(d => d.Code == QueryDiagnosticCodes.CompileCheckNotRunForSimulation);
        info.BlocksExecution.ShouldBeFalse();
        info.Column.ShouldBeNull();
        result.ValidationReport.IsValid.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Simulation_UsesTargetVisibility_NotCaller()
    {
        // the caller sees the table, the target has no consent: the catalog stage answers for the target
        var check = new FakeCompileParityCheck();
        var h = new SqlCompileParityHarness(check, SqlCompileParityHarness.AllowConsent());
        var caller = SqlCompileParityHarness.Caller(roles: "governance:simulate");

        var result = await h.AnalyzeAsync("SELECT id FROM demo.main.t", caller, SqlCompileParityHarness.Target("S-1-OTHER"));

        check.Calls.ShouldBe(0);
        result.AccessRequirements.MissingConsents.ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Simulation_WithoutPrivilege_Rejected_BeforeCompile()
    {
        var check = new FakeCompileParityCheck();
        var h = new SqlCompileParityHarness(check, SqlCompileParityHarness.AllowConsent());
        var target = QueryAnalysisService.CreateSimulatedTargetPrincipal("S-1-TARGET", null, "tenant-a");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => h.AnalyzeAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller(), target));

        check.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Simulation_TargetInOtherTenant_Rejected()
    {
        var check = new FakeCompileParityCheck();
        var h = new SqlCompileParityHarness(check, SqlCompileParityHarness.AllowConsent());
        var caller = SqlCompileParityHarness.Caller(roles: "governance:simulate");
        var target = QueryAnalysisService.CreateSimulatedTargetPrincipal("S-1-TARGET", null, "tenant-b");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => h.AnalyzeAsync("SELECT id FROM demo.main.t", caller, target));

        check.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CompileRejection_WritesOnlyQueryAnalysisEvaluatedAudit()
    {
        var h = Create();

        await h.AnalyzeAsync(PgSleep, SqlCompileParityHarness.Caller());
        await h.AnalyzeAsync(MaskedFilter, SqlCompileParityHarness.Caller());

        h.Audit.Entries.Select(e => e.EventType).Distinct().ShouldBe([AuditEventTypes.QueryAnalysisEvaluated]);
        h.Audit.Entries.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CompileCheck_NoCostAdmission()
    {
        var h = Create();

        await h.AnalyzeAsync(PgSleep, SqlCompileParityHarness.Caller());
        await h.AnalyzeAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller());

        h.CostQuota.Calls.ShouldBeEmpty();
        h.Connections.CreateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EndpointAndProcedureItems_NotCompiled()
    {
        var check = new FakeCompileParityCheck();
        var h = new SqlCompileParityHarness(check, SqlCompileParityHarness.AllowConsent());
        var endpoint = new SqlEndpointDefinition(
            Name: "ep",
            Summary: "endpoint",
            RawSql: "SELECT id FROM demo.main.t",
            ReferencedTables: ["demo.main.t"]);
        var procedure = new ProcedureDefinition(
            Name: "sp",
            Summary: "proc",
            ProcedureName: "main.sp",
            Mode: ProcedureMode.Read,
            DataSource: "demo",
            Parameters: [],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: "demo.main.t",
            ClearedResultColumns: [],
            RequiredRoles: [],
            TimeoutSeconds: 30)
        {
            ReferencedTables = ["demo.main.t"]
        };

        await h.Analysis.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSqlEndpoint(endpoint), QueryAnalysisItem.FromProcedure(procedure, "SELECT id FROM demo.main.t")],
            SqlCompileParityHarness.Caller());

        check.Calls.ShouldBe(0);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(NestedComment)]
    [InlineData(Tablesample)]
    [InlineData(PgSleep)]
    [InlineData(MaskedFilter)]
    [InlineData(MaskedAggregate)]
    [InlineData("SELEC id FROM demo.main.t")]
    public async Task SameCodeAndFieldsAsExecution_ForEachMappedCode(string sql)
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        var result = await h.AnalyzeAsync(sql, user);

        var execution = (await h.ExecutionRefusalAsync(sql, user)).ShouldNotBeNull();
        var diagnostic = Blocking(result, execution.Code);
        diagnostic.Message.ShouldBe(execution.Title);
        diagnostic.Hint.ShouldBe(execution.Hint);
        diagnostic.Location.ShouldBe(execution.Line is { } line && execution.Column is { } column ? new QueryDiagnosticLocation(line, column) : null);
        diagnostic.Function.ShouldBe(execution.Subject is { Kind: SqlErrorSubject.Function } f ? f.Name : null);
        diagnostic.Construct.ShouldBe(execution.Subject is { Kind: SqlErrorSubject.Construct } c ? c.Name : null);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PolicyDenied_Diagnostic_HasNoNameOrPosition()
    {
        var h = Create();
        var stranger = SqlCompileParityHarness.Caller("S-1-STRANGER");

        var result = await h.AnalyzeAsync("SELECT id FROM demo.main.t", stranger);

        var diagnostic = Blocking(result, "SQL_POLICY_DENIED");
        diagnostic.Column.ShouldBeNull();
        diagnostic.Function.ShouldBeNull();
        diagnostic.Construct.ShouldBeNull();
        diagnostic.Location.ShouldBeNull();
        diagnostic.Hint.ShouldBeNull();
        diagnostic.Message.ShouldNotContain("demo");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenColumnTable_NeverReportsUnknownColumnName()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        var hidden = await h.AnalyzeAsync("SELECT secret_col FROM demo.main.t", user);
        var missing = await h.AnalyzeAsync("SELECT nope_col FROM demo.main.t", user);

        // a hidden column answers exactly like a missing one, and nothing the compile reports names a column of a table that hides one
        hidden.ValidationReport.Diagnostics.Select(d => (d.Code, d.BlocksExecution))
            .ShouldBe(missing.ValidationReport.Diagnostics.Select(d => (d.Code, d.BlocksExecution)));
        hidden.ValidationReport.IsValid.ShouldBeFalse();
        hidden.ValidationReport.Diagnostics.Where(d => d.BlocksExecution).ShouldAllBe(d => d.Column == null && d.Hint == null);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SyntaxError_DoesNotContainParserMessage()
    {
        var h = Create();

        var result = await h.AnalyzeAsync("SELEC id FROM demo.main.t", SqlCompileParityHarness.Caller());

        var diagnostic = Blocking(result, "SQL_SYNTAX_ERROR");
        diagnostic.Message.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage("SQL_SYNTAX_ERROR"));
        diagnostic.Message.ShouldNotContain("SELEC");
        result.ValidationReport.Diagnostics.ShouldNotContain(d => d.Code == QueryDiagnosticCodes.SyntaxError);
        result.ValidationReport.IsValid.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnmappedInternal_Rethrows_NeverValid()
    {
        var check = new FakeCompileParityCheck(exception: new InvalidOperationException("boom"));
        var h = new SqlCompileParityHarness(check, SqlCompileParityHarness.AllowConsent());

        await Should.ThrowAsync<InvalidOperationException>(() => h.AnalyzeAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task BlockingDiagnostic_ForcesIsValidFalse()
    {
        var check = new FakeCompileParityCheck(new SqlCompileParityFailure("SOME_FUTURE_CODE", "Refused.", null, null, null, null, null));
        var h = new SqlCompileParityHarness(check, SqlCompileParityHarness.AllowConsent());

        var result = await h.AnalyzeAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller());

        result.ValidationReport.Diagnostics.ShouldContain(d => d.Code == "SOME_FUTURE_CODE" && d.BlocksExecution);
        result.ValidationReport.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Diagnostic_Serializes_WithoutEmptyMembers()
    {
        var h = Create();

        var result = await h.AnalyzeAsync(PgSleep, SqlCompileParityHarness.Caller());

        var json = JsonSerializer.Serialize(Blocking(result, "SQL_FUNCTION_NOT_PERMITTED"), WebJson);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("blocksExecution").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("function").GetString().ShouldBe("pg_sleep");
        doc.RootElement.TryGetProperty("construct", out _).ShouldBeFalse();
    }
}
