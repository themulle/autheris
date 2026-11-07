namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Issue270GovernanceBatchHydrationTests : IDisposable
{
    private readonly PostgreSqlGovernanceRepository _repository;
    private readonly IEventBus _eventBus;

    public Issue270GovernanceBatchHydrationTests()
    {
        _eventBus = Substitute.For<IEventBus>();
        _repository = GovernanceTestDatabase.CreateRepository(
            Substitute.For<IEpochValidationService>(),
            logger: NullLogger<PostgreSqlGovernanceRepository>.Instance,
            eventBus: _eventBus);
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    [Fact]
    public async Task GetAllTablesAsync_PopulatesColumnMaskingRules()
    {
        var tableId = new TableIdentifier("finance", "dbo", "accounts");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                DisplayName = "Accounts"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "account_number", DataType = "bigint", IsSensitive = true }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["account_number"] = new MaskingRule
                {
                    RuleType = "HMAC_SHA256",
                    HmacKeyId = "master-key"
                }
            }
        };

        await _repository.UpsertTableMetadataAsync(metadata);

        var tables = await _repository.GetAllTablesAsync();
        var tableMeta = tables.First(t => t.Identifier == tableId);
        tableMeta.Identifier.ShouldBe(tableId);
        tableMeta.ColumnMaskingRules.ShouldNotBeNull();
        tableMeta.ColumnMaskingRules.ContainsKey("account_number").ShouldBeTrue();
        var rule = tableMeta.ColumnMaskingRules["account_number"];
        rule.RuleType.ShouldBe("HMAC_SHA256");
        rule.IsHmac.ShouldBeTrue();
        rule.HmacKeyId.ShouldBe("master-key");
    }

    [Fact]
    public async Task UpsertTableMetadataAsync_WhenMaskingRuleAdded_BumpsStructureVersion()
    {
        var tableId = new TableIdentifier("finance", "dbo", "transactions");
        var initialMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                DisplayName = "Transactions"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "transaction_id", DataType = "bigint" }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

        await _repository.UpsertTableMetadataAsync(initialMetadata);
        var v1 = await _repository.GetCatalogVersionAsync();

        var updatedMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = initialMetadata.Table,
            Columns = initialMetadata.Columns,
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["transaction_id"] = new MaskingRule
                {
                    RuleType = "HMAC_SHA256"
                }
            }
        };

        await _repository.UpsertTableMetadataAsync(updatedMetadata);
        var v2 = await _repository.GetCatalogVersionAsync();

        v2.StructureVersion.ShouldBe(v1.StructureVersion + 1);
        await _eventBus.Received().PublishAsync(
            ICatalogVersionProvider.CatalogChangedEventChannel,
            Arg.Is<CatalogChangedEvent>(e => e.StructureVersion == v2.StructureVersion),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertTableMetadataAsync_WhenMaskingRuleRemoved_BumpsStructureVersion()
    {
        var tableId = new TableIdentifier("finance", "dbo", "payments");
        var initialMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                DisplayName = "Payments"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "payment_id", DataType = "bigint" }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["payment_id"] = new MaskingRule { RuleType = "HMAC" }
            }
        };

        await _repository.UpsertTableMetadataAsync(initialMetadata);
        var v1 = await _repository.GetCatalogVersionAsync();

        var unmaskedMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = initialMetadata.Table,
            Columns = initialMetadata.Columns,
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

        await _repository.UpsertTableMetadataAsync(unmaskedMetadata);
        var v2 = await _repository.GetCatalogVersionAsync();

        v2.StructureVersion.ShouldBe(v1.StructureVersion + 1);
    }

    [Fact]
    public async Task UpsertTableMetadataAsync_WhenOnlyDescriptionChanged_DoesNotBumpStructureVersion()
    {
        var tableId = new TableIdentifier("finance", "dbo", "invoices");
        var initialMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                DisplayName = "Invoices",
                Description = "Initial description"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "invoice_id", DataType = "bigint" }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

        await _repository.UpsertTableMetadataAsync(initialMetadata);
        var v1 = await _repository.GetCatalogVersionAsync();

        var updatedMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                DisplayName = "Invoices",
                Description = "Updated business description"
            },
            Columns = initialMetadata.Columns,
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = initialMetadata.ColumnMaskingRules
        };

        await _repository.UpsertTableMetadataAsync(updatedMetadata);
        var v2 = await _repository.GetCatalogVersionAsync();

        v2.StructureVersion.ShouldBe(v1.StructureVersion);
        v2.ContentVersion.ShouldBe(v1.ContentVersion + 1);
    }

    [Fact]
    public void HasMaskingRulesChanged_AccuratelyDetectsChanges()
    {
        var empty = ImmutableDictionary<string, MaskingRule>.Empty;
        var rule1 = new MaskingRule { RuleType = "HMAC", HmacKeyId = "k1" };
        var rule2 = new MaskingRule { RuleType = "HMAC", HmacKeyId = "k1" };
        var rule3 = new MaskingRule { RuleType = "HMAC_SHA256", HmacKeyId = "k1" };
        var rule4 = new MaskingRule { RuleType = "HMAC", HmacKeyId = "k2" };
        var rule5 = new MaskingRule { RuleType = "REDACT", Replacement = "XXXX" };

        var dict1 = new Dictionary<string, MaskingRule> { ["col"] = rule1 };
        var dict2 = new Dictionary<string, MaskingRule> { ["col"] = rule2 };
        var dict3 = new Dictionary<string, MaskingRule> { ["col"] = rule3 };
        var dict4 = new Dictionary<string, MaskingRule> { ["col"] = rule4 };
        var dict5 = new Dictionary<string, MaskingRule> { ["col"] = rule5 };

        CatalogSqlCore.HasMaskingRulesChanged(empty, empty).ShouldBeFalse();
        CatalogSqlCore.HasMaskingRulesChanged(dict1, dict2).ShouldBeFalse();
        CatalogSqlCore.HasMaskingRulesChanged(dict1, empty).ShouldBeTrue();
        CatalogSqlCore.HasMaskingRulesChanged(empty, dict1).ShouldBeTrue();
        CatalogSqlCore.HasMaskingRulesChanged(dict1, dict3).ShouldBeTrue();
        CatalogSqlCore.HasMaskingRulesChanged(dict1, dict4).ShouldBeTrue();
        CatalogSqlCore.HasMaskingRulesChanged(dict1, dict5).ShouldBeTrue();
    }
}
