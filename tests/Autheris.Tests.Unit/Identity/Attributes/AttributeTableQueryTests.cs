namespace Autheris.Tests.Unit.Identity.Attributes;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

/// <summary>PLAN-GOV-129 tests 6 and 7, ST-129-12 (BC-129-11): the lookup statement is fixed, identifiers are validated and quoted, values are bound.</summary>
[Trait("Category", "Security")]
public sealed class AttributeTableQueryTests
{
    private static Dictionary<string, string> Config(string table = "person_attributes", string schema = "hr", string key = "person_oid", string value = "cost_center", string? tenant = "tenant_id", string? issuer = null)
    {
        var config = new Dictionary<string, string>
        {
            ["dataSource"] = "hr_db", ["table"] = table, ["subjectKeyKind"] = "EntraObjectId", ["subjectKeyColumn"] = key, ["valueColumn"] = value
        };
        if (schema is not null) config["schema"] = schema;
        if (tenant is not null) config["tenantColumn"] = tenant;
        if (issuer is not null) config["issuerColumn"] = issuer;
        return config;
    }

    private static AttributeTableSpec Spec(Dictionary<string, string> config)
    {
        AttributeTableSpec.TryParse(config, out var spec, out var error).ShouldBeTrue(error);
        return spec!;
    }

    [Fact]
    public void Golden_SqlServer()
    {
        var q = AttributeTableQuery.Build(Spec(Config()), DatabaseDialect.SqlServer, "tenant_a", "oid-1", null, 50);
        q.Sql.ShouldBe("SELECT TOP (51) [cost_center], [tenant_id] FROM [hr].[person_attributes] WHERE [person_oid] = @ca_subject AND [tenant_id] COLLATE Latin1_General_100_BIN2 = @ca_tenant AND [cost_center] IS NOT NULL");
        q.Parameters.Select(p => (p.Key, p.Value)).ShouldBe([("@ca_subject", (object)"oid-1"), ("@ca_tenant", "tenant_a")]);
    }

    [Fact]
    public void Golden_PostgreSql_AndSqlite()
    {
        var pg = AttributeTableQuery.Build(Spec(Config()), DatabaseDialect.PostgreSql, "tenant_a", "oid-1", null, 50);
        pg.Sql.ShouldBe("SELECT \"cost_center\", \"tenant_id\" FROM \"hr\".\"person_attributes\" WHERE \"person_oid\" = @ca_subject AND \"tenant_id\" COLLATE \"C\" = @ca_tenant AND \"cost_center\" IS NOT NULL LIMIT 51");
        var lite = AttributeTableQuery.Build(Spec(Config(schema: null!)), DatabaseDialect.Sqlite, "tenant_a", "oid-1", null, 5);
        lite.Sql.ShouldBe("SELECT \"cost_center\", \"tenant_id\" FROM \"person_attributes\" WHERE \"person_oid\" = @ca_subject AND \"tenant_id\" COLLATE BINARY = @ca_tenant AND \"cost_center\" IS NOT NULL LIMIT 6");
    }

    [Fact]
    public void IssuerColumn_AddsAnIssuerPredicate_AndAParameter()
    {
        var q = AttributeTableQuery.Build(Spec(Config(tenant: null, issuer: "issuer")), DatabaseDialect.PostgreSql, "tenant_a", "oid-1", "https://login.example/", 10);
        q.Sql.ShouldContain("\"issuer\" COLLATE \"C\" = @ca_issuer");
        q.Parameters.Select(p => p.Key).ShouldBe(["@ca_subject", "@ca_issuer"]);
        q.IssuerOrdinal.ShouldBe(1);
        q.TenantOrdinal.ShouldBeNull();
    }

    [Fact]
    public void SubjectAndTenant_AreNeverInTheStatementText()
    {
        var q = AttributeTableQuery.Build(Spec(Config()), DatabaseDialect.SqlServer, "tenant_a", "x' OR '1'='1", null, 5);
        q.Sql.ShouldNotContain("OR '1'");
        q.Sql.ShouldNotContain("tenant_a");
    }

    [Theory]
    [InlineData("#temp")]
    [InlineData("tab#le")]
    [InlineData("a.b")]
    [InlineData("a\"b")]
    [InlineData("a]b")]
    [InlineData("a[b")]
    [InlineData("a'b")]
    [InlineData("a--b")]
    [InlineData("a/*b*/")]
    [InlineData("a;drop")]
    [InlineData("a b")]
    [InlineData("1abc")]
    [InlineData("tabelleä")]
    [InlineData("a$b")]
    public void ST_129_12_UnsafeIdentifiers_AreRefused_InEveryPosition(string identifier)
    {
        foreach (var config in new[]
        {
            Config(table: identifier), Config(schema: identifier), Config(key: identifier), Config(value: identifier), Config(tenant: identifier), Config(issuer: identifier)
        })
        {
            AttributeTableSpec.TryParse(config, out var spec, out var error).ShouldBeFalse();
            spec.ShouldBeNull();
            error.ShouldBe("UnsafeIdentifier");
            error.ShouldNotContain(identifier);
        }
    }

    [Fact]
    public void Identifier_Of129Characters_IsRefused_128IsAccepted()
    {
        AttributeTableSpec.TryParse(Config(table: new string('a', 129)), out _, out _).ShouldBeFalse();
        AttributeTableSpec.TryParse(Config(table: new string('a', 128)), out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void MissingFields_AndUnknownKeyKind_AreRefused()
    {
        var config = Config();
        config.Remove("valueColumn");
        AttributeTableSpec.TryParse(config, out _, out var error).ShouldBeFalse();
        error.ShouldBe("MissingConfiguration");

        var kind = Config();
        kind["subjectKeyKind"] = "Upn";
        AttributeTableSpec.TryParse(kind, out _, out error).ShouldBeFalse();
        error.ShouldBe("InvalidKeyKind");
    }

    [Fact]
    public void UnsupportedDialect_IsRefused() =>
        Should.Throw<System.NotSupportedException>(() => AttributeTableQuery.Build(Spec(Config()), DatabaseDialect.Oracle, "t", "k", null, 5));
}
