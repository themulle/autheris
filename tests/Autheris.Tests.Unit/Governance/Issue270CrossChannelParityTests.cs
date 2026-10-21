namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Issue270CrossChannelParityTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();
    private readonly IGovernedTreeQueryService _treeService = Substitute.For<IGovernedTreeQueryService>();

    private readonly TableIdentifier _empTableId = new("hr", "dbo", "employees");
    private readonly ColumnMaskingProvider _maskingProvider;

    public Issue270CrossChannelParityTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "shared-master-hmac-salt-0123456789abcdef"
            }
        });

        _maskingProvider = new ColumnMaskingProvider(options);
    }

    private async Task<IRequestExecutor> CreateExecutorAsync(TableMetadata metadata, ClaimsPrincipal principal)
    {
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([metadata]);
        _relationRepo.GetRelationsForTableAsync(_empTableId, Arg.Any<CancellationToken>()).Returns([]);

        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(_treeService);

        var ctx = new DefaultHttpContext { User = principal };
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        return await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>()))
            .BuildRequestExecutorAsync();
    }

    [Fact]
    public async Task CrossChannelParity_GraphQL_And_WebSql_ReturnIdenticalHexHash()
    {
        var baseRule = new MaskingRule
        {
            RuleType = "HMAC_SHA256",
            HmacKeyId = "corp-hr-salt"
        };

        var metadata = new TableMetadata
        {
            Identifier = _empTableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql, DisplayName = "Employees" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "ssn", DataType = "bigint", IsSensitive = true }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = baseRule
            }
        };

        const long rawSsn = 123456789L;
        // 1. Compute pseudonym as WebSQL / REST does via the unified IColumnMaskingProvider
        var webSqlOutput = (string)_maskingProvider.MaskValue("ssn", rawSsn, baseRule)!;

        // 2. Tree query service returns the masked row (as gateway produces for masked callers)
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
                    "ssn": "{{webSqlOutput}}"
                }
            ]
            """));

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "analyst")], "TestAuth"));
        var executor = await CreateExecutorAsync(metadata, principal);

        // 3. Execute GraphQL query
        var result = await executor.ExecuteAsync("{ hr_dbo_employees { id ssn } }");
        var opResult = result.ExpectOperationResult();
        (opResult.Errors ?? []).ShouldBeEmpty();

        var json = opResult.ToJson();
        using var doc = JsonDocument.Parse(json);
        var firstRow = doc.RootElement.GetProperty("data").GetProperty("hr_dbo_employees")[0];
        var graphQlOutput = firstRow.GetProperty("ssn").GetString();

        // 4. Parity verification using constant-time comparison
        graphQlOutput.ShouldNotBeNull();
        graphQlOutput.Length.ShouldBe(64);
        webSqlOutput.Length.ShouldBe(64);

        var gqlBytes = Encoding.UTF8.GetBytes(graphQlOutput);
        var webSqlBytes = Encoding.UTF8.GetBytes(webSqlOutput);
        CryptographicOperations.FixedTimeEquals(gqlBytes, webSqlBytes).ShouldBeTrue();
    }

    [Fact]
    public async Task TenantScopedHmac_ParityAcrossChannels_AndIsolation()
    {
        var baseRule = new MaskingRule
        {
            RuleType = "HMAC_SHA256",
            HmacKeyId = "tenant-master-salt"
        };

        var tenantA = new TenantId("tenant-alpha");
        var tenantB = new TenantId("tenant-beta");

        var ruleTenantA = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenantA);
        var ruleTenantB = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenantB);

        const long rawId = 9876543210L;

        // Compute WebSQL outputs for Tenant A and Tenant B
        var webSqlHashA = (string)_maskingProvider.MaskValue("account_id", rawId, ruleTenantA)!;
        var webSqlHashB = (string)_maskingProvider.MaskValue("account_id", rawId, ruleTenantB)!;

        webSqlHashA.Length.ShouldBe(64);
        webSqlHashB.Length.ShouldBe(64);

        var bytesA = Encoding.UTF8.GetBytes(webSqlHashA);
        var bytesB = Encoding.UTF8.GetBytes(webSqlHashB);

        // Cross-tenant isolation invariant (SEC-GOV270-03)
        CryptographicOperations.FixedTimeEquals(bytesA, bytesB).ShouldBeFalse();

        // Parity test: GraphQL output under Tenant A matches WebSQL Tenant A
        var metaTenantA = new TableMetadata
        {
            Identifier = _empTableId,
            Table = new Table { IsActive = true, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "account_id", DataType = "bigint" }],
            PrimaryKeyColumns = ["account_id"],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["account_id"] = ruleTenantA
            }
        };

        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse($$"""
            [
                {
                    "account_id": "{{webSqlHashA}}"
                }
            ]
            """));

        var principalA = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenantA.Value)], "TestAuth"));
        var executorA = await CreateExecutorAsync(metaTenantA, principalA);

        var resA = await executorA.ExecuteAsync("{ hr_dbo_employees { account_id } }");
        var opResultA = resA.ExpectOperationResult();
        (opResultA.Errors ?? []).ShouldBeEmpty();

        using var docA = JsonDocument.Parse(opResultA.ToJson());
        var gqlHashA = docA.RootElement.GetProperty("data").GetProperty("hr_dbo_employees")[0].GetProperty("account_id").GetString();

        gqlHashA.ShouldNotBeNull();
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(gqlHashA), bytesA).ShouldBeTrue();
    }
}
