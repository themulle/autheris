namespace Autheris.Tests.SqlCompilerGate.Parity;

using System.Text.RegularExpressions;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using Xunit.Abstractions;

/// <summary>
/// SEC-ADG-02 item 2 for the renderer, without a database: the IR text is fully parenthesized, every value is a marker in the reserved parameter
/// namespace, and every marker has a value. Runs on the four dialects (Oracle has no container in this gate; the in-memory consumers and the
/// connector validation run on its text).
/// </summary>
public sealed partial class RendererInvariantTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Dialects() =>
    [
        [GateDialect.Sqlite, "main"],
        [GateDialect.PostgreSql, "public"],
        [GateDialect.SqlServer, "dbo"],
        [GateDialect.Oracle, "GATE_APP"]
    ];

    [GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"\A@__(gql_rls|gql_vf|gql_mask|pl)_[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReservedNameRegex();

    /// <summary>True when an AND or OR sits at parenthesis depth 0 outside quoted text: a consumer that composes the text with another predicate would change its meaning.</summary>
    public static bool HasUnenclosedLogicalOperator(string sql)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (quote != '\0')
            {
                if (c == (quote == '[' ? ']' : quote)) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\'' or '"' or '[' or '`':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                    if (depth == 0 && i > 0 && char.IsWhiteSpace(sql[i - 1]) &&
                        (StartsWithWord(sql, i, "AND") || StartsWithWord(sql, i, "OR")))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool StartsWithWord(string sql, int index, string word) =>
        string.Compare(sql, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
        && index + word.Length < sql.Length && char.IsWhiteSpace(sql[index + word.Length]);

    private static IEnumerable<(ParityCase Case, FilterText Ir)> Produced(ParityContext context)
    {
        foreach (var c in ParityCases.All)
        {
            var pair = ParityRunner.TryProduce(c, context, out _);
            if (pair is not null && !string.IsNullOrEmpty(pair.Ir.Sql)) yield return (c, pair.Ir);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Renderer_FullyParenthesized_NoUnenclosedLogicalOperator(GateDialect dialect, string schema)
    {
        var offenders = Produced(new ParityContext(dialect, schema)).Where(p => HasUnenclosedLogicalOperator(p.Ir.Sql)).Select(p => $"{p.Case.Id}: {p.Ir.Sql}").ToList();

        offenders.ShouldBeEmpty("IR text with an unenclosed AND/OR on " + dialect + ":\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Renderer_ParenthesizationCheck_NegativeControl_FlagsUnenclosedOperators()
    {
        HasUnenclosedLogicalOperator("\"a\" = 1 OR \"b\" = 2").ShouldBeTrue();
        HasUnenclosedLogicalOperator("\"a\" = 1 and \"b\" = 2").ShouldBeTrue();
        HasUnenclosedLogicalOperator("(\"a\" = 1 OR \"b\" = 2) AND \"c\" = 3").ShouldBeTrue();
        // enclosed, quoted and identifier occurrences are not flagged
        HasUnenclosedLogicalOperator("(\"a\" = 1 OR \"b\" = 2)").ShouldBeFalse();
        HasUnenclosedLogicalOperator("\"a\" = 'x OR y'").ShouldBeFalse();
        HasUnenclosedLogicalOperator("\"order\" = 1").ShouldBeFalse();
        HasUnenclosedLogicalOperator("NOT (\"a\" = 1 AND \"b\" = 2)").ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Renderer_ValuesAreMarkersInTheReservedNamespace_AndEveryMarkerHasAValue(GateDialect dialect, string schema)
    {
        var context = new ParityContext(dialect, schema);
        int checkedCases = 0;
        foreach (var (parityCase, ir) in Produced(context))
        {
            checkedCases++;
            ir.Parameters.Keys.ShouldAllBe(k => ReservedNameRegex().IsMatch(k), $"{parityCase.Id}: a value outside the reserved namespace");

            var markers = MarkerRegex().Matches(StripQuoted(ir.Sql)).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            markers.SetEquals(ir.Parameters.Keys).ShouldBeTrue($"{parityCase.Id}: markers {string.Join(",", markers)} against values {string.Join(",", ir.Parameters.Keys)}");
        }

        output.WriteLine($"{dialect}: {checkedCases} IR texts checked");
        checkedCases.ShouldBeGreaterThan(40);
    }

    [Fact]
    public void ConsumerParameterNames_NeverEnterTheReservedNamespace()
    {
        // the names the consumers bind themselves (tenant, key, pagination, rename) stay outside "__gql_" and "__pl_", so no policy value can shadow them
        foreach (var own in new[] { "p_tenant_0", "p0", "rs_tenant", "rs_0_0", "gq0", "rf1_x", "gql_limit", "gql_offset" })
        {
            ReservedNameRegex().IsMatch("@" + own).ShouldBeFalse(own);
        }

        ReservedNameRegex().IsMatch("@__gql_rls_0").ShouldBeTrue();
        ReservedNameRegex().IsMatch("@p_tenant_0").ShouldBeFalse();
    }

    private static string StripQuoted(string sql) => Regex.Replace(sql, "'(?:[^']|'')*'|\"(?:[^\"]|\"\")*\"|\\[[^\\]]*\\]", "''", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>
/// The Oracle dialect has no container in the parity gate. The consumers that need no database (the in-memory row filter, the streaming
/// evaluator, the connector validation) run on its text; the classes and the oracle are the ones of the database classes.
/// </summary>
public sealed class OracleDialectParityTests(ITestOutputHelper output)
{
    private static ParityEnv Env() => new(new Databases.SqliteGateDatabase(), new ParityContext(GateDialect.Oracle, "GATE_APP"));

    [Fact]
    public async Task InMemoryAndPushdownConsumers_HaveNoLooserResult_OnTheOracleText()
    {
        var env = Env();
        var results = await ParityRunner.RunAllAsync(env, ParityConsumers.RowConsumers(databaseBacked: false));
        results = [.. results, .. await ParityRunner.RunAllAsync(env, [ParityConsumers.Pushdown])];
        foreach (var line in ParityRunner.Table(results)) output.WriteLine(line);

        results.Where(r => r.IsLooser).Select(r => $"{r.Consumer} {r.Case.Id} {r.Classification.Class.ToWireName()}").ShouldBeEmpty();
    }
}
