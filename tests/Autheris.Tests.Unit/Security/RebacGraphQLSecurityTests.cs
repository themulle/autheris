namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Directives;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class RebacGraphQLSecurityTests
{
    public class RebacQuery
    {
        public string GetDocument(string id) => $"DocumentContent_{id}";
    }

    public class RebacQueryType : ObjectType<RebacQuery>
    {
        protected override void Configure(IObjectTypeDescriptor<RebacQuery> descriptor)
        {
            descriptor.Field(f => f.GetDocument(default!))
                .Name("getDocument")
                .Directive(new RebacDirective
                {
                    Relation = "viewer",
                    ObjectType = "document",
                    ObjectArg = "id"
                });
        }
    }

    private static async Task<IRequestExecutor> CreateExecutorAsync(
        IRebacStore store,
        IRebacEvaluator evaluator,
        HttpContext httpContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(evaluator);
        services.AddScoped<IRebacBatchDataLoader, RebacBatchDataLoader>();
        services.AddSingleton<IHttpContextAccessor>(new DefaultHttpContextAccessor { HttpContext = httpContext });
        services.AddLogging();

        return await services
            .AddGraphQLServer()
            .AddDirectiveType<RebacDirectiveType>()
            .AddQueryType<RebacQueryType>()
            .BuildRequestExecutorAsync();
    }

    private static HttpContext CreateHttpContext(string user, string tenant, bool authenticated = true)
    {
        var context = new DefaultHttpContext();
        if (authenticated)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user),
                new(ClaimTypes.Name, user),
                new("tenant_id", tenant)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            context.User = new ClaimsPrincipal(identity);
        }
        else
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        return context;
    }

    private sealed class DefaultHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    [Fact]
    public async Task Rebac_DirectRelationPermitted_ReturnsFieldData()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        await store.AddTupleAsync(new RebacTuple("tenant-acme", "user:alice", "viewer", "document:doc-101"));

        var httpContext = CreateHttpContext("user:alice", "tenant-acme");
        var executor = await CreateExecutorAsync(store, evaluator, httpContext);

        // Act
        var result = await executor.ExecuteAsync("{ getDocument(id: \"doc-101\") }");
        var opResult = (OperationResult)result;

        // Assert
        (opResult.Errors == null || opResult.Errors.Count == 0).ShouldBeTrue();
        var json = result.ToJson();
        json.ShouldContain("DocumentContent_doc-101");
    }

    [Fact]
    public async Task Rebac_MissingRelation_ReturnsAuthNotAuthorized()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        // Bob has no relation on doc-101
        var httpContext = CreateHttpContext("user:bob", "tenant-acme");
        var executor = await CreateExecutorAsync(store, evaluator, httpContext);

        // Act
        var result = await executor.ExecuteAsync("{ getDocument(id: \"doc-101\") }");
        var opResult = (OperationResult)result;

        // Assert
        opResult.Errors.ShouldNotBeNull();
        opResult.Errors.Count.ShouldBeGreaterThan(0);
        opResult.Errors[0].Code.ShouldBe("AUTH_NOT_AUTHORIZED");
        opResult.Errors[0].Message.ShouldContain("Access denied");
    }

    [Fact]
    public async Task Rebac_TransitiveInheritancePermitted_ReturnsFieldData()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        // User alice is member of group:finance
        await store.AddTupleAsync(new RebacTuple("tenant-corp", "user:alice", "member", "group:finance"));
        // group:finance#member is viewer on folder:reports
        await store.AddTupleAsync(new RebacTuple("tenant-corp", "group:finance#member", "viewer", "folder:reports"));
        // folder:reports is parent of document:doc-202
        await store.AddTupleAsync(new RebacTuple("tenant-corp", "folder:reports", "parent", "document:doc-202"));

        var httpContext = CreateHttpContext("user:alice", "tenant-corp");
        var executor = await CreateExecutorAsync(store, evaluator, httpContext);

        // Act
        var result = await executor.ExecuteAsync("{ getDocument(id: \"doc-202\") }");
        var opResult = (OperationResult)result;

        // Assert
        (opResult.Errors == null || opResult.Errors.Count == 0).ShouldBeTrue();
        var json = result.ToJson();
        json.ShouldContain("DocumentContent_doc-202");
    }

    [Fact]
    public async Task Rebac_CrossTenantAttackBlocked_ReturnsAuthNotAuthorized()
    {
        // Arrange: Alice has viewer on doc-303 in tenant-A
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        await store.AddTupleAsync(new RebacTuple("tenant-a", "user:alice", "viewer", "document:doc-303"));

        // Alice attempts to execute query in context of tenant-b
        var httpContext = CreateHttpContext("user:alice", "tenant-b");
        var executor = await CreateExecutorAsync(store, evaluator, httpContext);

        // Act
        var result = await executor.ExecuteAsync("{ getDocument(id: \"doc-303\") }");
        var opResult = (OperationResult)result;

        // Assert: Must be denied (Anti-IDOR)
        opResult.Errors.ShouldNotBeNull();
        opResult.Errors.Count.ShouldBeGreaterThan(0);
        opResult.Errors[0].Code.ShouldBe("AUTH_NOT_AUTHORIZED");
    }

    [Fact]
    public async Task Rebac_UnauthenticatedCaller_ReturnsAuthNotAuthenticated()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        var httpContext = CreateHttpContext("anonymous", "tenant-acme", authenticated: false);
        var executor = await CreateExecutorAsync(store, evaluator, httpContext);

        // Act
        var result = await executor.ExecuteAsync("{ getDocument(id: \"doc-101\") }");
        var opResult = (OperationResult)result;

        // Assert
        opResult.Errors.ShouldNotBeNull();
        opResult.Errors.Count.ShouldBeGreaterThan(0);
        opResult.Errors[0].Code.ShouldBe("AUTH_NOT_AUTHENTICATED");
    }

    [Fact]
    public async Task Rebac_MissingTargetIdArgument_ReturnsAuthNotAuthorized()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        var httpContext = CreateHttpContext("user:alice", "tenant-acme");
        var executor = await CreateExecutorAsync(store, evaluator, httpContext);

        // Act: Pass empty string ID
        var result = await executor.ExecuteAsync("{ getDocument(id: \"\") }");
        var opResult = (OperationResult)result;

        // Assert: Fail-closed
        opResult.Errors.ShouldNotBeNull();
        opResult.Errors.Count.ShouldBeGreaterThan(0);
        opResult.Errors[0].Code.ShouldBe("AUTH_NOT_AUTHORIZED");
    }
}
