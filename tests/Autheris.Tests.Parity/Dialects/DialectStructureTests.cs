using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Matrix;
using Shouldly;

namespace Autheris.Tests.Parity.Dialects;

/// <summary>No container needed: guards that every test class bound to a container host is excluded from the SQLite job by its trait (D-15).</summary>
[Trait("Category", "Parity")]
public sealed class DialectStructureTests : ParityTestBase
{
    private static IEnumerable<Type> ContainerClasses() =>
        typeof(ParityPostgresHostFixture).Assembly.GetTypes()
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ParityPostgresHostFixture) || p.ParameterType == typeof(ParitySqlServerHostFixture))));

    private static bool HasTrait(Type type, string name, string value) =>
        type.GetCustomAttributesData().Any(d => d.AttributeType == typeof(TraitAttribute)
            && d.ConstructorArguments.Count == 2 && (string?)d.ConstructorArguments[0].Value == name && (string?)d.ConstructorArguments[1].Value == value);

    [Fact]
    public void Dialect_EveryClassUsingAContainerHostCarriesTheContainerTrait()
    {
        var classes = ContainerClasses().ToList();
        classes.ShouldNotBeEmpty();
        foreach (var type in classes)
        {
            HasTrait(type, "Dialect", "Container").ShouldBeTrue(type.Name);
            HasTrait(type, "Category", "Parity").ShouldBeTrue(type.Name);
        }
    }

    [Fact]
    public void Dialect_ClassesWithoutAContainerHostDoNotCarryTheContainerTrait()
    {
        var container = ContainerClasses().ToHashSet();
        typeof(ParityPostgresHostFixture).Assembly.GetTypes()
            .Where(t => !container.Contains(t) && HasTrait(t, "Dialect", "Container"))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Dialect_SqlServerIsRegisteredPendingForEveryPairwiseCase()
    {
        var cases = Pairwise.PairwiseModel.Cases().Where(c => c.Dialect == Pairwise.PairwiseModel.SqlServer).ToList();
        cases.Count.ShouldBe(Pairwise.PairwiseModel.Cases().Count(c => c.Dialect == Pairwise.PairwiseModel.Sqlite));
        DialectPairwiseTests.ContainerCases().Count(r => ((string)r[1]).EndsWith("_sqlserver", StringComparison.Ordinal)).ShouldBe(cases.Count * 2);
    }

    [Fact]
    public void Dialect_TimeBucketCellsCoverSqlServerAndPostgres()
    {
        var cellIds = TimeBucketCells.All.Select(c => c.Id).ToList();
        cellIds.ShouldBe(["tb_week_sale_date", "tb_quarter_sale_date", "tb_isoweek_year_boundary_utc", "tb_isoweek_year_boundary_berlin", "tb_month_year_boundary_berlin", "tb_isoweek_zone_shift_new_york", "tb_hour_sold_at"]);
        cellIds.Distinct().Count().ShouldBe(cellIds.Count);
        TimeBucketCells.All.Single(c => c.Id == "tb_week_sale_date").Expected.Sum(r => (int)r[1]!).ShouldBe(Seed.ParitySeed.Sales.Count);
        TimeBucketCells.All.Single(c => c.Id == "tb_quarter_sale_date").Expected.Sum(r => (int)r[1]!).ShouldBe(Seed.ParitySeed.Sales.Count);
        foreach (var type in new[] { typeof(DialectTimeBucketSqlServerTests), typeof(DialectTimeBucketPostgresTests) })
        {
            HasTrait(type, "Dialect", "Container").ShouldBeTrue(type.Name);
            HasTrait(type, "Category", "Parity").ShouldBeTrue(type.Name);
        }

        ContainerClasses().ShouldContain(typeof(DialectTimeBucketSqlServerTests));
        ContainerClasses().ShouldContain(typeof(DialectTimeBucketPostgresTests));
    }

    [Fact]
    public void Dialect_SqliteOnlyPendingReasonPassesThePendingRule()
    {
        PendingReasons.IsValid(DialectMatrixTests.SqliteOnlyReason, PendingReasons.FollowUpIds(PendingReasons.ReadPlan())).ShouldBeTrue();
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("duckdb")]
    public void Dialect_MatrixGapsAreSqliteOnly(string dialect)
    {
        var gaps = new Dictionary<string, string> { ["agg_sum_text|WebSql"] = "PARITY-GAP-021" };

        MatrixKnownGaps.For(gaps, "agg_sum_text", MatrixChannel.WebSql, "sqlite").ShouldBe("PARITY-GAP-021");
        MatrixKnownGaps.For(gaps, "agg_sum_text", MatrixChannel.WebSql, dialect).ShouldBeNull();
        MatrixKnownGaps.For("agg_sum_text", MatrixChannel.WebSql, dialect).ShouldBeNull();
    }
}
