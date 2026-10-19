namespace Autheris.Tests.Unit.Sql.TimeZones;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>ST-130-4 / BC-130-5d / BC-130-4: a zone slot is consumable only through the binder; every non-compiler call of a generator is known.</summary>
[Trait("Category", "Security")]
public sealed class ConvertTimeZoneConsumerTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task ST_130_4_TheGeneratedQueryPathRefusesAZoneSlotBeforeAnyProviderCall()
    {
        using var engine = new DuckDbOlapEngine(new DuckDbOlapOptions());
        var zone = new BoundParameter("$1", "1", 0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "zone", ZoneArgument: true);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            engine.ExecuteGeneratedAsync(new GeneratedOlapQuery("SELECT TIMEZONE($1, TIMESTAMP '2026-01-01 00:00:00')", [zone]), []));

        ex.Message.ShouldBe("The compiled SQL contains a parameter that only the governed binder may bind.");
    }

    [Fact]
    public void ST_130_4_TheOtherParameterConsumersNeverHoldABoundParameter()
    {
        // The virtual filter key-set provider and the tree service build their own (name, value) pairs from structured input; neither is handed a
        // CompiledSql or a BoundParameter, so a zone slot cannot reach them. A change that gives them one fails here and needs review.
        foreach (string file in new[]
                 {
                     "src/Autheris.Application/VirtualFilters/Federation/SqlVirtualFilterKeySetProvider.cs",
                     "src/Autheris.Application/VirtualFilters/Federation/StructuredFilterKeyQueryBuilder.cs",
                     "src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs"
                 })
        {
            string source = File.ReadAllText(Path.Combine(Root(), file));
            source.ShouldNotContain("BoundParameter", Case.Sensitive, file);
            source.ShouldNotContain("CompiledSql ", Case.Sensitive, file);
        }
    }

    [Fact]
    public void BC_130_4_EveryGeneratorCallOutsideTheCompilerIsKnown()
    {
        // Every site that asks a dialect generator for SQL outside the governed compiler. A new site must either call the time zone validator or be
        // unable to carry a user expression, and is added here after review. An unvalidated emission of convert_timezone throws anyway (BC-130-12).
        var sites = new List<string>();
        foreach (string path in Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Root(), path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (relative.StartsWith("src/TrinoSqlEngine/Ast/Generators/", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(path);
            int count = System.Text.RegularExpressions.Regex.Count(text, @"(?<![A-Za-z])generator\.(Generate|GenerateSql)\(|capabilityAware\.Generate\(|GetGenerator\([^)]*\)\.GenerateSql\(");
            if (count > 0) sites.Add($"{relative}:{count}");
        }

        sites.Order(StringComparer.Ordinal).ShouldBe(
        [
            "src/Autheris.Application/Sql/Federation/Subtrees/SubtreeCompiler.cs:1",       // co-located multi-database joins (Issue #132): SQL Server dialect generator only
            "src/Autheris.Application/Sql/Masking/MaskSpecRenderer.cs:1",                 // typed mask expressions of the policy: no user expression
            "src/Autheris.Application/Sql/Policy/PolicyPredicateRenderer.cs:1",           // admin predicates: the policy allowlist excludes the function (BC-130-2)
            "src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs:1",             // virtual filters: closed function list
            "src/TrinoSqlEngine/Governance/PolicyKeyQueryCompiler.cs:2",                  // key queries from typed policy only
            "src/TrinoSqlEngine/GovernedSqlCompiler.cs:2",                                // the compiler: validator before emit
            "src/TrinoSqlEngine/TimeZones/TimeZoneConversion.cs:1"                        // the federated planner, through ValidateFederated's proof
        ], ignoreOrder: false);
    }

    [Fact]
    public void BC_130_4_ThePlannerValidatesAfterTheStatementIsBuiltAndBeforeTheRewrite()
    {
        string source = File.ReadAllText(Path.Combine(Root(), "src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs"));
        int built = source.IndexOf("statement = new SqlAstBuilder().BuildStatement(tree);", StringComparison.Ordinal);
        int closed = source.IndexOf('}', built);
        int validated = source.IndexOf("TimeZoneConversion.ValidateFederated(", StringComparison.Ordinal);
        int rewritten = source.IndexOf("new FederationAstRewriter(", StringComparison.Ordinal);
        built.ShouldBeGreaterThan(0);
        validated.ShouldBeGreaterThan(closed);   // after the whole `if (statement == null) { ... }` block: it runs for a supplied statement too
        validated.ShouldBeLessThan(rewritten);
    }
}
