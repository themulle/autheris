namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Analysis;
using Autheris.GraphQL.Interceptors;
using FluentAssertions;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class Issue175SecurityConditionsTests
{
    public sealed class User
    {
        public string Id { get; set; } = "1";
        public string Name { get; set; } = "Alice";
    }

    public sealed class NestedNode
    {
        public string Id { get; set; } = "1";
        public NestedNode? Next { get; set; }
    }

    public sealed class TestQuery
    {
        public List<User> Users => [new()];
        public NestedNode Node => new();
    }

    private static async Task<(IRequestExecutorProvider Provider, ISchemaDefinition Schema)> CreateTestExecutorProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer()
            .AddQueryType<TestQuery>();

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IRequestExecutorProvider>();
        var executor = await provider.GetExecutorAsync();
        return (provider, executor.Schema);
    }

    private static ClaimsPrincipal ReadOnlyPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePrincipal(string sid = "user-1", params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("oid", sid),
            new("sub", sid),
            new(ClaimTypes.NameIdentifier, sid),
            new("sid", sid)
        };
        foreach (var r in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));
            claims.Add(new Claim("roles", r));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static async Task<(bool Passed, int Status)> InvokeMiddlewareAsync(
        ClaimsPrincipal user,
        string method,
        string path,
        Action<IHeaderDictionary>? configureHeaders = null)
    {
        var passed = false;
        var middleware = new ReadOnlyTokenMiddleware(_ =>
        {
            passed = true;
            return Task.CompletedTask;
        }, "/graphql", "/mcp");

        var context = new DefaultHttpContext { User = user };
        context.Request.Method = method;
        context.Request.Path = path;
        configureHeaders?.Invoke(context.Request.Headers);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        return (passed, context.Response.StatusCode);
    }

    private static TableMetadata CreateTable(
        string name,
        DatasetVisibility visibility = DatasetVisibility.Full,
        params (string Name, string Type, bool IsNullable)[] columns)
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
                Visibility = visibility
            },
            Columns = columns.Select(c => new TableColumn
            {
                ColumnName = c.Name,
                DataType = c.Type,
                IsSensitive = false
            }).ToList(),
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(),
            PrimaryKeyColumns = ["id"]
        };
    }

    // -------------------------------------------------------------------------------------------------
    // SEC-GOV175-01: Fail-Closed Route Matching in ReadOnlyTokenMiddleware
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("POST", "/api/v1/governance/queries/analyze", true, StatusCodes.Status200OK)]
    [InlineData("POST", "/api/v1/governance/queries/analyze/", true, StatusCodes.Status200OK)]
    [InlineData("POST", "/api/v1/governance/queries/analyze/sub", false, StatusCodes.Status403Forbidden)]
    [InlineData("POST", "/api/v1/governance/queries/analyze/exec", false, StatusCodes.Status403Forbidden)]
    [InlineData("POST", "/api/v1/governance/consents/bundle", false, StatusCodes.Status403Forbidden)]
    [InlineData("POST", "/api/v1/governance/consents/apply", false, StatusCodes.Status403Forbidden)]
    [InlineData("POST", "/api/governance/consents/bundle", false, StatusCodes.Status403Forbidden)]
    [InlineData("POST", "/api/governance/plans/apply", false, StatusCodes.Status403Forbidden)]
    [InlineData("POST", "/api/governance/hitl/approve", false, StatusCodes.Status403Forbidden)]
    public async Task T_SEC_GOV175_01_ReadOnlyToken_RouteAuthorization(string method, string path, bool expectedPassed, int expectedStatusCode)
    {
        var (passed, status) = await InvokeMiddlewareAsync(ReadOnlyPrincipal(), method, path);

        passed.Should().Be(expectedPassed);
        if (!expectedPassed)
        {
            status.Should().Be(expectedStatusCode);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV175_01_ReadOnlyToken_ProbingHeaders_FailClosed()
    {
        // Attackers probing with GET while specifying non-safe original or forwarded verbs fail closed
        var (passedOrig, statusOrig) = await InvokeMiddlewareAsync(
            ReadOnlyPrincipal(),
            "GET",
            "/api/v1/governance/queries/analyze",
            h => h["X-Original-Method"] = "DELETE");

        passedOrig.Should().BeFalse();
        statusOrig.Should().Be(StatusCodes.Status403Forbidden);

        var (passedFwd, statusFwd) = await InvokeMiddlewareAsync(
            ReadOnlyPrincipal(),
            "GET",
            "/api/v1/governance/queries/analyze",
            h => h["X-Forwarded-Method"] = "POST");

        passedFwd.Should().BeFalse();
        statusFwd.Should().Be(StatusCodes.Status403Forbidden);
    }

    // -------------------------------------------------------------------------------------------------
    // SEC-GOV175-02: DoS & AST Amplification Defense in QueryCostAnalyzerRule & QueryAnalysisService
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV175_02_AST_Amplification_And_Cycles_HandledSafely()
    {
        var (provider, schema) = await CreateTestExecutorProviderAsync();
        var gatewayOptions = new GatewayOptions
        {
            GraphQL = new GraphQLOptions
            {
                MaxAllowedComplexity = 1000,
                MaxAllowedExecutionDepth = 10,
                MaxRootFieldsPerOperation = 5
            }
        };
        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider, Options.Create(gatewayOptions));

        var usersTable = CreateTable("users", DatasetVisibility.Full, ("id", "varchar", false), ("name", "varchar", false));
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([usersTable]);

        var caller = CreatePrincipal("user-1");
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([new Consent
            {
                TableIdentifier = usersTable.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeSid = new Sid("user-1"),
                TenantId = caller.GetTenantId(),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(5)
            }]);

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            gatewayOptions: Options.Create(gatewayOptions),
            costAnalysisEngine: engine);

        // 1. Cyclic fragment spread
        var cyclicQuery = @"
query CyclicTest {
    users {
        id
        ...FragA
    }
}
fragment FragA on User {
    name
    ...FragB
}
fragment FragB on User {
    id
    ...FragA
}";

        var cyclicDoc = Utf8GraphQLParser.Parse(cyclicQuery);
        var rule = new QueryCostAnalyzerRule(
            maxAllowedCost: 1000,
            maxExecutionDepth: 10,
            defaultListMultiplier: 10,
            maxResponseRows: 1000);

        // ComputeCostDetails does not hang or overflow stack
        var (cyclicCost, cyclicRootCosts) = rule.ComputeCostDetails(cyclicDoc, schema);
        cyclicCost.Should().BeGreaterThan(0);
        cyclicRootCosts.Should().ContainKey("users");

        // 2. Deep nested AST exceeding MaxAllowedExecutionDepth (depth 15 > 10)
        var deepQueryBuilder = new StringBuilder("query Deep { node { ");
        for (var i = 0; i < 15; i++)
        {
            deepQueryBuilder.Append("next { ");
        }
        deepQueryBuilder.Append("id ");
        for (var i = 0; i < 15; i++)
        {
            deepQueryBuilder.Append("} ");
        }
        deepQueryBuilder.Append("} }");

        var deepDoc = Utf8GraphQLParser.Parse(deepQueryBuilder.ToString());
        var (deepCost, _) = rule.ComputeCostDetails(deepDoc, schema);
        deepCost.Should().BeGreaterThan(0);

        // 3. Excessive root field count (6 > 5)
        var excessiveRootQuery = "query { a: users { id } b: users { id } c: users { id } d: users { id } e: users { id } f: users { id } }";
        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl(excessiveRootQuery)], caller);

        result.ValidationReport.IsValid.Should().BeFalse();
        result.ValidationReport.Diagnostics.Should().Contain(d =>
            d.Code == QueryDiagnosticCodes.QueryTooComplex &&
            d.Message.Contains("5 root fields"));
    }

    // -------------------------------------------------------------------------------------------------
    // SEC-GOV175-03: Zero Database Reads & No Quota Consumption
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV175_03_ZeroDatabaseReadsAndZeroQuota()
    {
        var (provider, _) = await CreateTestExecutorProviderAsync();
        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider);

        var usersTable = CreateTable("users", DatasetVisibility.Full, ("id", "varchar", false), ("name", "varchar", false));

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([usersTable]);

        var caller = CreatePrincipal("user-1");
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([new Consent
            {
                TableIdentifier = usersTable.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeSid = new Sid("user-1"),
                TenantId = caller.GetTenantId(),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(5)
            }]);

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            costAnalysisEngine: engine);

        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromGraphQl("{ users { id name } }")], caller);

        result.ValidationReport.IsValid.Should().BeTrue();
        result.ValidationReport.CostEstimate.EstimatedCost.Should().BeGreaterThan(0);
        result.SourceDatabaseReadsExecuted.Should().Be(0);

        // Verification: Only catalog metadata was loaded; zero data query executions or database connections occurred
        await metadataRepo.Received(1).GetAllTablesAsync(Arg.Any<CancellationToken>());
        await consentRepo.Received(1).GetAllActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------------------------------
    // SEC-GOV175-04: Zero Existence Oracles & Catalog Leakage Prevention
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV175_04_NoExistenceOracle_HiddenDatasets()
    {
        var (provider, _) = await CreateTestExecutorProviderAsync();
        var engine = new HotChocolateGraphQlCostAnalysisEngine(provider);

        var hiddenTable = CreateTable("secret_ledger", DatasetVisibility.Hidden, ("id", "varchar", false), ("balance", "numeric", false));
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([hiddenTable]);

        var consentRepo = Substitute.For<IConsentRepository>();
        // Unprivileged caller has no consent on hiddenTable
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var service = new QueryAnalysisService(metadataRepo, consentRepo, costAnalysisEngine: engine);
        var unprivilegedCaller = CreatePrincipal("unprivileged-user");

        // 1. Query non-existent dataset
        var nonExistentResult = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromGraphQl("{ non_existent_dataset { id } }")],
            unprivilegedCaller);

        // 2. Query hidden dataset
        var hiddenResult = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromGraphQl("{ secret_ledger { id balance } }")],
            unprivilegedCaller);

        // Indistinguishability & Zero Disclosure
        nonExistentResult.ValidationReport.Diagnostics.Should().ContainSingle(d => d.Code == QueryDiagnosticCodes.UnknownTable);
        nonExistentResult.AccessRequirements.MissingConsents.Should().BeEmpty();
        nonExistentResult.ValidationReport.OutputColumns.Should().BeEmpty();

        hiddenResult.ValidationReport.Diagnostics.Should().ContainSingle(d => d.Code == QueryDiagnosticCodes.UnknownTable);
        hiddenResult.AccessRequirements.MissingConsents.Should().BeEmpty();
        hiddenResult.ValidationReport.OutputColumns.Should().BeEmpty();

        // 3. Unauthorized TargetSubjectId impersonation fails closed before catalog inspection
        var targetPrincipal = CreatePrincipal("target-admin");
        var metadataRepoSpy = Substitute.For<ITableMetadataRepository>();

        var impersonationService = new QueryAnalysisService(metadataRepoSpy, consentRepo);

        var act = async () => await impersonationService.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromGraphQl("{ secret_ledger { id } }")],
            callerPrincipal: unprivilegedCaller,
            targetPrincipal: targetPrincipal);

        var exceptionAssertions = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        exceptionAssertions.Which.Message.Should().Contain("governance:simulate");

        // Metadata inspection was NEVER reached
        await metadataRepoSpy.DidNotReceiveWithAnyArgs().GetAllTablesAsync(Arg.Any<CancellationToken>());
    }
}
