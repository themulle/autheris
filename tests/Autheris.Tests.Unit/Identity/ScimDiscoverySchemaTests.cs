namespace Autheris.Tests.Unit.Identity;

using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimDiscoverySchemaTests
{
    private static DefaultHttpContext NewContext()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body);
    }

    [Fact]
    public async Task GetSchemas_IncludesAutherisUserAndGroupExtensions()
    {
        var ctx = NewContext();
        using var doc = await ExecuteJsonAsync(ScimDiscoveryEndpoints.GetSchemas(), ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var resources = doc.RootElement.GetProperty("resources").EnumerateArray().ToList();
        var ids = resources.Select(r => r.GetProperty("id").GetString()).ToList();

        ids.ShouldContain(ScimSchemaUris.CoreUser);
        ids.ShouldContain(ScimSchemaUris.CoreGroup);
        ids.ShouldContain(ScimSchemaUris.AutherisUserExtension);
        ids.ShouldContain(ScimSchemaUris.AutherisGroupExtension);

        var groupExt = resources.Single(r => r.GetProperty("id").GetString() == ScimSchemaUris.AutherisGroupExtension);
        var groupSidAttr = groupExt.GetProperty("attributes").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "objectSid");
        groupSidAttr.GetProperty("required").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetResourceTypes_MarksGroupExtensionRequiredAndUserExtensionOptional()
    {
        var ctx = NewContext();
        using var doc = await ExecuteJsonAsync(ScimDiscoveryEndpoints.GetResourceTypes("workforce-entra"), ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var resources = doc.RootElement.GetProperty("resources").EnumerateArray().ToList();

        var userType = resources.Single(r => r.GetProperty("id").GetString() == "User");
        var userExt = userType.GetProperty("schemaExtensions").EnumerateArray()
            .Single(e => e.GetProperty("schema").GetString() == ScimSchemaUris.AutherisUserExtension);
        userExt.GetProperty("required").GetBoolean().ShouldBeFalse();

        var groupType = resources.Single(r => r.GetProperty("id").GetString() == "Group");
        var groupExt = groupType.GetProperty("schemaExtensions").EnumerateArray()
            .Single(e => e.GetProperty("schema").GetString() == ScimSchemaUris.AutherisGroupExtension);
        groupExt.GetProperty("required").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ScimErrorResult_Conflict409_And_BadRequest400_ProduceRfc7644ErrorShape()
    {
        var conflictCtx = NewContext();
        using var conflictDoc = await ExecuteJsonAsync(ScimErrorResult.UniquenessConflict("objectSid"), conflictCtx);
        conflictCtx.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        conflictDoc.RootElement.GetProperty("scimType").GetString().ShouldBe("uniqueness");
        conflictDoc.RootElement.GetProperty("status").GetString().ShouldBe("409");

        var badCtx = NewContext();
        using var badDoc = await ExecuteJsonAsync(ScimErrorResult.InvalidValue(), badCtx);
        badCtx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        badDoc.RootElement.GetProperty("scimType").GetString().ShouldBe("invalidValue");
        badDoc.RootElement.GetProperty("status").GetString().ShouldBe("400");
    }
}
