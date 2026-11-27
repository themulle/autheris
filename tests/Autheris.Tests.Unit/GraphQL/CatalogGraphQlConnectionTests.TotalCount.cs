namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using HotChocolate;
using HotChocolate.Execution;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed partial class CatalogGraphQlConnectionTests
{
    private const string ThreeRows = "[{\"id\":1,\"name\":\"a\"},{\"id\":2,\"name\":\"b\"},{\"id\":3,\"name\":\"c\"}]";

    private void StubPage(Action<TreeQueryNode>? capture = null, string rows = ThreeRows)
    {
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Do<TreeQueryNode>(n => capture?.Invoke(n)),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse(rows));
    }

    private void StubCount(long count) =>
        _treeService.CountAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(count);

    private async Task<(string Raw, JsonElement? TotalCount)> RunAsync(string connectionArgs, string selection)
    {
        var executor = await CreateExecutorAsync();
        var result = await executor.ExecuteAsync($"query {{ sales_dbo_customersConnection({connectionArgs}) {{ {selection} }} }}");
        var raw = result.ToJson();
        using var doc = JsonDocument.Parse(raw);
        JsonElement? total = null;
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.GetProperty("sales_dbo_customersConnection").ValueKind == JsonValueKind.Object
            && data.GetProperty("sales_dbo_customersConnection").TryGetProperty("totalCount", out var t))
        {
            total = t.Clone();
        }

        return (raw, total);
    }

    [Fact]
    public async Task TotalCount_Selected_ReturnsServiceCount_NotPageSize()
    {
        StubPage();
        StubCount(250);

        var (raw, total) = await RunAsync("first: 2", "edges { node { id } } totalCount");

        raw.ShouldNotContain("\"errors\":");
        total!.Value.GetInt32().ShouldBe(250);
    }

    [Fact]
    public async Task TotalCount_NotSelected_DoesNotCallCountAsync()
    {
        StubPage();

        var (raw, _) = await RunAsync("first: 2", "edges { node { id } }");

        raw.ShouldNotContain("\"errors\":");
        await _treeService.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TotalCount_SecondPage_CountNodeHasWhereWithoutSeekAndNoRelations()
    {
        StubPage(rows: "[{\"id\":1,\"name\":\"a\"},{\"id\":2,\"name\":\"b\"},{\"id\":3,\"name\":\"c\"}]");
        StubCount(7);
        var executor = await CreateExecutorAsync();
        var first = await executor.ExecuteAsync("query { sales_dbo_customersConnection(first: 2, where: { status: { eq: \"open\" } }) { pageInfo { endCursor } } }");
        using var firstDoc = JsonDocument.Parse(first.ToJson());
        var cursor = firstDoc.RootElement.GetProperty("data").GetProperty("sales_dbo_customersConnection").GetProperty("pageInfo").GetProperty("endCursor").GetString();

        TreeQueryNode? pageNode = null;
        TreeQueryNode? countNode = null;
        StubPage(n => pageNode = n, "[{\"id\":3,\"name\":\"c\"}]");
        _treeService.CountAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<TreeQueryNode>(n => countNode = n), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(7L);

        var result = await executor.ExecuteAsync($"query {{ sales_dbo_customersConnection(first: 2, after: \"{cursor}\", where: {{ status: {{ eq: \"open\" }} }}) {{ totalCount }} }}");

        result.ToJson().ShouldNotContain("\"errors\":");
        pageNode!.Where.ShouldBeOfType<TreeAndFilter>(); // where AND seek predicate
        countNode.ShouldNotBeNull();
        countNode.Where.ShouldBeOfType<TreeComparison>().Column.ShouldBe("status");
        countNode.Relations.ShouldBeEmpty();
        countNode.OrderBy.ShouldBeEmpty();
    }

    [Fact]
    public async Task TotalCount_AliasAndFragment_CountsOnce()
    {
        StubPage();
        StubCount(9);

        var (raw, _) = await RunAsync("first: 2", "a: totalCount ... on sales_dbo_customersConnection { b: totalCount }");

        raw.ShouldNotContain("\"errors\":");
        await _treeService.ReceivedWithAnyArgs(1).CountAsync(default, default!, default, default!, default);
    }

    [Fact]
    public async Task TotalCount_SkipDirective_DoesNotCount()
    {
        StubPage();
        StubCount(9);

        var (raw, _) = await RunAsync("first: 2", "edges { node { id } } totalCount @skip(if: true)");

        raw.ShouldNotContain("\"errors\":");
        await _treeService.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TotalCount_PageDenied_NoCountCall_AccessDenied()
    {
        _treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<JsonDocument>>(_ => throw new GatewayForbiddenException("Access denied."));

        var (raw, _) = await RunAsync("first: 2", "totalCount");

        raw.ShouldContain("ACCESS_DENIED");
        await _treeService.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TotalCount_InvalidCursor_NoCountCall()
    {
        StubPage();

        var (raw, _) = await RunAsync("first: 2, after: \"invalid.cursor.signature\"", "totalCount");

        raw.ShouldContain("INVALID_QUERY");
        await _treeService.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TotalCount_FirstZero_NoCountCall()
    {
        StubPage(rows: "[]");
        StubCount(5);

        await RunAsync("first: 0", "totalCount");

        await _treeService.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default, default!, default);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("throttled")]
    [InlineData("forbidden")]
    [InlineData("generic")]
    [InlineData("timeout")]
    public async Task TotalCount_CountThrows_ReturnsNullWithoutErrors(string kind)
    {
        StubPage();
        _treeService.CountAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<long>>(_ => throw (kind switch
            {
                "throttled" => new GatewayThrottledException(2),
                "forbidden" => new GatewayForbiddenException("Access denied."),
                "timeout" => new OperationCanceledException("db timeout"),
                _ => new InvalidOperationException("secret detail")
            }));

        var (raw, total) = await RunAsync("first: 2", "edges { node { id } } totalCount");

        raw.ShouldNotContain("\"errors\":");
        raw.ShouldNotContain("secret detail");
        total!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task TotalCount_UsesSameOperationIdAsPage()
    {
        string? pageOp = null;
        string? countOp = null;
        _treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Do<string>(o => pageOp = o), Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse(ThreeRows));
        _treeService.CountAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Do<string>(o => countOp = o), Arg.Any<CancellationToken>())
            .Returns(3L);

        await RunAsync("first: 2", "totalCount");

        pageOp.ShouldNotBeNull();
        countOp.ShouldBe(pageOp);
    }
}
