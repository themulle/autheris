namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogSchemaModelPartitioningTests
{
    private static TableMetadata CreateTable(TableIdentifier id)
    {
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                IsActive = true,
                SourceName = "src",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        };
    }

    [Fact]
    public async Task BuildAsync_WithTableFilter_PartitionsTables()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var tFinance = CreateTable(new TableIdentifier("finance", "dbo", "invoices"));
        var tSales = CreateTable(new TableIdentifier("sales", "dbo", "orders"));
        var tHr = CreateTable(new TableIdentifier("hr", "dbo", "employees"));

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([tFinance, tSales, tHr]);
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // Filter only sales tables
        var model = await CatalogSchemaModel.BuildFilteredAsync(metaRepo, relRepo, t => t.Identifier.Domain == "sales");

        model.Tables.Count.ShouldBe(1);
        model.Tables[0].Identifier.ShouldBe(tSales.Identifier);
    }

    [Fact]
    public async Task BuildAsync_WithoutTableFilter_IncludesAllTables()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var tFinance = CreateTable(new TableIdentifier("finance", "dbo", "invoices"));
        var tSales = CreateTable(new TableIdentifier("sales", "dbo", "orders"));

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([tFinance, tSales]);
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        model.Tables.Count.ShouldBe(2);
    }

    [Fact]
    public void GraphQlOptions_PartitioningDefaults_AreConfiguredCorrectly()
    {
        var options = new GraphQLOptions();

        options.SchemaPartitioning.ShouldBe(GraphQlSchemaPartitioningMode.None);
        options.PartitionAllowedDomains.ShouldBeEmpty();
        options.PartitionAllowedConsentScopes.ShouldBeEmpty();
    }
}
