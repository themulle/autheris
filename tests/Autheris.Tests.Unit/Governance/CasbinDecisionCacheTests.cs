namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class CasbinDecisionCacheTests
{
    private static readonly TableIdentifier Table = new("sales", "dbo", "orders");

    private static SecurityEvaluationContext Ctx(
        string[]? groups = null,
        string[]? columns = null,
        Dictionary<string, object?>? attributes = null,
        DatabaseDialect? dialect = null,
        string[]? tableColumns = null,
        string user = "S-1-5-21-u1")
        => new(
            new Sid(user),
            (groups ?? []).Select(g => new Sid(g)).ToArray(),
            new TenantId("t1"),
            Table,
            columns ?? [],
            IPAddress.Loopback,
            DateTimeOffset.UnixEpoch,
            "purpose",
            attributes,
            dialect)
        {
            TableColumns = tableColumns
        };

    [Fact]
    public void DecisionKey_DifferentTargetDialect_IsCacheMiss()
    {
        var pg = DecisionKey.Create(1, Ctx(dialect: DatabaseDialect.PostgreSql), "read");
        var sql = DecisionKey.Create(1, Ctx(dialect: DatabaseDialect.SqlServer), "read");
        var none = DecisionKey.Create(1, Ctx(dialect: null), "read");

        pg.Equals(sql).ShouldBeFalse();
        pg.Equals(none).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_DifferentTableColumns_IsCacheMiss()
    {
        var a = DecisionKey.Create(1, Ctx(tableColumns: ["id", "name"]), "read");
        var b = DecisionKey.Create(1, Ctx(tableColumns: ["id", "name", "ssn"]), "read");
        var c = DecisionKey.Create(1, Ctx(tableColumns: null), "read");

        a.Equals(b).ShouldBeFalse();
        a.Equals(c).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_GroupsWithSeparatorChars_DoNotCollide()
    {
        var joined = DecisionKey.Create(1, Ctx(groups: ["a,b"]), "read");
        var split = DecisionKey.Create(1, Ctx(groups: ["a", "b"]), "read");
        joined.Equals(split).ShouldBeFalse();

        var attrJoined = DecisionKey.Create(1, Ctx(attributes: new() { ["a"] = "1;b=2" }), "read");
        var attrSplit = DecisionKey.Create(1, Ctx(attributes: new() { ["a"] = "1", ["b"] = "2" }), "read");
        attrJoined.Equals(attrSplit).ShouldBeFalse();

        var colJoined = DecisionKey.Create(1, Ctx(columns: ["x,y"]), "read");
        var colSplit = DecisionKey.Create(1, Ctx(columns: ["x", "y"]), "read");
        colJoined.Equals(colSplit).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_DifferentEpochOrAction_IsCacheMiss()
    {
        DecisionKey.Create(1, Ctx(), "read").Equals(DecisionKey.Create(2, Ctx(), "read")).ShouldBeFalse();
        DecisionKey.Create(1, Ctx(), "read").Equals(DecisionKey.Create(1, Ctx(), "write")).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_EqualFields_EqualHash()
    {
        var a = DecisionKey.Create(7, Ctx(groups: ["g2", "g1"], columns: ["c2", "c1"], attributes: new() { ["b"] = 2, ["a"] = "x" }), "read");
        var b = DecisionKey.Create(7, Ctx(groups: ["g1", "g2"], columns: ["c1", "c2"], attributes: new() { ["a"] = "x", ["b"] = 2 }), "read");

        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void DecisionKey_CollectionAttributeValues_DoNotCollide()
    {
        var a = DecisionKey.Create(1, Ctx(attributes: new() { ["roles"] = new List<string> { "a", "b" } }), "read");
        var b = DecisionKey.Create(1, Ctx(attributes: new() { ["roles"] = new List<string> { "a", "c" } }), "read");
        a.Equals(b).ShouldBeFalse();
    }
}
