namespace Autheris.Tests.Unit.GraphQL;

using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Transport.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class GraphQlIndexHintExtensionTests
{
    private static readonly GraphQlIndexHint[] OneHint =
    [
        new("werte", GraphQlIndexHint.NoUsableIndexReason, ["gruppe"]),
    ];

    private static async Task<OperationResult> ExecuteAsync()
    {
        var executor = await new ServiceCollection()
            .AddGraphQL()
            .AddQueryType<ProbeQuery>()
            .BuildRequestExecutorAsync();
        return (OperationResult)await executor.ExecuteAsync("{ ping }");
    }

    private static string Format(OperationResult result)
    {
        var writer = new ArrayBufferWriter<byte>();
        JsonResultFormatter.Default.Format(result, writer);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    [Fact]
    public async Task IndexHints_AreSerializedAsObjectsWithColumnNames()
    {
        var result = await ExecuteAsync();
        CostAndQuotaMiddleware.AttachIndexHintsToResult(result, OneHint);

        using var doc = JsonDocument.Parse(Format(result));
        var hint = doc.RootElement.GetProperty("extensions").GetProperty("autheris").GetProperty("indexHints")[0];
        hint.ValueKind.ShouldBe(JsonValueKind.Object);
        hint.GetProperty("root").GetString().ShouldBe("werte");
        hint.GetProperty("reason").GetString().ShouldBe("NO_USABLE_INDEX");
        var cols = hint.GetProperty("helpfulIndexColumns");
        cols.GetArrayLength().ShouldBe(1);
        cols[0].GetString().ShouldBe("gruppe");
    }

    [Fact]
    public async Task IndexHints_ContainNoDotNetTypeNames()
    {
        var result = await ExecuteAsync();
        CostAndQuotaMiddleware.AttachIndexHintsToResult(result, OneHint);

        var json = Format(result);
        json.ShouldNotContain("System.");
        json.ShouldNotContain("GraphQlIndexHint");
    }

    [Fact]
    public async Task IndexHints_PreserveExistingAutherisExtensionKeys()
    {
        var result = await ExecuteAsync();
        result.Extensions = (result.Extensions
            ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty)
            .SetItem("autheris", new Dictionary<string, object?> { ["cost"] = 7 });
        CostAndQuotaMiddleware.AttachIndexHintsToResult(result, OneHint);

        using var doc = JsonDocument.Parse(Format(result));
        var autheris = doc.RootElement.GetProperty("extensions").GetProperty("autheris");
        autheris.GetProperty("cost").GetInt32().ShouldBe(7);
        autheris.TryGetProperty("indexHints", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task NoHints_AddNoIndexHintsKey()
    {
        var result = await ExecuteAsync();
        CostAndQuotaMiddleware.AttachIndexHintsToResult(result, []);

        using var doc = JsonDocument.Parse(Format(result));
        doc.RootElement.TryGetProperty("extensions", out _).ShouldBeFalse();
    }

    public sealed class ProbeQuery
    {
        public string Ping() => "pong";
    }
}
