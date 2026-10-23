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
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ParityPostgresHostFixture))));

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
    public void Dialect_SqliteOnlyPendingReasonPassesThePendingRule()
    {
        PendingReasons.IsValid(DialectMatrixTests.SqliteOnlyReason, PendingReasons.FollowUpIds(PendingReasons.ReadPlan())).ShouldBeTrue();
    }
}
