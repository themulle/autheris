namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

public class DbtHealthExecutionMiddlewareTests
{
    public sealed class DummyQuery
    {
        public string Orders() => "ok";
    }

    private static async Task<IRequestExecutor> CreateExecutorAsync(
        IDbtHealthCircuitBreaker circuitBreaker,
        ITableMetadataRepository tableRepo,
        ITableAccessResolver? accessResolver = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(circuitBreaker)
            .AddSingleton(tableRepo);

        if (accessResolver != null)
        {
            services.AddSingleton(accessResolver);
        }

        return await services
            .AddGraphQLServer()
            .AddQueryType<DummyQuery>()
            .UseRequest<DbtHealthExecutionMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();
    }

    [Fact]
    public async Task UnauthenticatedCaller_DoesNotDiscloseQuarantineStatus()
    {
        // Arrange
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var accessResolver = Substitute.For<ITableAccessResolver>();

        var tableId = new TableIdentifier("default", "default", "orders");
        circuitBreaker.GetTableHealthAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new DbtHealthState(tableId, DbtModelHealthStatus.Quarantined, Array.Empty<DbtTestFailure>(), DateTimeOffset.UtcNow));

        var executor = await CreateExecutorAsync(circuitBreaker, tableRepo, accessResolver);

        // Act: Execute without ClaimsPrincipal (unauthenticated)
        var request = OperationRequestBuilder.New()
            .SetDocument("{ orders }")
            .Build();

        await using var result = await executor.ExecuteAsync(request);
        var json = result.ToJson();

        // Assert: unauthenticated callers must NOT see TABLE_IN_QUARANTINE
        json.ShouldNotContain("TABLE_IN_QUARANTINE");
        json.ShouldNotContain("Quarantined");
    }

    [Fact]
    public async Task Authenticated_UnauthorizedCaller_DoesNotDiscloseQuarantineStatus()
    {
        // Arrange
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var accessResolver = Substitute.For<ITableAccessResolver>();

        var tableId = new TableIdentifier("finance", "dbo", "orders");
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TableMetadata { Identifier = tableId, Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "finance" } } });

        circuitBreaker.GetTableHealthAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new DbtHealthState(tableId, DbtModelHealthStatus.Quarantined, Array.Empty<DbtTestFailure>(), DateTimeOffset.UtcNow));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Alice") }, "TestAuth"));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                new TableMetadata { Identifier = tableId, Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "finance" } },
                TableAccessDecision.Denied(tableId, "Not authorized"),
                new TenantId("tenant-1"),
                new Sid("user-1"),
                principal));

        var executor = await CreateExecutorAsync(circuitBreaker, tableRepo, accessResolver);

        // Act: Execute with authenticated user
        var request = OperationRequestBuilder.New()
            .SetDocument("{ orders }")
            .SetGlobalState("ClaimsPrincipal", principal)
            .Build();

        await using var result = await executor.ExecuteAsync(request);
        var json = result.ToJson();

        // Assert: unauthorized callers must NOT see TABLE_IN_QUARANTINE
        json.ShouldNotContain("TABLE_IN_QUARANTINE");
        json.ShouldNotContain("Quarantined");
    }

    [Fact]
    public async Task Authenticated_AuthorizedCaller_DisclosesQuarantineStatus()
    {
        // Arrange
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var accessResolver = Substitute.For<ITableAccessResolver>();

        var tableId = new TableIdentifier("finance", "dbo", "orders");
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TableMetadata { Identifier = tableId, Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "finance" } } });

        circuitBreaker.GetTableHealthAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new DbtHealthState(tableId, DbtModelHealthStatus.Quarantined, Array.Empty<DbtTestFailure>(), DateTimeOffset.UtcNow));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Alice") }, "TestAuth"));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                new TableMetadata { Identifier = tableId, Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "finance" } },
                TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>(), null, true),
                new TenantId("tenant-1"),
                new Sid("user-1"),
                principal));

        var executor = await CreateExecutorAsync(circuitBreaker, tableRepo, accessResolver);

        // Act: Execute with authorized user
        var request = OperationRequestBuilder.New()
            .SetDocument("{ orders }")
            .SetGlobalState("ClaimsPrincipal", principal)
            .Build();

        await using var result = await executor.ExecuteAsync(request);
        var json = result.ToJson();

        // Assert: authorized callers SHOULD be blocked by TABLE_IN_QUARANTINE
        json.ShouldContain("TABLE_IN_QUARANTINE");
        json.ShouldContain("Quarantined");
    }

    [Fact]
    public async Task UnprivilegedCaller_WhenAccessResolverNull_DoesNotDiscloseQuarantineStatus()
    {
        // GQL-12: If access cannot be verified (e.g. accessResolver is null), do not disclose quarantine status to unprivileged caller
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "orders");
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TableMetadata { Identifier = tableId, Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "finance" } } });

        circuitBreaker.GetTableHealthAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new DbtHealthState(tableId, DbtModelHealthStatus.Quarantined, Array.Empty<DbtTestFailure>(), DateTimeOffset.UtcNow));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Alice") }, "TestAuth"));

        // Do not register accessResolver
        var executor = await CreateExecutorAsync(circuitBreaker, tableRepo, accessResolver: null);

        var request = OperationRequestBuilder.New()
            .SetDocument("{ orders }")
            .SetGlobalState("ClaimsPrincipal", principal)
            .Build();

        await using var result = await executor.ExecuteAsync(request);
        var json = result.ToJson();

        json.ShouldNotContain("TABLE_IN_QUARANTINE");
        json.ShouldNotContain("Quarantined");
    }

    private static async Task<string> ExecuteAuthorizedAsync(string document, DbtModelHealthStatus status)
    {
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var accessResolver = Substitute.For<ITableAccessResolver>();

        var tableId = new TableIdentifier("finance", "dbo", "orders");
        var meta = new TableMetadata { Identifier = tableId, Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "finance" } };
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new[] { meta });
        circuitBreaker.GetTableHealthAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new DbtHealthState(tableId, status, Array.Empty<DbtTestFailure>(), DateTimeOffset.UtcNow));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Alice") }, "TestAuth"));
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                meta,
                TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>(), null, true),
                new TenantId("tenant-1"),
                new Sid("user-1"),
                principal));

        var executor = await CreateExecutorAsync(circuitBreaker, tableRepo, accessResolver);
        var request = OperationRequestBuilder.New()
            .SetDocument(document)
            .SetGlobalState("ClaimsPrincipal", principal)
            .Build();

        await using var result = await executor.ExecuteAsync(request);
        return result.ToJson();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_InlineFragmentOnQuery_ReturnsTableInQuarantine()
    {
        var json = await ExecuteAuthorizedAsync("{ ... on Query { orders } }", DbtModelHealthStatus.Quarantined);
        json.ShouldContain("TABLE_IN_QUARANTINE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_NamedFragment_ReturnsTableInQuarantine()
    {
        var json = await ExecuteAuthorizedAsync("{ ...F } fragment F on Query { orders }", DbtModelHealthStatus.Quarantined);
        json.ShouldContain("TABLE_IN_QUARANTINE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_NestedNamedFragments_ReturnsTableInQuarantine()
    {
        var json = await ExecuteAuthorizedAsync(
            "{ ...A } fragment A on Query { ...B } fragment B on Query { ... on Query { orders } }",
            DbtModelHealthStatus.Quarantined);
        json.ShouldContain("TABLE_IN_QUARANTINE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_FragmentCycle_Terminates()
    {
        var json = await ExecuteAuthorizedAsync(
            "{ ...A } fragment A on Query { ...B orders } fragment B on Query { ...A }",
            DbtModelHealthStatus.Quarantined);
        json.ShouldContain("TABLE_IN_QUARANTINE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_HealthyModelViaFragment_PassesThrough()
    {
        var json = await ExecuteAuthorizedAsync("{ ...F } fragment F on Query { orders }", DbtModelHealthStatus.Healthy);
        json.ShouldNotContain("TABLE_IN_QUARANTINE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_FragmentOnUnknownType_StillGated()
    {
        var json = await ExecuteAuthorizedAsync("{ ...F } fragment F on NoSuchType { orders }", DbtModelHealthStatus.Quarantined);
        json.ShouldContain("TABLE_IN_QUARANTINE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_FragmentSpreadMissingDefinition_DoesNotThrow()
    {
        var json = await ExecuteAuthorizedAsync("{ ...Missing }", DbtModelHealthStatus.Quarantined);
        json.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gql14_FragmentSharedTwice_NotSkippedWrongly()
    {
        var json = await ExecuteAuthorizedAsync(
            "{ ...A ...B } fragment A on Query { ...C } fragment B on Query { ...C } fragment C on Query { orders }",
            DbtModelHealthStatus.Quarantined);
        json.ShouldContain("TABLE_IN_QUARANTINE");
    }
}
