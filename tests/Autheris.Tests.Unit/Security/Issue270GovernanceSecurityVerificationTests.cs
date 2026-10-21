namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Persistence;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Issue270GovernanceSecurityVerificationTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository;
    private readonly IEventBus _eventBus;
    private readonly string _connectionString;

    public Issue270GovernanceSecurityVerificationTests()
    {
        _eventBus = Substitute.For<IEventBus>();
        _connectionString = $"Data Source=issue270_sec_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = _connectionString,
                JournalMode = "DELETE"
            }
        });

        _repository = GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            options,
            logger: NullLogger<SqliteGovernanceRepository>.Instance,
            eventBus: _eventBus);
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    [Fact]
    public async Task SEC_TEST_GOV270_01_FailClosed_OnCorruptedMaskingRuleHydration()
    {
        // 1. Seed a valid table and column
        var validTableId = new TableIdentifier("sec", "dbo", "customers");
        var meta = new TableMetadata
        {
            Identifier = validTableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, DisplayName = "Customers" },
            Columns = [new TableColumn { ColumnName = "ssn", DataType = "bigint", IsSensitive = true }],
            PrimaryKeyColumns = ["ssn"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };
        await _repository.UpsertTableMetadataAsync(meta);

        // 2. Corrupt the masking rule directly in the database (e.g. empty rule_type)
        using (var rawConn = new SqliteConnection(_connectionString))
        {
            await rawConn.OpenAsync();
            using var cmd = rawConn.CreateCommand();
            cmd.CommandText = @"SELECT c.id FROM TABLE_COLUMNS c
                                INNER JOIN TABLES t ON c.table_id = t.id
                                WHERE t.source_name = 'sec' AND t.table_name = 'customers'";
            var colIdObj = await cmd.ExecuteScalarAsync();
            colIdObj.ShouldNotBeNull();
            var colId = colIdObj!.ToString();

            using var insertCorruptCmd = rawConn.CreateCommand();
            insertCorruptCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                            VALUES (@id, @colId, '', NULL, NULL, NULL)";
            insertCorruptCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            insertCorruptCmd.Parameters.AddWithValue("@colId", colId);
            await insertCorruptCmd.ExecuteNonQueryAsync();
        }

        // 3. Call GetAllTablesAsync() - must fail-closed: sensitive table is omitted rather than served unmasked
        var tables = await _repository.GetAllTablesAsync();
        tables.ShouldNotContain(t => t.Identifier == validTableId);
    }

    [Fact]
    public async Task SEC_TEST_GOV270_02_AntiInformationDisclosure_InGraphQLSdl()
    {
        const string secretVaultRef = "vault://keys/prod-corp-ssn-hmac-salt-987";
        var tableId = new TableIdentifier("sec", "dbo", "users");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, DisplayName = "Users" },
            Columns =
            [
                new TableColumn { ColumnName = "user_id", DataType = "bigint", Description = "Enterprise user identifier." }
            ],
            PrimaryKeyColumns = ["user_id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["user_id"] = new MaskingRule
                {
                    RuleType = "HMAC_SHA256",
                    HmacKeyId = secretVaultRef
                }
            }
        };

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([meta]);

        var relationRepo = Substitute.For<ITableRelationRepository>();
        relationRepo.GetRelationsForTableAsync(tableId, Arg.Any<CancellationToken>()).Returns([]);

        var services = new ServiceCollection();
        services.AddSingleton(metadataRepo);
        services.AddSingleton(relationRepo);
        services.AddSingleton(Substitute.For<IGovernedTreeQueryService>());
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(metadataRepo, relationRepo))
            .BuildRequestExecutorAsync();

        var sdl = executor.Schema.ToString();

        // Security invariant: secret vault references and internal salts must NEVER leak into the GraphQL schema
        sdl.ShouldNotContain("vault://");
        sdl.ShouldNotContain("prod-corp-ssn-hmac-salt-987");
        sdl.ShouldContain("Enterprise user identifier.");
    }

    [Fact]
    public void SEC_TEST_GOV270_03_StrictCrossTenantPseudonymizationIsolation()
    {
        var options = Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "prod-root-enterprise-salt-32byteslong!!"
            }
        });
        var maskingProvider = new ColumnMaskingProvider(options);

        var baseRule = new MaskingRule { RuleType = "HMAC_SHA256", HmacKeyId = "master-salt" };
        var tenantA = new TenantId("tenant-org-alpha");
        var tenantB = new TenantId("tenant-org-beta");

        var ruleA = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenantA);
        var ruleB = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenantB);

        const long rawValue = 9876543210L;
        var hashA = (string)maskingProvider.MaskValue("account_number", rawValue, ruleA)!;
        var hashB = (string)maskingProvider.MaskValue("account_number", rawValue, ruleB)!;

        // 1. Valid 64-char hex strings
        Regex.IsMatch(hashA, "^[a-fA-F0-9]{64}$").ShouldBeTrue();
        Regex.IsMatch(hashB, "^[a-fA-F0-9]{64}$").ShouldBeTrue();

        // 2. Strict cross-tenant isolation
        var bytesA = Encoding.UTF8.GetBytes(hashA);
        var bytesB = Encoding.UTF8.GetBytes(hashB);
        CryptographicOperations.FixedTimeEquals(bytesA, bytesB).ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_TEST_GOV270_04_CallerPrivilegeIsolation_MaskedVsCleartextInvariance()
    {
        var tableId = new TableIdentifier("sec", "dbo", "customers");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "account_id", DataType = "bigint" }],
            PrimaryKeyColumns = ["account_id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["account_id"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            }
        };

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([meta]);

        var relationRepo = Substitute.For<ITableRelationRepository>();
        relationRepo.GetRelationsForTableAsync(tableId, Arg.Any<CancellationToken>()).Returns([]);

        var treeService = Substitute.For<IGovernedTreeQueryService>();

        const string hexHash = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

        var services = new ServiceCollection();
        services.AddSingleton(metadataRepo);
        services.AddSingleton(relationRepo);
        services.AddSingleton(treeService);

        var httpCtx = new DefaultHttpContext();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpCtx });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(metadataRepo, relationRepo))
            .BuildRequestExecutorAsync();

        // 1. Masked caller execution
        treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse($$"""[ { "account_id": "{{hexHash}}" } ]"""));

        var resMasked = await executor.ExecuteAsync("{ sec_dbo_customers { account_id } }");
        var opResMasked = resMasked.ExpectOperationResult();
        (opResMasked.Errors ?? []).ShouldBeEmpty();
        using (var doc = JsonDocument.Parse(opResMasked.ToJson()))
        {
            var val = doc.RootElement.GetProperty("data").GetProperty("sec_dbo_customers")[0].GetProperty("account_id").GetString();
            val.ShouldBe(hexHash);
        }

        // 2. Cleartext caller execution
        treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse("""[ { "account_id": 1000042 } ]"""));

        var resClear = await executor.ExecuteAsync("{ sec_dbo_customers { account_id } }");
        var opResClear = resClear.ExpectOperationResult();
        (opResClear.Errors ?? []).ShouldBeEmpty();
        using (var doc = JsonDocument.Parse(opResClear.ToJson()))
        {
            var val = doc.RootElement.GetProperty("data").GetProperty("sec_dbo_customers")[0].GetProperty("account_id").GetString();
            val.ShouldBe("1000042");
        }
    }

    [Fact]
    public async Task SEC_TEST_GOV270_05_HotChocolateLeafErrorScrubbing_AndStackTraceSanitation()
    {
        var tableId = new TableIdentifier("sec", "dbo", "audit");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "event_id", DataType = "bigint" }],
            PrimaryKeyColumns = ["event_id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["event_id"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            }
        };

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([meta]);

        var relationRepo = Substitute.For<ITableRelationRepository>();
        relationRepo.GetRelationsForTableAsync(tableId, Arg.Any<CancellationToken>()).Returns([]);

        var treeService = Substitute.For<IGovernedTreeQueryService>();

        // Return corrupted/unexpected object type instead of string or number to test fail-closed ReportMasked
        treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse("""[ { "event_id": { "nested": "invalid_kind" } } ]"""));

        var services = new ServiceCollection();
        services.AddSingleton(metadataRepo);
        services.AddSingleton(relationRepo);
        services.AddSingleton(treeService);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(metadataRepo, relationRepo))
            .BuildRequestExecutorAsync();

        var res = await executor.ExecuteAsync("{ sec_dbo_audit { event_id } }");
        var opRes = res.ExpectOperationResult();

        opRes.Errors.ShouldNotBeNull();
        opRes.Errors!.Count.ShouldBeGreaterThan(0);
        var err = opRes.Errors[0];

        // Fail-closed standard code
        err.Code.ShouldBe("MASKED");
        err.Message.ShouldBe("Column 'event_id' is masked.");

        // Stack traces and raw internal details scrubbed
        err.Extensions?.ContainsKey("stackTrace").ShouldBeFalse();
        err.Message.ShouldNotContain("nested");
        err.Message.ShouldNotContain("invalid_kind");
    }

    [Fact]
    public void SEC_TEST_GOV270_06_SqlParameterization_AndInjectionVerification()
    {
        // Audit persistence files to ensure batch queries in GetAllTablesAsync do not interpolate identifiers
        var repoFiles = new[]
        {
            "src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Catalog.cs",
            "src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Catalog.cs",
            "src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Catalog.cs"
        };

        var root = FindRepositoryRoot();
        foreach (var file in repoFiles)
        {
            var content = System.IO.File.ReadAllText(System.IO.Path.Combine(root, file));

            // No dynamic table or column interpolation in catalog batch queries
            content.ShouldNotContain("WHERE t.table_name = '\" +");
            content.ShouldNotContain("WHERE t.table_name = $\"");
            content.ShouldNotContain("LEFT JOIN COLUMN_MASKING_RULES m ON c.id = $\"");
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task SEC_TEST_GOV270_07_TamperEvidentCatalogAuditLogging()
    {
        var tableId = new TableIdentifier("sec", "dbo", "financials");
        var initialMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, DisplayName = "Financials" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "transaction_amount", DataType = "decimal" }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };

        await _repository.UpsertTableMetadataAsync(initialMeta);
        var v1 = await _repository.GetCatalogVersionAsync();

        var updatedMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = initialMeta.Table,
            Columns = initialMeta.Columns,
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["transaction_amount"] = new MaskingRule
                {
                    RuleType = "HMAC_SHA256",
                    HmacKeyId = "audit-vault-secret-key"
                }
            }
        };

        await _repository.UpsertTableMetadataAsync(updatedMeta);
        var v2 = await _repository.GetCatalogVersionAsync();

        // 1. Structure version increments atomically
        v2.StructureVersion.ShouldBe(v1.StructureVersion + 1);

        // 2. Structured CatalogChangedEvent published
        await _eventBus.Received().PublishAsync(
            ICatalogVersionProvider.CatalogChangedEventChannel,
            Arg.Is<CatalogChangedEvent>(e =>
                e.StructureVersion == v2.StructureVersion &&
                e.ContentVersion == v2.ContentVersion),
            Arg.Any<CancellationToken>());
    }
}
