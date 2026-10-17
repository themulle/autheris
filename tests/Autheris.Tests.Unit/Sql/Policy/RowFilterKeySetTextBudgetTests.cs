using System.Collections.Immutable;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>Issue #107 D-WPC-3, SEC-RF107-18: the pure text budget helper matches the compiler formula and over-estimates the emitted lists.</summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetTextBudgetTests
{
    private static readonly TableIdentity Air1 = new("fms", "air1");
    private static readonly TenantBinding Tenant = new("__autheris_tenant", "acme", SqlParameterType.String);
    private static readonly FastSqlEngine Compiler = new();

    private static InMemoryTableCatalog Catalog(string whole, string text) => new(
    [
        new TableCatalogEntry(Air1, ImmutableArray.Create(
            new CatalogColumn("id", whole), new CatalogColumn("client_id", whole), new CatalogColumn("tenant_id", text)), "tenant_id", 1),
        new TableCatalogEntry(new TableIdentity("conf", "client"), ImmutableArray.Create(
            new CatalogColumn("id", whole), new CatalogColumn("tenant_id", text)), "tenant_id", 1)
    ], "dbo");

    private static ConsentRowFilter Correlated() => new()
    {
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("ds", "conf", "client"),
        DependentTableAlias = "c",
        ForeignKeyColumn = "client_id",
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = "{\"c.id\": 1}"
    };

    private sealed class Policies(Dictionary<TableIdentity, PolicyPredicate> predicates) : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => predicates.ContainsKey(table);

        public PolicyPredicate GetPredicate(TableIdentity table) => predicates[table];

        public bool HasMask(TableIdentity table, string column) => false;

        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private static PolicyPredicate Substituted(TargetSqlDialect dialect, int ordinal, int count)
    {
        var predicate = new TypedRowFilterBuilder().BuildCombined([new Consent { Effect = ConsentEffect.Allow, RowFilters = [Correlated()] }], [], Air1)!;
        var site = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;
        var keys = Enumerable.Range(1, count).Select(i => new PolicyValue((long)i, SqlParameterType.Int64)).ToList();
        var replacement = new KeySetReplacement("client_id", keys, count, ordinal);
        return CorrelatedKeySetSubstitution.Substitute(predicate,
            [(site, replacement)], dialect);
    }

    private static CompiledSql Compile(string sql, TargetSqlDialect dialect, PolicyPredicate predicate, int? factor)
    {
        var (whole, text) = dialect switch
        {
            TargetSqlDialect.SqlServer => ("int", "nvarchar(64)"),
            TargetSqlDialect.Oracle => ("NUMBER(10)", "VARCHAR2(64)"),
            TargetSqlDialect.Sqlite => ("integer", "text"),
            _ => ("integer", "varchar(64)")
        };
        var provider = new Policies(new Dictionary<TableIdentity, PolicyPredicate> { [Air1] = predicate });
        var request = new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy { RowFilters = provider, Masks = provider, Catalog = Catalog(whole, text), Tenant = Tenant }
        };
        return Compiler.Compile(sql.AsMemory(), factor is null ? request : request with { MaxExpansionFactor = factor.Value }, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 64, 4096)]
    [InlineData(10, 64, 4096)]      // the floor of 64 characters applies
    [InlineData(64, 64, 4096)]
    [InlineData(65, 64, 4160)]
    [InlineData(1000, 64, 64000)]
    [InlineData(100, 256, 25600)]
    [InlineData(100, 1, 100)]
    public void Limit_Values(int userSqlLength, int factor, long expected)
        => RowFilterKeySetTextBudget.Limit(userSqlLength, factor).ShouldBe(expected);

    [Fact]
    public void Limit_MatchesCompilerFormula_D_WPC_3()
    {
        const string Select = "SELECT id FROM fms.air1";
        var predicate = Substituted(TargetSqlDialect.PostgreSql, 0, 2000);

        var ex = Should.Throw<SqlLimitExceededException>(() => Compile(Select, TargetSqlDialect.PostgreSql, predicate, factor: null));

        ex.Kind.ShouldBe(SqlLimitKind.PolicyExpansionFactor);
        ex.Maximum.ShouldBe(RowFilterKeySetTextBudget.Limit(Select.Length, 64));
        // and with the floor: a statement shorter than 64 characters
        Select.Length.ShouldBeLessThan(CompileLimits.MinimumInputForExpansion);
        ex.Maximum.ShouldBe(64L * CompileLimits.MinimumInputForExpansion);
    }

    [Fact]
    public void KeyListBudget_IsHalfTheLimit()
        => RowFilterKeySetTextBudget.KeyListBudget(100, 64).ShouldBe(3200);

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void EstimateIsUpperBound_PerDialect_SEC_RF107_18(TargetSqlDialect dialect)
    {
        // A long user statement, so that every list fits the compiler budget and the real emitted length can be measured.
        string longSelect = "SELECT " + string.Join(", ", Enumerable.Repeat("id", 1500)) + " FROM fms.air1";
        int[] counts = [1, 64, 1000, 1001, 2000];
        foreach (int ordinal in new[] { 0, 10 })
        {
            int emptyLength = Compile(longSelect, dialect, Substituted(dialect, ordinal, 0), 256).Sql.Length;
            foreach (int count in counts)
            {
                int emitted = Compile(longSelect, dialect, Substituted(dialect, ordinal, count), 256).Sql.Length;
                long leaf = emitted - emptyLength + 5;   // the leaf is "1 = 0" (5 characters) in the baseline
                long estimate = RowFilterKeySetTextBudget.EstimateListChars(dialect, ordinal, count, "client_id");

                estimate.ShouldBeGreaterThanOrEqualTo(leaf, $"{dialect}, ordinal {ordinal}, {count} items: estimate {estimate} < emitted {leaf}");
                estimate.ShouldBeLessThan(leaf * 4, "the bound must stay meaningful");
            }
        }
    }

    [Fact]
    public void Estimate_OracleCountsOneChunkPer1000Items()
    {
        long one = RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.Oracle, 0, 1000, "fk");
        long two = RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.Oracle, 0, 1001, "fk");

        RowFilterKeySetTextBudget.ChunkSize(TargetSqlDialect.Oracle).ShouldBe(1000);
        (two - one).ShouldBeGreaterThan(2L * (RowFilterAliases.Target.Length + 2 + 6));   // a second chunk adds its own column reference and syntax
    }

    [Fact]
    public void Estimate_EmptySet_IsTheConstantFalsePredicate()
        => RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.PostgreSql, 3, 0, "fk").ShouldBe(5);

    [Fact]
    public void Estimate_IsPure_AndGrowsWithCount()
    {
        var a = RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.PostgreSql, 2, 64, "fk");

        RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.PostgreSql, 2, 64, "fk").ShouldBe(a);
        RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.PostgreSql, 2, 128, "fk").ShouldBeGreaterThan(a);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(9999)]
    [InlineData(10000)]
    [InlineData(100000)]
    public void IndexDigitSum_ClosedForm_EqualsTheNaiveSum(int count)
    {
        long naive = 0;
        for (int i = 0; i < count; i++)
        {
            naive += i.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        }

        RowFilterKeySetTextBudget.IndexDigitSum(count).ShouldBe(naive);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(9, 100)]
    [InlineData(10, 1000)]
    [InlineData(100, 1001)]
    public void EstimateListChars_EqualsTheNaivePerItemFormula(int ordinal, int count)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        long perItem = 0;
        for (int i = 0; i < count; i++)
        {
            perItem += 1 + "__rls_ks".Length + ordinal.ToString(culture).Length + 1 + i.ToString(culture).Length + 2;
        }

        const string column = "client_id";
        long chunks = (count + 999) / 1000;
        long expected = perItem + chunks * (2L * (RowFilterAliases.Target.Length + column.Length + 6) + 16);

        RowFilterKeySetTextBudget.EstimateListChars(TargetSqlDialect.Oracle, ordinal, count, column).ShouldBe(expected);
    }
}
