namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
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

[Collection("CatalogRelation")]
[Trait("Category", "Security")]
public sealed class ConnectionCursorCallerBindingTests
{
    private const string SigningKey = "test-graphql-cursor-signing-key-0123456789";

    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();
    private readonly IGovernedTreeQueryService _treeService = Substitute.For<IGovernedTreeQueryService>();
    private readonly TableIdentifier _custTableId = new("sales", "dbo", "customers");

    public ConnectionCursorCallerBindingTests()
    {
        var table = CreateTableMetadata();
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([table]);
        _relationRepo.GetRelationsForTableAsync(_custTableId, Arg.Any<CancellationToken>()).Returns([]);
        _treeService.GetMaskedColumns(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<string>()).Returns([]);
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[{\"id\":1,\"name\":\"Alice\"},{\"id\":2,\"name\":\"Bob\"}]"));
    }

    private static TableMetadata CreateTableMetadata() => new()
    {
        Identifier = new TableIdentifier("sales", "dbo", "customers"),
        Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "nvarchar(100)" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    private async Task<IRequestExecutor> CreateExecutorAsync(HttpContext httpContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(_treeService);
        services.AddSingleton<IKeysetCursorEncoder>(new KeysetCursorEncoder(SigningKey));

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        services.AddSingleton<IHttpContextAccessor>(accessor);
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

    private static DefaultHttpContext ContextWithUser(params Claim[] claims)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return ctx;
    }

    [Fact]
    public async Task Cursor_bound_to_canonical_sid()
    {
        const string canonicalSid = "S-1-5-21-1001-2002-3003-4004";
        var ctx = ContextWithUser(
            new Claim(ClaimTypes.PrimarySid, canonicalSid),
            new Claim(ClaimTypes.NameIdentifier, "legacy-name-id"),
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("tenant_id", "T1"));
        var executor = await CreateExecutorAsync(ctx);

        var firstResult = await executor.ExecuteAsync(
            "query { sales_dbo_customersConnection(first: 1) { pageInfo { endCursor } edges { node { id } } } }");
        var firstJson = firstResult.ToJson();
        firstJson.ShouldNotContain("\"errors\":");

        using var doc = JsonDocument.Parse(firstJson);
        var cursor = doc.RootElement.GetProperty("data")
            .GetProperty("sales_dbo_customersConnection")
            .GetProperty("pageInfo")
            .GetProperty("endCursor")
            .GetString()!;

        // Replaying under a principal whose PrimarySid matches succeeds.
        var encoder = new KeysetCursorEncoder(SigningKey);
        var expectedContext = KeysetCursorContextFactory.ForGraphQl(
            tenantId: "T1",
            metadata: CreateTableMetadata(),
            keyColumns: ["id"],
            callerSid: canonicalSid,
            filter: null,
            select: ["id"],
            keyDirections: [KeyColumnDirection.Ascending]);
        var decoded = encoder.DecodeCursor(cursor, CreateTableMetadata(), expectedContext);
        decoded["id"].ShouldBe(1);

        // Replaying when only NameIdentifier matches legacy-name-id (not PrimarySid) fails closed.
        var otherCtx = ContextWithUser(
            new Claim(ClaimTypes.NameIdentifier, "legacy-name-id"),
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("tenant_id", "T1"));
        var otherExecutor = await CreateExecutorAsync(otherCtx);
        var replay = await otherExecutor.ExecuteAsync(
            $"query {{ sales_dbo_customersConnection(first: 1, after: \"{cursor}\") {{ edges {{ node {{ id }} }} }} }}");
        replay.ToJson().ShouldContain("\"errors\":");
    }

    [Fact]
    public async Task Cursor_of_other_user_with_same_display_name_rejected()
    {
        var alice1 = ContextWithUser(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-alice-1"),
            new Claim(ClaimTypes.Name, "Alice Smith"),
            new Claim("tenant_id", "T1"));
        var exec1 = await CreateExecutorAsync(alice1);

        var first = await exec1.ExecuteAsync(
            "query { sales_dbo_customersConnection(first: 1) { pageInfo { endCursor } edges { node { id } } } }");
        using var doc = JsonDocument.Parse(first.ToJson());
        var cursor = doc.RootElement.GetProperty("data")
            .GetProperty("sales_dbo_customersConnection")
            .GetProperty("pageInfo")
            .GetProperty("endCursor")
            .GetString()!;

        var alice2 = ContextWithUser(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-alice-2"),
            new Claim(ClaimTypes.Name, "Alice Smith"),
            new Claim("tenant_id", "T1"));
        var exec2 = await CreateExecutorAsync(alice2);

        var replay = await exec2.ExecuteAsync(
            $"query {{ sales_dbo_customersConnection(first: 1, after: \"{cursor}\") {{ edges {{ node {{ id }} }} }} }}");
        var json = replay.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("INVALID_QUERY");
    }

    [Fact]
    public async Task Principal_without_sid_is_denied()
    {
        var noSid = ContextWithUser(
            new Claim(ClaimTypes.Name, "alice-display-only"),
            new Claim("tenant_id", "T1"));
        var executor = await CreateExecutorAsync(noSid);

        var result = await executor.ExecuteAsync(
            "query { sales_dbo_customersConnection(first: 1) { edges { node { id } } } }");
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("UNAUTHORIZED");
    }

    [Fact]
    public async Task Empty_sid_is_denied()
    {
        var emptySid = ContextWithUser(
            new Claim(ClaimTypes.PrimarySid, "   "),
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("tenant_id", "T1"));
        var executor = await CreateExecutorAsync(emptySid);

        var result = await executor.ExecuteAsync(
            "query { sales_dbo_customersConnection(first: 1) { edges { node { id } } } }");
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("UNAUTHORIZED");
    }

    [Fact]
    public async Task Context_data_tenant_is_ignored()
    {
        var noTenantCtx = ContextWithUser(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-user-1"),
            new Claim(ClaimTypes.Name, "alice"));
        var executor = await CreateExecutorAsync(noTenantCtx);

        var request = OperationRequestBuilder.New()
            .SetDocument("query { sales_dbo_customersConnection(first: 1) { edges { node { id } } } }")
            .AddGlobalState("TenantId", "T1")
            .Build();

        var result = await executor.ExecuteAsync(request);
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldNotContain("\"sales_dbo_customersConnection\":{\"edges\"");
    }

    [Fact]
    public async Task Tenant_claim_mismatch_with_request_tenant_denied()
    {
        var mismatchedCtx = ContextWithUser(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-user-1"),
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("tenant_id", "T1"));
        mismatchedCtx.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("T2");
        var executor = await CreateExecutorAsync(mismatchedCtx);

        var result = await executor.ExecuteAsync(
            "query { sales_dbo_customersConnection(first: 1) { edges { node { id } } } }");
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("FORBIDDEN");
    }
}
