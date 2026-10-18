namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #138 INC-1 P3: the single-row read (<c>GetTableMetadataAsync</c>, used by describe through the visibility service) returns the columns in
/// the same stable order as the list read (<c>GetAllTablesAsync</c>): registration order, never the order a join happens to produce.
/// </summary>
public sealed class CatalogColumnOrderTests : IDisposable
{
    private static readonly string[] Registered = ["id", "zeta", "serial_number", "alpha", "tonnage", "crane_type"];
    private static readonly TableIdentifier Id = new("default", "main", "cranes");
    private readonly SqliteGovernanceRepository _repo = Autheris.Tests.Unit.GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>());

    public void Dispose() => _repo.Dispose();

    private async Task SeedAsync(bool withMasking)
    {
        var columns = Registered.Select(n => new TableColumn { ColumnName = n, DataType = "TEXT" }).ToList();
        var masking = withMasking
            ? new System.Collections.Generic.Dictionary<string, MaskingRule> { ["tonnage"] = new MaskingRule { RuleType = "REDACT", Replacement = "x" } }
            : new System.Collections.Generic.Dictionary<string, MaskingRule>();
        await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = Id,
            Table = new Table { Id = Guid.NewGuid(), SourceName = Id.Domain, SchemaName = Id.Schema, TableName = Id.TableName, DisplayName = "cranes", IsActive = true },
            Columns = columns,
            ColumnMaskingRules = masking
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleRowRead_ColumnOrder_EqualsListRead_AndRegistrationOrder(bool withMasking)
    {
        await SeedAsync(withMasking);

        var single = (await _repo.GetTableMetadataAsync(Id))!.Columns.Select(c => c.ColumnName).ToArray();
        var list = (await _repo.GetAllTablesAsync()).Single(t => t.Identifier == Id).Columns.Select(c => c.ColumnName).ToArray();

        list.ShouldBe(Registered);
        single.ShouldBe(Registered);
    }

    [Fact]
    public async Task Reingest_UpdatedMiddleColumnAndAppendedColumn_KeepsRegistrationOrderPlusNew()
    {
        await SeedAsync(withMasking: false);
        var columns = Registered.Select(n => new TableColumn { ColumnName = n, DataType = n == "serial_number" ? "INTEGER" : "TEXT", Description = n == "serial_number" ? "changed" : null }).ToList();
        columns.Add(new TableColumn { ColumnName = "appended", DataType = "TEXT" });
        await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = Id,
            Table = new Table { Id = Guid.NewGuid(), SourceName = Id.Domain, SchemaName = Id.Schema, TableName = Id.TableName, DisplayName = "cranes", IsActive = true },
            Columns = columns
        });

        string[] expected = [.. Registered, "appended"];
        (await _repo.GetTableMetadataAsync(Id))!.Columns.Select(c => c.ColumnName).ToArray().ShouldBe(expected);
        (await _repo.GetAllTablesAsync()).Single(t => t.Identifier == Id).Columns.Select(c => c.ColumnName).ToArray().ShouldBe(expected);
    }
}
