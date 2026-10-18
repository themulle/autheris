namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Analysis;
using Autheris.GraphQL.Interceptors;
using FluentAssertions;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public sealed class QueryAnalysisExecutionCostModelTests
{
    public sealed class Order
    {
        public string Id { get; set; } = "1";
        public string Ssn { get; set; } = "123";
        public string Region { get; set; } = "EU";
    }

    public sealed class User
    {
        public string Id { get; set; } = "2";
        public string Name { get; set; } = "Alice";
    }

    public sealed class AppQuery
    {
        public List<Order> GetOrders(OrderSort? orderBy = null) => [new()];
        public List<User> GetUsers() => [new()];
    }

    public sealed class OrderSort
    {
        public string? Ssn { get; set; }
        public string? Id { get; set; }
    }

    private static ClaimsPrincipal CreatePrincipal(string sid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sid),
            new(ClaimTypes.Name, sid),
            new("sid", sid)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static TableMetadata CreateTable(
        string name,
        IReadOnlyDictionary<string, MaskingRule>? maskingRules = null,
        params (string Name, string Type, bool IsSensitive)[] cols)
    {
        var id = new TableIdentifier("sales", "public", name);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "public",
                TableName = name,
                DisplayName = name,
                Visibility = DatasetVisibility.Full
            },
            Columns = cols.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type, IsSensitive = c.IsSensitive }).ToList(),
            ColumnMaskingRules = maskingRules ?? new Dictionary<string, MaskingRule>(),
            PrimaryKeyColumns = ["id"]
        };
    }

    private static async Task<(IRequestExecutorProvider Provider, ISchemaDefinition Schema)> CreateTestExecutorProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer().AddQueryType<AppQuery>();
        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IRequestExecutorProvider>();
        var executor = await provider.GetExecutorAsync();
        return (provider, executor.Schema);
    }

    [Fact]
    public async Task ExactCostParityWithQueryCostAnalyzerRule()
    {
        var (provider, schema) = await CreateTestExecutorProviderAsync();
        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider);

        var ordersTable = CreateTable("orders", null, ("id", "varchar", false), ("region", "varchar", false));
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([ordersTable]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([new Consent
            {
                TableIdentifier = ordersTable.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeSid = new Sid("user-1"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(10)
            }]);

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            costAnalysisEngine: engine);

        var caller = CreatePrincipal("user-1");
        var queryStr = "{ orders { id region } }";
        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl(queryStr)], caller);

        var expectedCost = QueryCostAnalyzerRule.CalculateCost(Utf8GraphQLParser.Parse(queryStr), schema);
        result.ValidationReport.CostEstimate.EstimatedCost.Should().Be(expectedCost);
        result.ValidationReport.CostEstimate.RootFieldCosts.Should().NotBeNull();
        result.ValidationReport.CostEstimate.RootFieldCosts!["orders"].Should().Be(expectedCost);
    }

    [Fact]
    public async Task RootFieldCostsDictionaryPopulated_ForMultipleRootFields()
    {
        var (provider, schema) = await CreateTestExecutorProviderAsync();
        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider);

        var ordersTable = CreateTable("orders", null, ("id", "varchar", false), ("region", "varchar", false));
        var usersTable = CreateTable("users", null, ("id", "varchar", false), ("name", "varchar", false));

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([ordersTable, usersTable]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([
                new Consent { TableIdentifier = ordersTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1") },
                new Consent { TableIdentifier = usersTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1") }
            ]);

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            costAnalysisEngine: engine);

        var caller = CreatePrincipal("user-1");
        var queryStr = "{ orders { id } users { id } }";
        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl(queryStr)], caller);

        result.ValidationReport.CostEstimate.RootFieldCosts.Should().NotBeNull();
        result.ValidationReport.CostEstimate.RootFieldCosts!.ContainsKey("orders").Should().BeTrue();
        result.ValidationReport.CostEstimate.RootFieldCosts.ContainsKey("users").Should().BeTrue();

        var expectedTotal = result.ValidationReport.CostEstimate.RootFieldCosts["orders"] + result.ValidationReport.CostEstimate.RootFieldCosts["users"];
        result.ValidationReport.CostEstimate.EstimatedCost.Should().Be(expectedTotal);
    }

    [Fact]
    public async Task QueryTooComplex_ExceedingBudget_SetsIsValidFalse()
    {
        var (provider, _) = await CreateTestExecutorProviderAsync();
        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider);

        var ordersTable = CreateTable("orders", null, ("id", "varchar", false));
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([ordersTable]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([new Consent { TableIdentifier = ordersTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1") }]);

        // Budget set very low (5)
        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            analysisOptions: new QueryAnalysisOptions { MaxAllowedCost = 5 },
            costAnalysisEngine: engine);

        var caller = CreatePrincipal("user-1");
        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl("{ orders { id } }")], caller);

        result.ValidationReport.CostEstimate.IsWithinCostLimit.Should().BeFalse();
        result.ValidationReport.IsValid.Should().BeFalse();
        result.ValidationReport.Diagnostics.Should().Contain(d => d.Code == QueryDiagnosticCodes.QueryTooComplex);
    }

    [Fact]
    public async Task QueryTooComplex_ExceedingMaxRootFields_SetsIsValidFalse()
    {
        var (provider, _) = await CreateTestExecutorProviderAsync();
        var options = new GatewayOptions
        {
            GraphQL = new GraphQLOptions { MaxRootFieldsPerOperation = 3 }
        };

        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider, Options.Create(options));

        var ordersTable = CreateTable("orders", null, ("id", "varchar", false));
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([ordersTable]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([new Consent { TableIdentifier = ordersTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1") }]);

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            gatewayOptions: Options.Create(options),
            costAnalysisEngine: engine);

        var caller = CreatePrincipal("user-1");
        // 4 root fields exceeds limit of 3
        var queryStr = "{ a: orders { id } b: orders { id } c: orders { id } d: orders { id } }";
        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl(queryStr)], caller);

        result.ValidationReport.IsValid.Should().BeFalse();
        result.ValidationReport.Diagnostics.Should().Contain(d => d.Code == QueryDiagnosticCodes.QueryTooComplex);
    }

    [Fact]
    public async Task MaskedColumnInOrderBy_EmitsMaskedColumnInFilterDiagnostic()
    {
        var maskingRules = new Dictionary<string, MaskingRule>
        {
            ["ssn"] = new MaskingRule { RuleType = "REDACT" }
        };
        var ordersTable = CreateTable("orders", maskingRules, ("id", "varchar", false), ("ssn", "varchar", true));

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([ordersTable]);

        var caller = CreatePrincipal("user-1");

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([new Consent
            {
                TableIdentifier = ordersTable.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeSid = new Sid("user-1"),
                TenantId = caller.GetTenantId(),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(5)
            }]);

        var service = new QueryAnalysisService(metadataRepo, consentRepo);

        var queryStr = "query { orders(orderBy: { ssn: ASC }) { id } }";
        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl(queryStr)], caller);

        result.ValidationReport.Diagnostics.Should().Contain(d =>
            d.Code == QueryDiagnosticCodes.MaskedColumnInFilter &&
            string.Equals(d.Column, "ssn", StringComparison.OrdinalIgnoreCase));

        result.AccessRequirements.MissingConsents.Should().Contain(c =>
            string.Equals(c.Column, "ssn", StringComparison.OrdinalIgnoreCase) &&
            c.RequestedLevel == "READ_CLEARTEXT");
    }

    [Fact]
    public async Task FallbackBehavior_WhenEngineIsNull_PreservesExistingBehavior()
    {
        var ordersTable = CreateTable("orders", null, ("id", "varchar", false));
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([ordersTable]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([new Consent { TableIdentifier = ordersTable.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1") }]);

        // costAnalysisEngine is omitted / null
        var service = new QueryAnalysisService(metadataRepo, consentRepo);
        var caller = CreatePrincipal("user-1");

        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl("{ orders { id } }")], caller);

        result.ValidationReport.CostEstimate.EstimatedCost.Should().BeGreaterThan(0);
        result.ValidationReport.IsValid.Should().BeTrue();
    }
}
