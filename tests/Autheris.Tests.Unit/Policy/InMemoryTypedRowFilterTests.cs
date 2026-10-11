using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0l: the in-memory row filter reads the marker text of a typed policy together with its parameters.</summary>
public sealed class InMemoryTypedRowFilterTests
{
    private static readonly TableMetadata Meta = new()
    {
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "region", DataType = "varchar" }]
    };

    private static List<IReadOnlyDictionary<string, object?>> Rows() =>
    [
        new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU" },
        new Dictionary<string, object?> { ["id"] = 2, ["region"] = "US" },
        new Dictionary<string, object?> { ["id"] = 3, ["region"] = "O'Brien" }
    ];

    [Fact]
    public void MarkerText_WithItsParameters_FiltersTheRows()
    {
        var filtered = InMemoryRowFilterEvaluator.FilterRows(Rows(), "\"region\" = @__gql_rls_0", Meta,
            new Dictionary<string, object?> { ["@__gql_rls_0"] = "EU" });

        filtered.Select(r => r["id"]).ShouldBe(new object?[] { 1 });
    }

    [Fact]
    public void HostileValue_IsOnlyEverAValue()
    {
        var filtered = InMemoryRowFilterEvaluator.FilterRows(Rows(), "\"region\" = @__gql_rls_0", Meta,
            new Dictionary<string, object?> { ["@__gql_rls_0"] = "x' OR '1'='1" });

        filtered.ShouldBeEmpty();
    }

    [Fact]
    public void ValueWithAQuote_MatchesExactly()
    {
        var filtered = InMemoryRowFilterEvaluator.FilterRows(Rows(), "\"region\" = @__gql_rls_0", Meta,
            new Dictionary<string, object?> { ["@__gql_rls_0"] = "O'Brien" });

        filtered.Select(r => r["id"]).ShouldBe(new object?[] { 3 });
    }

    [Fact]
    public void UnboundMarker_FailsClosed_ReturningNoRows()
    {
        InMemoryRowFilterEvaluator.FilterRows(Rows(), "\"region\" = @__gql_rls_0", Meta, new Dictionary<string, object?>()).ShouldBeEmpty();
        InMemoryRowFilterEvaluator.FilterRows(Rows(), "\"region\" = @__gql_rls_0", Meta).ShouldBeEmpty();
    }

    [Fact]
    public void LegacyText_WithoutParameters_StillWorks()
    {
        InMemoryRowFilterEvaluator.FilterRows(Rows(), "region = 'EU'", Meta).Select(r => r["id"]).ShouldBe(new object?[] { 1 });
    }
}
