using System.Collections.Frozen;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;

namespace TrinoSqlEngine.Tests.Conformance;

/// <summary>The observed result of compiling one corpus input for one dialect.</summary>
internal sealed record ConformanceObservation(
    SqlStatement? Statement,
    string Outcome,
    string? Sql,
    IReadOnlyList<ExpectedParameter> Parameters,
    string? ExceptionType);

internal static class ConformanceRunner
{
    private static readonly FastSqlEngine Engine = new();

    /// <summary>All DML permissions, strict token guards, the shared policy fixture catalog and a bound tenant (<c>acme</c>), no row or mask policies.</summary>
    private static CompileRequest DmlRequest(TargetSqlDialect dialect)
    {
        var filters = new DictPolicyProvider();
        filters.NoPolicy.Add(PolicyFixtures.Orders);
        filters.NoPolicy.Add(PolicyFixtures.Entitlements);
        filters.NoPolicy.Add(PolicyFixtures.Lookup);
        return new CompileRequest
        {
            TargetDialect = dialect,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
            Policy = new GovernancePolicy
            {
                RowFilters = filters,
                Masks = new DictMaskProvider(),
                Catalog = DmlCompileTests.CatalogFor(dialect),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
                Dml = DmlGuardOptions.Strict
            }
        };
    }

    private static bool IsDml(string group) => group.StartsWith("dml", StringComparison.Ordinal);

    /// <summary>
    /// The statement the generator sees: the Trino input is parsed and built like <c>Compile</c> does (the compiler function map of the dialect, validation,
    /// simplification), without the security injection; <c>@name</c> inputs are in-memory ASTs. DML keeps the pre-injection tree.
    /// </summary>
    public static SqlStatement Prepare(string input, string group, TargetSqlDialect dialect)
    {
        if (input.StartsWith('@')) return ConformanceAstFixtures.ByName[input[1..]]().Statement;

        var (tree, _) = Engine.Parse(input.AsMemory(), IsDml(group) ? SqlTokenSecurityOptions.Strict : SqlTokenSecurityOptions.None);
        var ast = new SqlAstBuilder(new AstBuilderOptions
        {
            EnforceReadOnlyQueries = !IsDml(group),
            EnforceFunctionPolicy = true,
            AllowedFunctions = SqlFunctionAllowlists.CompilerNames(dialect),
            TranslateTrinoDateFunctions = group == "date-translated"
        }).BuildStatement(tree);
        new AstValidationVisitor().Validate(ast);
        return (SqlStatement)new AstSimplificationVisitor().Visit(ast);
    }

    /// <summary>Generates SQL for the dialect and records the outcome (DML goes through the governed Compile pipeline).</summary>
    public static ConformanceObservation Run(string input, string group, TargetSqlDialect dialect)
    {
        SqlStatement? statement = null;
        try
        {
            IReadOnlyDictionary<string, PolicyValue> policy = new Dictionary<string, PolicyValue>();
            IReadOnlyDictionary<string, object?> client = new Dictionary<string, object?>();
            if (input.StartsWith('@'))
            {
                var ast = ConformanceAstFixtures.ByName[input[1..]]();
                statement = ast.Statement;
                policy = ast.PolicyValues;
                client = ast.ClientValues;
            }
            else if (IsDml(group))
            {
                // DML cannot be emitted without its injected security predicates: it goes through the governed Compile pipeline.
                try { statement = Prepare(input, group, dialect); } catch (Exception) { /* the compile below reports it */ }
                var compiledDml = Engine.Compile(input.AsMemory(), DmlRequest(dialect), CancellationToken.None);
                return new ConformanceObservation(statement, "ok", compiledDml.Sql,
                    compiledDml.Parameters.Select(ConformanceStore.Describe).ToImmutableArray(), null);
            }
            else
            {
                statement = Prepare(input, group, dialect);
            }

            var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
            var compiled = generator.Generate(statement, new ParameterSource(policy.ToFrozenDictionary(), client), CancellationToken.None);
            return new ConformanceObservation(statement, "ok", compiled.Sql,
                compiled.Parameters.Select(ConformanceStore.Describe).ToImmutableArray(), null);
        }
        catch (Exception ex)
        {
            return new ConformanceObservation(statement, "rejected", null, Array.Empty<ExpectedParameter>(), ex.GetType().Name);
        }
    }
}
