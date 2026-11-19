namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Modules.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

/// <summary>Setup for the semantic manifest import governance regression cases.</summary>
internal static class SemanticImportGovernanceHelper
{
    private const string Manifest = """
    { "semantic_models": [ { "name": "orders_source", "description": "Orders",
        "node_relation": { "relation_name": "analytics.public.orders" },
        "entities": [ { "name": "order_id", "type": "primary" } ],
        "measures": [ { "name": "order_total", "agg": "sum", "expr": "amount" } ],
        "dimensions": [ { "name": "status", "type": "categorical" } ] } ],
      "metrics": [] }
    """;

    private static readonly TenantId Tenant = Autheris.Tests.Common.TestTenants.Default;

    internal static TableMetadata Stored(Func<TableMetadata, TableMetadata> stricten)
    {
        var id = Guid.NewGuid();
        var identifier = SemanticVirtualTables.IdentifierFor(Tenant, "orders_source");
        var baseMeta = new TableMetadata
        {
            Identifier = identifier,
            Table = new Table
            {
                Id = id,
                SchemaName = SemanticVirtualTables.SchemaFor(Tenant),
                TableName = "orders_source",
                DocumentationSource = SemanticVirtualTables.SourceMarker,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { TableId = id, ColumnName = "status", DataType = "varchar" },
                new TableColumn { TableId = id, ColumnName = "order_total", DataType = "decimal" }
            ]
        };
        return stricten(baseMeta);
    }

    internal static async Task<TableMetadata> ReimportAsync(TableMetadata? stored)
    {
        TableMetadata? written = null;
        var repository = Substitute.For<ITableMetadataRepository>();
        repository.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(stored);
        repository.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => written = m), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<TableMetadata>()));
        var single = SemanticCatalogTestKit.Mode(Autheris.Tests.Common.TenancyShape.Single);
        var service = new DbtSemanticManifestIngestionService(new InMemorySemanticCatalog(single),
            NullLogger<DbtSemanticManifestIngestionService>.Instance, repository, gatewayOptions: single);
        await service.ParseAndIngestAsync(Manifest, Tenant);
        written.ShouldNotBeNull();
        return written;
    }

    internal static async Task AssertAsync(string bullet)
    {
        switch (bullet)
        {
            case "reimport_keeps_stricter_table_governance":
            {
                var stored = Stored(m => m with
                {
                    Table = m.Table with
                    {
                        Sensitivity = "restricted",
                        RequiresFourEyes = true,
                        MinGroupSize = 20,
                        NonPersonal = false,
                        RequiredFilterColumns = ["status"]
                    }
                });
                var result = await ReimportAsync(stored);
                result.Table.Sensitivity.ShouldBe("restricted");
                result.Table.RequiresFourEyes.ShouldBeTrue();
                result.Table.MinGroupSize.ShouldBe(20);
                result.Table.RequiredFilterColumns.ShouldContain("status");
                break;
            }
            case "reimport_does_not_reactivate":
            {
                var stored = Stored(m => m with { Table = m.Table with { IsActive = false } });
                (await ReimportAsync(stored)).Table.IsActive.ShouldBeFalse();
                break;
            }
            case "reimport_keeps_masking_rules":
            {
                var stored = Stored(m => m with
                {
                    ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["status"] = new MaskingRule { RuleType = "REDACT" } }
                });
                var result = await ReimportAsync(stored);
                result.ColumnMaskingRules.ShouldContainKey("status");
                result.ColumnMaskingRules["status"].RuleType.ShouldBe("REDACT");
                break;
            }
            case "reimport_keeps_column_governance":
            {
                var stored = Stored(m => m with
                {
                    ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>
                    {
                        ["status"] = [new ColumnClassification { TableColumnId = "status", Code = "pii", Source = "manual" }]
                    }
                });
                var result = await ReimportAsync(stored);
                result.ColumnClassifications.ShouldContainKey("status");
                result.ColumnClassifications["status"].ShouldContain(c => c.Code == "pii");
                break;
            }
            case "new_table_imported_unchanged":
            {
                var result = await ReimportAsync(null);
                result.Table.IsActive.ShouldBeTrue();
                result.Table.TableName.ShouldBe("orders_source");
                result.Columns.Select(c => c.ColumnName).ShouldBe(["order_id", "status", "order_total"], ignoreOrder: true);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet));
        }
    }
}
