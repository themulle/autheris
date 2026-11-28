namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using Xunit;

/// <summary>
/// On a table with a row policy the row-policy guards refuse an expression over an unresolvable column. That refusal must answer like every
/// other unknown column (named when the caller sees every table, else the denial of a hidden column) so the code never separates an unknown
/// column from a hidden one. A resolvable column keeps the guard's own answer. The tables carry a tenant column, so every statement runs under a row policy.
/// </summary>
[Trait("Category", "Security")]
public sealed class UnresolvedColumnOnRowPolicyTableTests
{
    private static readonly IReadOnlyDictionary<string, Dictionary<string, ColumnAccessLevel>> HiddenSecret =
        new Dictionary<string, Dictionary<string, ColumnAccessLevel>>
        {
            ["orders"] = new() { ["secret"] = ColumnAccessLevel.Deny },
            ["customers"] = new() { ["ssn"] = ColumnAccessLevel.Deny }
        };

    private static async Task<Exception> RefusalAsync(GovernedSqlRewriter rewriter, string sql) =>
        await Should.ThrowAsync<Exception>(() => UnknownColumnNamingTests.RewriteAsync(rewriter, sql));

    private static async Task AssertLooksLikeDeniedColumnAsync(string unknownSql, string hiddenSql)
    {
        var rewriter = UnknownColumnNamingTests.Rewriter(HiddenSecret);

        var unknown = await RefusalAsync(rewriter, unknownSql);
        var hidden = await RefusalAsync(rewriter, hiddenSql);

        var body = UnknownColumnNamingTests.Body(unknown);
        body.ShouldContain("SQL_POLICY_DENIED");
        body.ShouldContain("\"status\":403");
        UnknownColumnNamingTests.Body(hidden).ShouldBe(body);
        foreach (var key in new[] { "\"line\"", "\"column\"", "\"hint\"", "\"columnName\"", "nope" })
        {
            body.ShouldNotContain(key);
        }

        // the audit reason is the same fixed class of text: it names no column
        unknown.Message.ShouldNotContain("nope");
    }

    [Fact]
    public Task UnknownColumn_InFilter_OnRowPolicyTable_AnswersLikeDeniedColumn() =>
        AssertLooksLikeDeniedColumnAsync(
            "SELECT id FROM orders WHERE nope = 'x'",
            "SELECT id FROM orders WHERE secret = 'x'");

    [Fact]
    public Task UnknownColumn_InOrderBy_OnRowPolicyTable_AnswersLikeDeniedColumn() =>
        AssertLooksLikeDeniedColumnAsync(
            "SELECT id FROM orders ORDER BY CASE WHEN nope = 'x' THEN 1 ELSE 0 END",
            "SELECT id FROM orders ORDER BY CASE WHEN secret = 'x' THEN 1 ELSE 0 END");

    [Fact]
    public Task UnknownColumn_InJoinCondition_OnRowPolicyTable_AnswersLikeDeniedColumn() =>
        AssertLooksLikeDeniedColumnAsync(
            "SELECT o.id FROM orders o JOIN customers c ON c.nope = 'x'",
            "SELECT o.id FROM orders o JOIN customers c ON c.ssn = 'x'");

    [Fact]
    public async Task UnknownColumn_OnFullyVisibleRowPolicyTable_IsNamedUnknownColumn()
    {
        var error = await RefusalAsync(UnknownColumnNamingTests.Rewriter(), "SELECT id FROM orders WHERE nope = 'x'");

        var named = error.ShouldBeOfType<SqlUnknownColumnException>();
        named.ColumnName.ShouldBe("nope");
        named.Line.ShouldBe(1);
        named.Column.ShouldBe(29);
        SqlCompilerErrorMapper.TryMap(error, out var mapped).ShouldBeTrue();
        mapped.Status.ShouldBe(400);
        mapped.Code.ShouldBe("UNKNOWN_COLUMN");
        error.Message.ShouldNotContain("nope");
    }

    [Fact]
    public async Task ResolvedColumn_RowPolicyGuardBehaviourUnchanged()
    {
        // id is an int catalog column: comparing it with a string literal is a mixed-family expression the guard still refuses
        foreach (var rewriter in new[] { UnknownColumnNamingTests.Rewriter(), UnknownColumnNamingTests.Rewriter(HiddenSecret) })
        {
            var error = await RefusalAsync(rewriter, "SELECT id FROM orders WHERE id = 'x'");

            error.ShouldBeOfType<RowPolicyExpressionRejectedException>();
            SqlCompilerErrorMapper.TryMap(error, out var mapped).ShouldBeTrue();
            mapped.Code.ShouldBe("ROW_POLICY_EXPRESSION_NOT_ALLOWED");
        }
    }

    [Fact]
    public async Task UnresolvedColumnException_NeverCarriesPolicyText()
    {
        var error = await RefusalAsync(UnknownColumnNamingTests.Rewriter(HiddenSecret), "SELECT id FROM orders WHERE nope = 'x'");

        error.ShouldBeOfType<WebSqlTableDeniedException>();
        error.InnerException.ShouldBeNull();
        foreach (var text in new[] { "nope", "tenant", "secret", "ssn" })
        {
            error.Message.ShouldNotContain(text, Case.Insensitive);
            UnknownColumnNamingTests.Body(error).ShouldNotContain(text, Case.Insensitive);
        }

        // a name that is not safe to echo is a denial even where the caller sees every table
        var unsafeName = await RefusalAsync(UnknownColumnNamingTests.Rewriter(), "SELECT id FROM orders WHERE \"bad name\" = 'x'");
        unsafeName.ShouldBeOfType<WebSqlTableDeniedException>();
    }

    private const string HiddenFilter = "SELECT id FROM orders WHERE secret = 'x'";

    [Fact]
    public Task UnknownSpelledAsSelectAlias_AnswersLikeDeniedColumn() =>
        AssertLooksLikeDeniedColumnAsync("SELECT 1 AS nope FROM orders WHERE nope = 'x'", HiddenFilter);

    [Fact]
    public async Task UnknownSpelledAsTableAlias_OnFullyVisibleTable_IsNeverNamed()
    {
        // A name the statement defines itself is never echoed as an unknown column. (UNNEST alias lists are not supported by the compiler,
        // so they never reach the guard. On a table with masked columns a table alias spelled like a column is answered earlier by the
        // whole-row guard, which is outside this refusal.)
        var error = await RefusalAsync(UnknownColumnNamingTests.Rewriter(), "SELECT id FROM orders nope WHERE nope = 'x'");

        error.ShouldBeOfType<WebSqlTableDeniedException>();
        UnknownColumnNamingTests.Body(error).ShouldContain("SQL_POLICY_DENIED");
        UnknownColumnNamingTests.Body(error).ShouldNotContain("nope");
    }

    [Fact]
    public Task UnknownSpelledAsCteName_AnswersLikeDeniedColumn() =>
        AssertLooksLikeDeniedColumnAsync("WITH nope AS (SELECT id FROM orders) SELECT id FROM orders WHERE nope = 'x'", HiddenFilter);

    [Fact]
    public Task UnknownStructPath_AnswersLikeDeniedColumn() =>
        AssertLooksLikeDeniedColumnAsync("SELECT id FROM orders WHERE orders.nope.x = 'x'", HiddenFilter);

    [Fact]
    public async Task ResolverException_AnswersDenied()
    {
        // a statement the resolver cannot read is never "all catalog columns": the answer is then the denial, not the guard's code
        var probe = new UnknownColumnProbe("SELEC ??? FROM", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });

        UnknownColumnResolver.AllReferencesAreUnshadowedCatalogColumns(probe).ShouldBeFalse();
        UnknownColumnResolver.Classify(probe, out _, out _, out _).ShouldBe(UnknownColumnResolver.Finding.None);
        await Task.CompletedTask;
    }
}
