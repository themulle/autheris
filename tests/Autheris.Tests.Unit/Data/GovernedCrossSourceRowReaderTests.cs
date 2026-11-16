namespace Autheris.Tests.Unit.Data;

using System;
using Autheris.Application.DataSources.Relations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>VF-02: the cross-source row reader builds a governed, quoted, parameterized statement and initializes the session.</summary>
[Trait("Category", "Security")]
public sealed class GovernedCrossSourceRowReaderTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static TableMetadata Target(string sourceType = "PostgreSql", bool tenanted = true) => new()
    {
        Identifier = new TableIdentifier("crm", "Sales", "Customers"),
        Table = new Table { SourceName = "crm", SchemaName = "Sales", TableName = "Customers", SourceType = sourceType, DataSourceType = DataSourceType.Sql, IsActive = true },
        Columns = tenanted
            ? [new TableColumn { ColumnName = "CustomerId" }, new TableColumn { ColumnName = "Name" }, new TableColumn { ColumnName = "tenant_id" }]
            : [new TableColumn { ColumnName = "CustomerId" }, new TableColumn { ColumnName = "Name" }]
    };

    private static CrossSourceKeyReadRequest Request(TableMetadata target, string[]? joinKeys = null, string[]? projected = null) =>
        new(target, joinKeys ?? ["customerid"], projected ?? ["name"], [new CompositeKey(new object?[] { 7 })], Tenant, "S-1-5-1");

    [Fact]
    public void ReadByKeys_QuotesTableAndColumns()
    {
        var (sql, _) = GovernedCrossSourceRowReader.BuildStatement(Request(Target()));

        sql.ShouldContain("FROM \"Sales\".\"Customers\"");
        sql.ShouldContain("\"CustomerId\" IN (@p0)");
        sql.ShouldContain("\"Name\"");
    }

    [Fact]
    public void ReadByKeys_UnknownJoinColumn_Throws() =>
        Should.Throw<InvalidOperationException>(() => GovernedCrossSourceRowReader.BuildStatement(Request(Target(), joinKeys: ["nope; DROP TABLE x"])));

    [Fact]
    public void ReadByKeys_UnknownProjectedColumn_Throws() =>
        Should.Throw<InvalidOperationException>(() => GovernedCrossSourceRowReader.BuildStatement(Request(Target(), projected: ["secret"])));

    [Fact]
    public void ReadByKeys_TenantedTarget_AddsBoundTenantPredicate()
    {
        var (sql, parameters) = GovernedCrossSourceRowReader.BuildStatement(Request(Target()));

        sql.ShouldContain("AND \"tenant_id\" = @vf_tenant");
        parameters.ShouldContain(p => p.Name == "@vf_tenant" && Equals(p.Value, "tenant-a"));
        sql.ShouldNotContain("tenant-a");
    }

    [Fact]
    public void ReadByKeys_UntenantedTarget_HasNoTenantPredicate() =>
        GovernedCrossSourceRowReader.BuildStatement(Request(Target(tenanted: false))).Sql.ShouldNotContain("tenant");

    [Fact]
    public void ReadByKeys_Oracle_UsesColonMarkers()
    {
        var (sql, parameters) = GovernedCrossSourceRowReader.BuildStatement(Request(Target("Oracle")));

        sql.ShouldContain("IN (:p0)");
        sql.ShouldContain("= :vf_tenant");
        parameters.ShouldContain(p => p.Name == "p0");
        sql.ShouldNotContain("@");
    }

    [Fact]
    public void ReadByKeys_ProjectsExplicitColumns_NoSelectStar()
    {
        var (sql, _) = GovernedCrossSourceRowReader.BuildStatement(Request(Target()));

        sql.ShouldNotContain("*");
        sql.ShouldStartWith("SELECT \"Name\", \"CustomerId\", \"tenant_id\" FROM");
    }
}
