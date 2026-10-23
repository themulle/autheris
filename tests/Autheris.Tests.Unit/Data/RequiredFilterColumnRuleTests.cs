namespace Autheris.Tests.Unit.Data;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Data.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>MISC-03/MISC-04: the shared required-filter-column rule matches whole identifiers only.</summary>
public sealed class RequiredFilterColumnRuleTests
{
    private static readonly TableIdentifier Id = new("sales", "public", "orders");

    private static TableMetadata Meta(string[] required, string[][]? groups = null, string? tenantColumn = null)
    {
        var columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "int" }, new() { ColumnName = "region", DataType = "varchar" } };
        if (tenantColumn != null)
        {
            columns.Add(new TableColumn { ColumnName = tenantColumn, DataType = "varchar" });
        }

        return new TableMetadata
        {
            Identifier = Id,
            Table = new Table
            {
                SourceName = "sales",
                SchemaName = "public",
                TableName = "orders",
                RequiredFilterColumns = [.. required],
                RequiredFilterColumnGroups = [.. (groups ?? []).Select(g => System.Collections.Immutable.ImmutableArray.Create(g))]
            },
            Columns = columns
        };
    }

    private static TableAccessDecision Decision(string? rowFilter) =>
        TableAccessDecision.Allowed(Id, new Dictionary<string, ColumnAccessLevel>(), rowFilter, hasUnconstrainedColumnAllow: true);

    private static bool Satisfied(TableMetadata meta, string? rowFilter, bool tenantCounts = true) =>
        RequiredFilterColumnRule.IsSatisfied(meta, Decision(rowFilter), clientFilterColumns: null, tenantCounts);

    [Fact]
    [Trait("Category", "Security")]
    public void LongerIdentifier_region_code_DoesNotSatisfy_region() =>
        Satisfied(Meta(["region"]), "region_code = 'EU'").ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void StringLiteralContainingColumn_DoesNotSatisfy() =>
        Satisfied(Meta(["region"]), "status = 'region'").ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void TenantIdColumn_DoesNotSatisfy_id() =>
        Satisfied(Meta(["id"], tenantColumn: "tenant_id"), "tenant_id = 'a'").ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void CommentContainingColumn_DoesNotSatisfy()
    {
        Satisfied(Meta(["region"]), "status = 1 -- region\n").ShouldBeFalse();
        Satisfied(Meta(["region"]), "status = 1 /* region */").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UnparseableRowFilter_FailsClosed()
    {
        Satisfied(Meta(["region"]), "status = 'unterminated region").ShouldBeFalse();
        Satisfied(Meta(["region"]), "   ").ShouldBeFalse();
        Satisfied(Meta(["region"]), null).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WholeIdentifier_Satisfies()
    {
        Satisfied(Meta(["region"]), "region = 'EU'").ShouldBeTrue();
        Satisfied(Meta(["region"]), "(\"REGION\" = 'EU')").ShouldBeTrue();
        Satisfied(Meta(["region"]), "t.region IN ('EU')").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TenantColumn_SatisfiesOnlyWhenCounted()
    {
        var meta = Meta(["tenant_id"], tenantColumn: "tenant_id");
        Satisfied(meta, null, tenantCounts: true).ShouldBeTrue();
        Satisfied(meta, null, tenantCounts: false).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GroupSatisfied_WhenAllMembersSatisfied()
    {
        var meta = Meta([], [["region", "id"]]);
        Satisfied(meta, "region = 'EU'").ShouldBeFalse();
        Satisfied(meta, "region = 'EU' AND id = 1").ShouldBeTrue();
    }

    [Fact]
    public void ClientFilterColumns_Satisfy()
    {
        RequiredFilterColumnRule.IsSatisfied(Meta(["region"]), Decision(null), ["REGION"], tenantColumnCounts: true).ShouldBeTrue();
    }

    [Fact]
    public void NoRequirement_IsSatisfied() =>
        Satisfied(Meta([]), null).ShouldBeTrue();

    [Fact]
    [Trait("Category", "Security")]
    public void NoSubstringMatchingOnRowFilterTextRemainsInSource()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var offenders = System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(dir!.FullName, "src"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(f), @"CombinedRowFilterSql\s*\.Contains\("))
            .ToList();
        offenders.ShouldBeEmpty();
    }
}
