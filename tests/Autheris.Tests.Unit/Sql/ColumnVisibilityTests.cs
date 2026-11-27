namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>A table is fully visible when the decision allows it and no catalog column is hidden from the caller; a masked column is visible.</summary>
public sealed class ColumnVisibilityTests
{
    private static readonly TableIdentifier Id = new("default", "public", "orders");

    private static TableMetadata Meta() => new()
    {
        Identifier = Id,
        Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL", SourceName = "default" },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "secret", DataType = "varchar" }]
    };

    private static TableAccessDecision Allowed(params (string Column, ColumnAccessLevel Level)[] levels)
    {
        var access = new Dictionary<string, ColumnAccessLevel>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (column, level) in levels)
        {
            access[column] = level;
        }

        return TableAccessDecision.Allowed(Id, access, null, hasUnconstrainedColumnAllow: true);
    }

    [Fact]
    public void AllowedWithoutRestrictions_IsFullyVisible() =>
        ColumnVisibility.IsFullyVisible(Allowed(), Meta()).ShouldBeTrue();

    [Fact]
    public void MaskedColumn_CountsAsVisible() =>
        ColumnVisibility.IsFullyVisible(Allowed(("secret", ColumnAccessLevel.Mask)), Meta()).ShouldBeTrue();

    [Fact]
    public void DeniedColumn_IsNotFullyVisible() =>
        ColumnVisibility.IsFullyVisible(Allowed(("secret", ColumnAccessLevel.Deny)), Meta()).ShouldBeFalse();

    [Fact]
    public void DeniedColumn_MatchedCaseInsensitively_IsNotFullyVisible() =>
        ColumnVisibility.IsFullyVisible(Allowed(("SECRET", ColumnAccessLevel.Deny)), Meta()).ShouldBeFalse();

    [Fact]
    public void DeniedDecision_IsNotFullyVisible() =>
        ColumnVisibility.IsFullyVisible(TableAccessDecision.Denied(Id, "no consent"), Meta()).ShouldBeFalse();
}
