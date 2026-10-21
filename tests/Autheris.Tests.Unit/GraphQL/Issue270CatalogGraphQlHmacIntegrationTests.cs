namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Persistence;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Issue270CatalogGraphQlHmacIntegrationTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository;
    private readonly ICatalogVersionProvider _versionProvider;
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();
    private readonly IGovernedTreeQueryService _treeService = Substitute.For<IGovernedTreeQueryService>();

    private readonly TableIdentifier _txTableId = new("finance", "dbo", "transactions");

    public Issue270CatalogGraphQlHmacIntegrationTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source=issue270_gql_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
                JournalMode = "DELETE"
            }
        });

        _repository = GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            options,
            logger: NullLogger<SqliteGovernanceRepository>.Instance);

        _versionProvider = new CatalogVersionProvider(_repository);

        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    private async Task<IRequestExecutor> CreateExecutorAsync(CatalogGraphQlTypeModule module, ClaimsPrincipal? principal = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITableMetadataRepository>(_repository);
        services.AddSingleton(_versionProvider);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(_treeService);

        var ctx = new DefaultHttpContext
        {
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "analyst")], "TestAuth"))
        };
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        return await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddTypeModule(_ => module)
            .BuildRequestExecutorAsync();
    }

    [Fact]
    public async Task ExecuteQuery_MaskedCaller_OnHmacBigIntColumn_Returns64CharHexString()
    {
        var meta = new TableMetadata
        {
            Identifier = _txTableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, DisplayName = "Transactions" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "transaction_id", DataType = "bigint", IsSensitive = true }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["transaction_id"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            }
        };

        await _repository.UpsertTableMetadataAsync(meta);

        const string hexHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse($$"""
            [
                {
                    "id": 1,
                    "transaction_id": "{{hexHash}}"
                }
            ]
            """));

        var module = new CatalogGraphQlTypeModule(_repository, _relationRepo, versionProvider: _versionProvider);
        var executor = await CreateExecutorAsync(module);

        var result = await executor.ExecuteAsync("{ finance_dbo_transactions { id transaction_id } }");
        var opResult = result.ExpectOperationResult();
        (opResult.Errors ?? []).ShouldBeEmpty();

        var json = opResult.ToJson();
        using var doc = JsonDocument.Parse(json);
        var txArray = doc.RootElement.GetProperty("data").GetProperty("finance_dbo_transactions");
        var firstRow = txArray[0];

        var returnedHash = firstRow.GetProperty("transaction_id").GetString();
        returnedHash.ShouldBe(hexHash);
        Regex.IsMatch(returnedHash!, "^[a-fA-F0-9]{64}$").ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteQuery_CleartextCaller_OnHmacBigIntColumn_ReturnsStringifiedNumber()
    {
        var meta = new TableMetadata
        {
            Identifier = _txTableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, DisplayName = "Transactions" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "transaction_id", DataType = "bigint", IsSensitive = true }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["transaction_id"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            }
        };

        await _repository.UpsertTableMetadataAsync(meta);

        // When a cleartext / unmasked caller executes, the underlying data source returns the raw numeric value
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse("""
            [
                {
                    "id": 1,
                    "transaction_id": 10000000001
                }
            ]
            """));

        var module = new CatalogGraphQlTypeModule(_repository, _relationRepo, versionProvider: _versionProvider);
        var executor = await CreateExecutorAsync(module);

        var result = await executor.ExecuteAsync("{ finance_dbo_transactions { id transaction_id } }");
        var opResult = result.ExpectOperationResult();
        (opResult.Errors ?? []).ShouldBeEmpty();

        var json = opResult.ToJson();
        using var doc = JsonDocument.Parse(json);
        var txArray = doc.RootElement.GetProperty("data").GetProperty("finance_dbo_transactions");
        var firstRow = txArray[0];

        var returnedVal = firstRow.GetProperty("transaction_id").GetString();
        returnedVal.ShouldBe("10000000001");
    }

    [Fact]
    public async Task DynamicSchemaRebuild_OnMaskingRuleUpsert_UpdatesGraphQLSchema()
    {
        // 1. Initial table without masking rule (transaction_id is bigint -> Long scalar)
        var initialMeta = new TableMetadata
        {
            Identifier = _txTableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, DisplayName = "Transactions" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "transaction_id", DataType = "bigint" }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

        await _repository.UpsertTableMetadataAsync(initialMeta);

        var module = new CatalogGraphQlTypeModule(_repository, _relationRepo, versionProvider: _versionProvider);
        var executor = await CreateExecutorAsync(module);

        var initialSchema = executor.Schema;
        initialSchema.Types.TryGetType<ObjectType>("finance_dbo_transactions", out var entityType).ShouldBeTrue();
        entityType!.Fields["transaction_id"].Type.NamedType().Name.ShouldBe("Long");

        // 2. Upsert table adding HMAC masking rule
        var updatedMeta = new TableMetadata
        {
            Identifier = _txTableId,
            Table = initialMeta.Table,
            Columns = initialMeta.Columns,
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["transaction_id"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            }
        };

        bool typesChangedFired = false;
        module.TypesChanged += (sender, args) => typesChangedFired = true;

        await _repository.UpsertTableMetadataAsync(updatedMeta);

        // 3. Trigger change detection
        var changed = await module.CheckForCatalogChangesAsync(CancellationToken.None);
        changed.ShouldBeTrue();
        typesChangedFired.ShouldBeTrue();

        // 4. Rebuild executor and verify schema updated to String
        var rebuiltExecutor = await CreateExecutorAsync(module);
        var rebuiltSchema = rebuiltExecutor.Schema;
        rebuiltSchema.Types.TryGetType<ObjectType>("finance_dbo_transactions", out var rebuiltEntityType).ShouldBeTrue();
        rebuiltEntityType!.Fields["transaction_id"].Type.NamedType().Name.ShouldBe("String");
    }
}
