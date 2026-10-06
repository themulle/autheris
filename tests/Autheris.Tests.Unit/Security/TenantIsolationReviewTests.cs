namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-04 deep dive: E-5 (tenant column spellings) and E-6 (in-memory RLS as strict as SQL).</summary>
public sealed class TenantIsolationReviewTests
{
    private static readonly TableIdentifier Table = new("crm", "dbo", "customers");

    private static TableMetadata Meta(params string[] columns) => new()
    {
        Identifier = Table,
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = c == "id" ? "int" : "varchar" }).ToList()
    };

    [Theory]
    [InlineData("tenant_id", "tenant_id")]
    [InlineData("TenantId", "TenantId")]
    [InlineData("tenantId", "tenantId")]
    [InlineData("TENANT-ID", "TENANT-ID")]
    public void TenantColumnName_FindsUsualSpellings(string column, string expected)
    {
        Meta("id", column).TenantColumnName.ShouldBe(expected);
    }

    [Fact]
    public void TenantColumnName_PrefersExactSnakeCase()
    {
        Meta("TenantId", "tenant_id").TenantColumnName.ShouldBe("tenant_id");
    }

    [Fact]
    public void TenantColumnName_NoTenantColumn_IsNull()
    {
        Meta("id", "name", "tenant_name").TenantColumnName.ShouldBeNull();
    }

    [Fact]
    public void FilterRows_IsCaseSensitive_LikeSqlOrdinal()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU" },
            new Dictionary<string, object?> { ["id"] = 2, ["region"] = "eu" }
        };

        var filtered = GatewayExecutionService.FilterRows(rows, "[region] = 'EU'", Meta("id", "region"));

        filtered.Count.ShouldBe(1);
        filtered[0]["id"].ShouldBe(1);
    }

    [Fact]
    public void FilterRows_RowWithoutReferencedColumn_IsExcluded_NotTreatedAsNull()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1 },                      // e.g. a CDC delete event: key only
            new Dictionary<string, object?> { ["id"] = 2, ["region"] = null }
        };

        var filtered = GatewayExecutionService.FilterRows(rows, "[region] IS NULL", Meta("id", "region"));

        filtered.Count.ShouldBe(1);
        filtered[0]["id"].ShouldBe(2);
    }
}
