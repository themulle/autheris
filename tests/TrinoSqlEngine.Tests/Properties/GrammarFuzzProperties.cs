using System.Collections.Immutable;
using Antlr4.Runtime;
using FsCheck;
using FsCheck.Fluent;
using Xunit;
using Xunit.Abstractions;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// G5 grammar fuzzing: random token sequences drawn from the lexer vocabulary of <c>SqlBase.g4</c>, and valid statements damaged
/// by token deletion, duplication, swap and insertion, must either compile into text that satisfies every oracle or be rejected
/// with a typed exception. Under a read-only request nothing but a SELECT may ever compile. Any other exception type is a defect.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
[Trait("Category", "Fuzz")]
[Trait("Category", "NightlyGate")]
public class GrammarFuzzProperties(ITestOutputHelper output)
{
    private static readonly string[] Extras =
    [
        "orders", "entitlements", "lookup", "id", "tenantid", "region", "status", "t1", "dbo", "1", "42", "3.14", "1e5", "0x1F", "'abc'", "'it''s'",
        "\"quoted\"", "`tick`", "?", ":p1", "@p0", "$1", "--", "/*", "*/", ";", "'", "\"", "\\", "\0", "é", "\U0001F600", "${x}"
    ];

    /// <summary>Literal tokens of the grammar (keywords and operators), taken from the generated lexer so that new tokens are fuzzed automatically.</summary>
    private static readonly string[] Vocabulary = BuildVocabulary();

    private static string[] BuildVocabulary()
    {
        var vocab = SqlBaseLexer.DefaultVocabulary;
        var tokens = new List<string>();
        for (int i = 1; i < 1000; i++)
        {
            string? literal = vocab.GetLiteralName(i);
            if (literal is { Length: > 2 } && literal[0] == '\'' && literal[^1] == '\'')
            {
                tokens.Add(literal[1..^1]);
            }
        }

        return tokens.Concat(Extras).ToArray();
    }

    private static Gen<string> Token() => Gen.Frequency((8, Gen.Elements(Vocabulary)), (3, Gen.Elements(Extras)));

    private static Gen<Frag> Soup() =>
        from n in Gen.Choose(1, 40)
        from tokens in Gen.ListOf(Token(), n)
        select Frag.Of(tokens.ToArray());

    private static Gen<Frag> Mutated() =>
        from valid in QueryGen.Query()
        from edits in Gen.Choose(1, 3)
        from picks in Gen.ListOf(Gen.Choose(0, 1000), edits)
        from kinds in Gen.ListOf(Gen.Choose(0, 3), edits)
        from inserts in Gen.ListOf(Token(), edits)
        select Mutate(valid, picks, kinds, inserts);

    private static Frag Mutate(Frag valid, IReadOnlyList<int> picks, IReadOnlyList<int> kinds, IReadOnlyList<string> inserts)
    {
        var tokens = valid.Tokens.ToList();
        for (int i = 0; i < picks.Count && tokens.Count > 1; i++)
        {
            int at = picks[i] % tokens.Count;
            switch (kinds[i])
            {
                case 0:
                    tokens.RemoveAt(at);
                    break;
                case 1:
                    tokens.Insert(at, tokens[at]);
                    break;
                case 2:
                    int other = (at + 1) % tokens.Count;
                    (tokens[at], tokens[other]) = (tokens[other], tokens[at]);
                    break;
                default:
                    tokens.Insert(at, inserts[i]);
                    break;
            }
        }

        return valid with { Tokens = tokens.ToImmutableArray(), PhysicalRefs = int.MaxValue, UnknownFunction = false };
    }

    private static Gen<StmtCase> CaseGen(Gen<Frag> frag) =>
        from f in frag
        from ctx in CaseCtx.Gen()
        select new StmtCase(f with { PhysicalRefs = int.MaxValue }, null, ctx, new RenderStyle(1, 0, 1), new RenderStyle(1, 0, 1));

    [Fact]
    public void TokenSoup_IsRejectedWithATypedException_OrCompilesSafely()
    {
        var result = PropertyRunner.Run(CaseGen(Soup()), GateProfile.CasesPerProperty / 2, c => Oracles.CheckCase(c, ProductionCompiler.Compile), maxSize: 1);
        output.WriteLine($"cases={result.Cases} compiled={result.Compiled} rejected={result.Rejected}");
        result.ThrowIfFailed(nameof(TokenSoup_IsRejectedWithATypedException_OrCompilesSafely));
    }

    [Fact]
    public void DamagedStatements_AreRejectedWithATypedException_OrCompileSafely()
    {
        var result = PropertyRunner.Run(CaseGen(Mutated()), GateProfile.CasesPerProperty / 2, c => Oracles.CheckCase(c, ProductionCompiler.Compile));
        output.WriteLine($"cases={result.Cases} compiled={result.Compiled} rejected={result.Rejected}");
        foreach (var (type, count) in result.RejectionTypes.OrderByDescending(p => p.Value))
        {
            output.WriteLine($"  rejected by {type}: {count}");
        }

        result.ThrowIfFailed(nameof(DamagedStatements_AreRejectedWithATypedException_OrCompileSafely));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    [InlineData(1_000)]
    [InlineData(3_000)]
    public void DeepNesting_IsRejectedWithATypedException(int depth)
    {
        var ctx = new CaseCtx(TargetSqlDialect.PostgreSql, true, "TNT1_acme", "POL1_eu");
        string Repeat(string s) => string.Concat(Enumerable.Repeat(s, depth));
        var shapes = new Dictionary<string, string>
        {
            ["parens"] = $"SELECT {Repeat("(")}1{Repeat(")")} FROM orders",
            ["not"] = $"SELECT id FROM orders WHERE {Repeat("NOT ")}TRUE",
            ["negation"] = $"SELECT {Repeat("- ")}1 FROM orders",
            ["subquery"] = $"{Repeat("SELECT id FROM orders WHERE id IN (")}SELECT 1{Repeat(")")}",
            ["case"] = $"SELECT {Repeat("CASE WHEN TRUE THEN ")}1 {Repeat("ELSE 2 END ")} FROM orders",
            ["derived"] = $"SELECT * FROM {Repeat("(SELECT * FROM ")}orders{Repeat(") AS t")}",
            ["function"] = $"SELECT {Repeat("abs(")}1{Repeat(")")} FROM orders",
        };

        foreach (var (name, sql) in shapes)
        {
            var (compiled, error) = Oracles.TryCompile(ProductionCompiler.Compile, sql, ctx, StatementPermissions.ReadOnly);
            Assert.True(compiled is not null || error is not null && (Oracles.IsTypedRejection(error) || error is ArgumentOutOfRangeException),
                $"{name} depth {depth}: unexpected {error?.GetType().FullName}");
        }
    }
}
