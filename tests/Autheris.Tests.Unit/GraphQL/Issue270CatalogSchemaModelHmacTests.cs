namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Issue270CatalogSchemaModelHmacTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();

    private readonly TableIdentifier _tableId = new("analytics", "dbo", "metrics");

    [Fact]
    public async Task BuildAsync_WithHmacMaskingOnNonStringColumns_SetsCatalogFieldTypeString()
    {
        var metadata = new TableMetadata
        {
            Identifier = _tableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "bigint_col", DataType = "bigint" },
                new TableColumn { ColumnName = "int_col", DataType = "int" },
                new TableColumn { ColumnName = "decimal_col", DataType = "decimal(18,2)" },
                new TableColumn { ColumnName = "float_col", DataType = "float" }
            ],
            PrimaryKeyColumns = ["bigint_col"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["bigint_col"] = new MaskingRule { RuleType = "HMAC_SHA256" },
                ["int_col"] = new MaskingRule { RuleType = "HMAC" },
                ["decimal_col"] = new MaskingRule { RuleType = "HMAC_SHA256" },
                ["float_col"] = new MaskingRule { RuleType = "HMAC" }
            }
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([metadata]);
        _relationRepo.GetRelationsForTableAsync(_tableId, Arg.Any<CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(_metadataRepo, _relationRepo);
        var tableType = model.Tables.ShouldHaveSingleItem();

        tableType.Columns.Count.ShouldBe(4);
        foreach (var col in tableType.Columns)
        {
            col.FieldType.ShouldBe(CatalogFieldType.String, $"Column '{col.ColumnName}' with data type '{col.DataType}' should be mapped to String when HMAC masking is active.");
        }
    }

    [Fact]
    public async Task BuildAsync_WithoutHmacMasking_PreservesOriginalFieldTypes()
    {
        var metadata = new TableMetadata
        {
            Identifier = _tableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "bigint_col", DataType = "bigint" },
                new TableColumn { ColumnName = "int_col", DataType = "int" },
                new TableColumn { ColumnName = "decimal_col", DataType = "decimal(18,2)" },
                new TableColumn { ColumnName = "float_col", DataType = "float" }
            ],
            PrimaryKeyColumns = ["bigint_col"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([metadata]);
        _relationRepo.GetRelationsForTableAsync(_tableId, Arg.Any<CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(_metadataRepo, _relationRepo);
        var tableType = model.Tables.ShouldHaveSingleItem();

        tableType.Columns.First(c => c.ColumnName == "bigint_col").FieldType.ShouldBe(CatalogFieldType.Long);
        tableType.Columns.First(c => c.ColumnName == "int_col").FieldType.ShouldBe(CatalogFieldType.Int);
        tableType.Columns.First(c => c.ColumnName == "decimal_col").FieldType.ShouldBe(CatalogFieldType.Decimal);
        tableType.Columns.First(c => c.ColumnName == "float_col").FieldType.ShouldBe(CatalogFieldType.Float);
    }

    [Fact]
    public async Task Fingerprint_ChangesWhenHmacMaskingRuleIsAddedOrRemoved()
    {
        var unmaskedMeta = new TableMetadata
        {
            Identifier = _tableId,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "account_id", DataType = "bigint" }],
            PrimaryKeyColumns = ["account_id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

        var maskedMeta = new TableMetadata
        {
            Identifier = _tableId,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "account_id", DataType = "bigint" }],
            PrimaryKeyColumns = ["account_id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["account_id"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            }
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([unmaskedMeta]);
        _relationRepo.GetRelationsForTableAsync(_tableId, Arg.Any<CancellationToken>()).Returns([]);
        var unmaskedModel = await CatalogSchemaModel.BuildAsync(_metadataRepo, _relationRepo);
        var fpUnmasked = unmaskedModel.Fingerprint();

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([maskedMeta]);
        var maskedModel = await CatalogSchemaModel.BuildAsync(_metadataRepo, _relationRepo);
        var fpMasked = maskedModel.Fingerprint();

        fpMasked.ShouldNotBe(fpUnmasked);
        fpMasked.Length.ShouldBe(64);
    }
}
