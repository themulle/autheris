namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class McpResultMaskingTests
{
    private static readonly TableIdentifier Table = new("sales", "dbo", "customers");

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params string[] columns) =>
        [columns.ToDictionary(c => c, _ => (object?)1, StringComparer.OrdinalIgnoreCase)];

    private static TableAccessDecision Decision(params (string Column, ColumnAccessLevel Level)[] access) =>
        new(Table, true, access.ToDictionary(a => a.Column, a => a.Level, StringComparer.OrdinalIgnoreCase), null, []);

    // ---- scope ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Report_WithoutScope_IsNoOp()
    {
        Should.NotThrow(() => McpResultMasking.Report(true));
        using var scope = McpResultMasking.Begin();
        scope.Outcome.ShouldBeNull();
    }

    [Fact]
    public async Task Report_True_InAwaitedChildTask_IsVisibleToScope()
    {
        using var scope = McpResultMasking.Begin();
        await ReportAfterYieldAsync(true);
        scope.Outcome.ShouldBe(true);
    }

    [Fact]
    public async Task Report_FromTaskRun_AfterAwait_IsVisibleToScope()
    {
        using var scope = McpResultMasking.Begin();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = Task.Run(async () =>
        {
            await gate.Task;
            McpResultMasking.Report(true);
        });
        gate.SetResult();
        await child;
        scope.Outcome.ShouldBe(true);
    }

    [Fact]
    public void Report_False_StatesFalse()
    {
        using var scope = McpResultMasking.Begin();
        McpResultMasking.Report(false);
        scope.Outcome.ShouldBe(false);
    }

    [Fact]
    public void Report_TrueThenFalse_StaysTrue()
    {
        using var scope = McpResultMasking.Begin();
        McpResultMasking.Report(true);
        McpResultMasking.Report(false);
        scope.Outcome.ShouldBe(true);
    }

    [Fact]
    public void NoReport_OutcomeNull()
    {
        using var scope = McpResultMasking.Begin();
        scope.Outcome.ShouldBeNull();
    }

    [Fact]
    public void NestedScope_RestoresOuter()
    {
        using var outer = McpResultMasking.Begin();
        McpResultMasking.Report(false);
        using (var inner = McpResultMasking.Begin())
        {
            McpResultMasking.Report(true);
            inner.Outcome.ShouldBe(true);
        }

        outer.Outcome.ShouldBe(false);
        McpResultMasking.Report(true);
        outer.Outcome.ShouldBe(true);
    }

    [Fact]
    public async Task ConcurrentScopes_AreIsolated()
    {
        const int Count = 64;
        var reported = 0;
        var allReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = await Task.WhenAll(Enumerable.Range(0, Count).Select(async i =>
        {
            using var scope = McpResultMasking.Begin();
            await Task.Yield();
            McpResultMasking.Report(i % 2 == 0);

            // Every scope has reported before any scope reads its outcome, so all 64 holders are live at the same time.
            if (Interlocked.Increment(ref reported) == Count)
            {
                allReported.SetResult();
            }

            await allReported.Task;
            await Task.Yield();
            return (Expected: i % 2 == 0, Actual: scope.Outcome);
        }));

        results.ShouldAllBe(r => r.Actual == r.Expected);
    }

    [Fact]
    public void Scope_Disposed_OnException()
    {
        using var outer = McpResultMasking.Begin();
        McpResultMasking.Report(false);
        McpResultMaskingScope? inner = null;
        try
        {
            using var scope = McpResultMasking.Begin();
            inner = scope;
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
        }

        McpResultMasking.Report(true);
        inner!.Outcome.ShouldBeNull();
        outer.Outcome.ShouldBe(true);
    }

    [Fact]
    public void Scope_Disposed_WithoutOuter_ReportIsNoOp()
    {
        McpResultMaskingScope inner;
        using (var scope = McpResultMasking.Begin())
        {
            inner = scope;
        }

        Should.NotThrow(() => McpResultMasking.Report(true));
        inner.Outcome.ShouldBeNull();
    }

    [Fact]
    public void Report_AfterScopeDisposed_DoesNotAffectNextScope()
    {
        using (McpResultMasking.Begin())
        {
        }

        McpResultMasking.Report(true);
        using var next = McpResultMasking.Begin();
        next.Outcome.ShouldBeNull();
    }

    private static async Task ReportAfterYieldAsync(bool masked)
    {
        await Task.Yield();
        McpResultMasking.Report(masked);
    }

    // ---- classification ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("query_sql")]
    [InlineData("query_dataset")]
    [InlineData("query_graphql")]
    [InlineData("sample_rows")]
    [InlineData("call_procedure")]
    [InlineData("query_metrics")]
    [InlineData("invoke_api")]
    [InlineData("query_customers")]
    [InlineData("some_dynamic_tool")]
    public void IsGovernedDataTool_DataTools_True(string name) =>
        McpResultMasking.IsGovernedDataTool(new McpToolDefinition(name, "d", "{}", "query X { x }")).ShouldBeTrue();

    [Theory]
    [InlineData("list_datasets")]
    [InlineData("describe_dataset")]
    [InlineData("describe_my_access")]
    [InlineData("search_catalog")]
    [InlineData("find_relevant_tables")]
    [InlineData("get_table_schema")]
    [InlineData("get_my_permissions")]
    [InlineData("list_datasources")]
    [InlineData("list_containers")]
    [InlineData("get_data_lineage")]
    [InlineData("describe_api")]
    [InlineData("list_metrics")]
    [InlineData("describe_metric")]
    [InlineData("request_access")]
    [InlineData("simulate_query")]
    [InlineData("get_golden_queries")]
    [InlineData("search_docs")]
    [InlineData("admin_plan_access")]
    public void IsGovernedDataTool_BuiltInMetadataTools_False(string name) =>
        McpResultMasking.IsGovernedDataTool(new McpToolDefinition(name, "d", "{}", name)).ShouldBeFalse();

    [Fact]
    public void IsGovernedDataTool_EveryRegisteredTool_IsClassified()
    {
        var registry = new McpToolRegistry();
        var tools = registry.GetAvailableTools();
        tools.ShouldNotBeEmpty();
        tools.Where(t => !McpResultMasking.IsClassified(t.Name)).Select(t => t.Name).ShouldBeEmpty();
    }

    // ---- HasMaskedValues -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void HasMaskedValues_EmptyRows_False() =>
        McpResultMasking.HasMaskedValues(Decision(("ssn", ColumnAccessLevel.Mask)), null, []).ShouldBeFalse();

    [Fact]
    public void HasMaskedValues_MaskedColumnProjected_True() =>
        McpResultMasking.HasMaskedValues(Decision(("id", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Mask)), null, Rows("id", "ssn")).ShouldBeTrue();

    [Fact]
    public void HasMaskedValues_MaskedColumnNotProjected_False() =>
        McpResultMasking.HasMaskedValues(Decision(("id", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Mask)), null, Rows("id")).ShouldBeFalse();

    [Fact]
    public void HasMaskedValues_RowDependent_True() =>
        McpResultMasking.HasMaskedValues(Decision(("id", ColumnAccessLevel.Clear), ("salary", ColumnAccessLevel.RowDependent)), null, Rows("id", "salary")).ShouldBeTrue();

    [Fact]
    public void HasMaskedValues_AllClear_False() =>
        McpResultMasking.HasMaskedValues(Decision(("id", ColumnAccessLevel.Clear), ("city", ColumnAccessLevel.Clear)), null, Rows("id", "city")).ShouldBeFalse();

    [Fact]
    public void HasMaskedValues_TypedColumnMask_True()
    {
        var decision = Decision(("id", ColumnAccessLevel.Clear), ("city", ColumnAccessLevel.Clear)) with { ColumnMasks = new FixedMasks("city") };
        McpResultMasking.HasMaskedValues(decision, null, Rows("id", "city")).ShouldBeTrue();
    }

    [Fact]
    public void HasMaskedValues_RestrictedScopeWithoutMetadata_True()
    {
        var decision = Decision(("id", ColumnAccessLevel.Clear)) with
        {
            ScopeClearance = new RestrictedScopeClearance(new HashSet<string>(), new HashSet<string>())
        };
        McpResultMasking.HasMaskedValues(decision, null, Rows("id")).ShouldBeTrue();
    }

    [Fact]
    public void HasMaskedValues_UnresolvedColumn_True() =>
        McpResultMasking.HasMaskedValues(Decision(("id", ColumnAccessLevel.Clear)), null, Rows("id", "derived_expr")).ShouldBeTrue();

    private sealed class FixedMasks(params string[] columns) : IColumnMaskPolicy
    {
        public string Fingerprint => "fixed";
        public IReadOnlyCollection<string> MaskedColumns => columns;
        public bool HasMask(string column) => columns.Contains(column, StringComparer.OrdinalIgnoreCase);
    }

    // ---- GovernedSqlResult ---------------------------------------------------------------------------------------------------------

    private static GovernedSqlResult SqlResult(ImmutableArray<bool> masks, int rows, bool inDb = false) =>
        new("q", ["id", "ssn"], Enumerable.Range(0, rows).Select(_ => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = 1, ["ssn"] = "x" }).ToList(),
            rows, 1, InDbColumnMaskingExecuted: inDb, MaskedColumns: masks);

    [Fact]
    public void GovernedSqlResult_Masked_WithRows_True() =>
        SqlResult([false, true], 1).HasPolicyMaskedValues.ShouldBeTrue();

    [Fact]
    public void GovernedSqlResult_Masked_ZeroRows_False() =>
        SqlResult([false, true], 0).HasPolicyMaskedValues.ShouldBeFalse();

    [Fact]
    public void GovernedSqlResult_NoMask_False() =>
        SqlResult([false, false], 2, inDb: true).HasPolicyMaskedValues.ShouldBeFalse();

    [Fact]
    public void GovernedSqlResult_DefaultMasks_InDbMasking_True() =>
        SqlResult(default, 1, inDb: true).HasPolicyMaskedValues.ShouldBeTrue();

    [Fact]
    public void GovernedSqlResult_DefaultMasks_NoInDbMasking_False() =>
        SqlResult(default, 1, inDb: false).HasPolicyMaskedValues.ShouldBeFalse();

    [Fact]
    public void GovernedSqlResult_NewMembers_AreNotSerialized()
    {
        var masked = JsonSerializer.Serialize(SqlResult([false, true], 1));
        var clear = JsonSerializer.Serialize(SqlResult([false, false], 1));
        masked.ShouldBe(clear);
        masked.ToLowerInvariant().ShouldNotContain("mask");

        var procedure = JsonSerializer.Serialize(new GovernedProcedureResult(["id"], [], 0, false, 1, HasMaskedValues: true));
        procedure.ToLowerInvariant().ShouldNotContain("mask");
    }
}
