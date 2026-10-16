using Autheris.Application.Sql.Tree.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Tree;

/// <summary>
/// BUG #75: the shared predicate that decides whether the tree service can run a relation. The schema and
/// <c>GovernedTreeQueryService</c> both call it, so these cases are also the contract for both.
/// </summary>
public sealed class RelationExecutabilityTests
{
    /// <summary>Runtime outcome of the tree service for a relation between the two tables of a case.</summary>
    public enum TreeOutcome
    {
        Runs,
        AcrossSources,
        NotSql,
        DialectMismatch,
        NoConnection
    }

    /// <summary>
    /// One data table for the predicate, the schema and the tree service (parent root is always a Sqlite table):
    /// case, parent source, child source, child source type, child data source type, executable, tree service outcome.
    /// </summary>
    public static TheoryData<string, string, string, string, DataSourceType, bool, TreeOutcome> Cases => new()
    {
        { "same source", "blog", "blog", "Sqlite", DataSourceType.Sql, true, TreeOutcome.Runs },
        { "case difference", "blog", "BLOG", "Sqlite", DataSourceType.Sql, true, TreeOutcome.Runs },
        { "different source", "blog", "crm", "Sqlite", DataSourceType.Sql, false, TreeOutcome.AcrossSources },
        { "same name different dialect", "blog", "blog", "SqlServer", DataSourceType.Sql, false, TreeOutcome.DialectMismatch },
        { "empty on both sides", "", "", "Sqlite", DataSourceType.Sql, true, TreeOutcome.NoConnection },
        { "empty versus non-empty", "", "blog", "Sqlite", DataSourceType.Sql, false, TreeOutcome.AcrossSources },
        { "non-empty versus empty", "blog", "", "Sqlite", DataSourceType.Sql, false, TreeOutcome.AcrossSources },
        { "different data source type", "blog", "blog", "Sqlite", DataSourceType.HttpDeclarative, false, TreeOutcome.NotSql }
    };

    internal static TableMetadata Meta(string sourceName, string sourceType, DataSourceType dataSourceType = DataSourceType.Sql, string domain = "d") => new()
    {
        Identifier = new TableIdentifier(domain, "main", "t"),
        Table = new Table { SourceName = sourceName, SourceType = sourceType, DataSourceType = dataSourceType }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsSameExecutionSource_MatchesTheTreeServiceRule(
        string name, string parentSource, string childSource, string childSourceType, DataSourceType childDsType, bool executable, TreeOutcome outcome)
    {
        _ = name;
        _ = outcome;

        var parent = Meta(parentSource, "Sqlite");
        var child = Meta(childSource, childSourceType, childDsType);

        RelationExecutability.IsSameExecutionSource(parent, child).ShouldBe(executable);
        RelationExecutability.IsSameExecutionSource(child, parent).ShouldBe(executable); // symmetric
    }

    [Fact]
    public void IsSameExecutionSource_DifferentDialectSameSourceName_NotExecutable()
    {
        RelationExecutability.IsSameExecutionSource(Meta("pg1", "PostgreSql"), Meta("pg1", "SqlServer")).ShouldBeFalse();
    }

    [Fact]
    public void IsSameExecutionSource_DifferentDomainSameSource_IsExecutable()
    {
        RelationExecutability.IsSameExecutionSource(Meta("pg1", "PostgreSql", domain: "sales"), Meta("pg1", "PostgreSql", domain: "crm")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("PostgreSql", "postgres")]
    [InlineData("PostgreSQL", "pgsql")]
    [InlineData("SqlServer", "mssql")]
    public void IsSameExecutionSource_DialectAliases_AreTheSameDialect(string first, string second)
    {
        RelationExecutability.IsSameExecutionSource(Meta("s", first), Meta("s", second)).ShouldBeTrue();
    }

    [Fact]
    public void IsSameExecutionSource_UnparseableDialect_FailsClosed()
    {
        RelationExecutability.IsSameExecutionSource(Meta("s", "NoSuchDb"), Meta("s", "NoSuchDb")).ShouldBeFalse();
    }

    [Fact]
    public void IsSameExecutionSource_WhitespaceVariantOfSourceName_IsADifferentSource()
    {
        // No trimming or normalization: a variant is omitted (safe direction).
        RelationExecutability.IsSameExecutionSource(Meta("pg1", "PostgreSql"), Meta("pg1 ", "PostgreSql")).ShouldBeFalse();
    }

    [Fact]
    public void HasSameSourceName_IgnoresDialectAndDataSourceType()
    {
        RelationExecutability.HasSameSourceName(Meta("pg1", "PostgreSql"), Meta("PG1", "SqlServer", DataSourceType.HttpDeclarative)).ShouldBeTrue();
    }

    [Fact]
    public void Predicates_RejectNull()
    {
        var table = Meta("pg1", "PostgreSql");

        Should.Throw<ArgumentNullException>(() => RelationExecutability.IsSameExecutionSource(null!, table));
        Should.Throw<ArgumentNullException>(() => RelationExecutability.IsSameExecutionSource(table, null!));
        Should.Throw<ArgumentNullException>(() => RelationExecutability.HasSameSourceName(null!, table));
    }
}
