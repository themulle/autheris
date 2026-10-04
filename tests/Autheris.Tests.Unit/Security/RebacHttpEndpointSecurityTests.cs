namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class RebacHttpEndpointSecurityTests
{
    private static IServiceProvider CreateServiceProvider(IRebacStore store, IRebacEvaluator evaluator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(evaluator);
        services.AddScoped<IRebacBatchDataLoader, RebacBatchDataLoader>();
        services.AddLogging();
        services.AddSingleton(Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } }));
        return services.BuildServiceProvider();
    }

    private static HttpContext CreateHttpContext(
        IServiceProvider sp,
        string? user,
        string tenant,
        Dictionary<string, object?>? routeValues = null,
        QueryCollection? query = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = sp
        };

        if (user != null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user),
                new("tenant_id", tenant)
            };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        if (routeValues != null)
        {
            foreach (var kvp in routeValues)
            {
                context.Request.RouteValues[kvp.Key] = kvp.Value;
            }
        }

        if (query != null)
        {
            context.Request.Query = query;
        }

        return context;
    }

    private sealed class TestEndpointFilterInvocationContext : EndpointFilterInvocationContext
    {
        public TestEndpointFilterInvocationContext(HttpContext httpContext, IList<object?> arguments)
        {
            HttpContext = httpContext;
            Arguments = arguments;
        }

        public override HttpContext HttpContext { get; }
        public override IList<object?> Arguments { get; }
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    [Fact]
    public async Task RequireRebac_Allowed_ProceedsToNextHandler()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);
        var sp = CreateServiceProvider(store, evaluator);

        await store.AddTupleAsync(new RebacTuple("tenant-acme", "user:alice", "reader", "dataset:dataset-42"));

        var httpContext = CreateHttpContext(
            sp,
            user: "user:alice",
            tenant: "tenant-acme",
            routeValues: new Dictionary<string, object?> { ["id"] = "dataset-42" });

        var filter = new RebacEndpointFilter("reader", "dataset", "id", RebacParameterSource.Route);
        var invocationContext = new TestEndpointFilterInvocationContext(httpContext, new List<object?> { "dataset-42" });

        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok(new { status = "success" }));
        };

        // Act
        var result = await filter.InvokeAsync(invocationContext, next);

        // Assert
        nextCalled.ShouldBeTrue();
        result.ShouldNotBeNull();
        result.GetType().Name.ShouldStartWith("Ok");
    }

    [Fact]
    public async Task RequireRebac_Denied_Returns403Forbidden()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);
        var sp = CreateServiceProvider(store, evaluator);

        // Bob has no relation on dataset-42
        var httpContext = CreateHttpContext(
            sp,
            user: "user:bob",
            tenant: "tenant-acme",
            routeValues: new Dictionary<string, object?> { ["id"] = "dataset-42" });

        var filter = new RebacEndpointFilter("reader", "dataset", "id", RebacParameterSource.Route);
        var invocationContext = new TestEndpointFilterInvocationContext(httpContext, new List<object?> { "dataset-42" });

        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        var result = await filter.InvokeAsync(invocationContext, next);

        // Assert
        nextCalled.ShouldBeFalse();
        result.ShouldNotBeNull();
        result.ShouldBeOfType<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result;
        problem.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        problem.ProblemDetails.Detail!.ShouldContain("Access denied");
    }

    [Fact]
    public async Task RequireRebac_Unauthenticated_Returns401Unauthorized()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);
        var sp = CreateServiceProvider(store, evaluator);

        var httpContext = CreateHttpContext(
            sp,
            user: null, // anonymous
            tenant: "tenant-acme",
            routeValues: new Dictionary<string, object?> { ["id"] = "dataset-42" });

        var filter = new RebacEndpointFilter("reader", "dataset", "id", RebacParameterSource.Route);
        var invocationContext = new TestEndpointFilterInvocationContext(httpContext, new List<object?> { "dataset-42" });

        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        var result = await filter.InvokeAsync(invocationContext, next);

        // Assert
        nextCalled.ShouldBeFalse();
        result.ShouldNotBeNull();
        result.ShouldBeOfType<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result;
        problem.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task RequireRebac_MissingRouteParameter_FailsClosedWith403()
    {
        // Arrange
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);
        var sp = CreateServiceProvider(store, evaluator);

        // Route values do not contain "id"
        var httpContext = CreateHttpContext(
            sp,
            user: "user:alice",
            tenant: "tenant-acme",
            routeValues: new Dictionary<string, object?>());

        var filter = new RebacEndpointFilter("reader", "dataset", "id", RebacParameterSource.Route);
        var invocationContext = new TestEndpointFilterInvocationContext(httpContext, new List<object?>());

        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        var result = await filter.InvokeAsync(invocationContext, next);

        // Assert: Fail-closed
        nextCalled.ShouldBeFalse();
        result.ShouldNotBeNull();
        result.ShouldBeOfType<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result;
        problem.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        problem.ProblemDetails.Detail!.ShouldContain("missing");
    }

    [Fact]
    public async Task RequireRebac_CrossTenantBoundary_Returns403()
    {
        // Arrange: Alice has reader on dataset:ds-77 in tenant-a
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true } });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);
        var sp = CreateServiceProvider(store, evaluator);

        await store.AddTupleAsync(new RebacTuple("tenant-a", "user:alice", "reader", "dataset:ds-77"));

        // Alice attempts to access dataset:ds-77 in tenant-b
        var httpContext = CreateHttpContext(
            sp,
            user: "user:alice",
            tenant: "tenant-b",
            routeValues: new Dictionary<string, object?> { ["id"] = "ds-77" });

        var filter = new RebacEndpointFilter("reader", "dataset", "id", RebacParameterSource.Route);
        var invocationContext = new TestEndpointFilterInvocationContext(httpContext, new List<object?> { "ds-77" });

        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        // Act
        var result = await filter.InvokeAsync(invocationContext, next);

        // Assert: Multi-tenant boundary enforced
        nextCalled.ShouldBeFalse();
        result.ShouldNotBeNull();
        result.ShouldBeOfType<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result;
        problem.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
