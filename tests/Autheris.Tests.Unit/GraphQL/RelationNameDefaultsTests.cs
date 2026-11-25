using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Issue #1099: the default relation name (dbt relationships without a name) and its recognition by the schema builder.</summary>
public sealed class RelationNameDefaultsTests
{
    [Theory]
    [InlineData("client", "client")]
    [InlineData("stg_client", "client")]
    [InlineData("stg_", "stg_")]
    [InlineData("STG_client", "STG_client")]
    [InlineData("dim_users", "dim_users")]
    public void ForParent_StripsTheStagingPrefix(string parent, string expected) =>
        RelationNameDefaults.ForParent(parent).ShouldBe(expected);

    [Theory]
    [InlineData("client", "client", true)]
    [InlineData("CLIENT", "client", true)]
    [InlineData("client", "stg_client", true)]
    [InlineData("stg_client", "stg_client", true)]
    [InlineData("air1", "client", false)]
    [InlineData("client_orders", "client", false)]
    [InlineData("", "client", false)]
    public void IsDefaultFor_RecognisesTheDefaultName(string relationName, string parentTable, bool expected) =>
        RelationNameDefaults.IsDefaultFor(relationName, parentTable).ShouldBe(expected);
}
