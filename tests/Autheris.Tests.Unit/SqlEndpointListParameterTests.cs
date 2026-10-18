namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>REQ-SQL-128 WP-5: list parameters of declared SQL endpoints, POST bodies and GET query strings (A-8, SEC-128-07, SEC-128-14).</summary>
public sealed class SqlEndpointListParameterTests
{
    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u")], "Test"));
    private static readonly TenantId Tenant = new("tenant_1");

    private static (SqlEndpointExecutionService Service, Func<GovernedSqlQueryRequest?> Captured) Service(IOptions<GatewayOptions>? options = null, params SqlEndpointParameter[] parameters)
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("q", "q", "SELECT id FROM t WHERE id IN (@ids) AND note = @note", Parameters: parameters.ToList()));
        GovernedSqlQueryRequest? captured = null;
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(r => captured = r), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", [], [], 0, 0));
        return (new SqlEndpointExecutionService(registry, governed, options ?? Options.Create(new GatewayOptions())), () => captured);
    }

    private static SqlEndpointParameter ListOf(Type element, bool required = true, object? @default = null) =>
        new("ids", "@ids", element, IsRequired: required, DefaultValue: @default, IsList: true);

    private static ClientListValue Captured(GovernedSqlQueryRequest request, string key = "ids") => (ClientListValue)request.Parameters![key]!;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static object[] Elements(ClientListValue v) => Enumerable.Range(0, v.Count).Select(i => v[i]).ToArray();

    [Fact]
    public async Task Post_ArrayBody_IsAList_StoredUnderBothSpellings()
    {
        var (service, captured) = Service(null, ListOf(typeof(int)));
        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json("[3, 1, 3, 2]") }, User, Tenant);

        var request = captured()!;
        Elements(Captured(request)).ShouldBe(new object[] { 3, 1, 2 });
        request.Parameters!["@ids"].ShouldBeSameAs(request.Parameters["ids"]);
        request.Transport.ShouldBe(SqlTransports.SqlEndpoints);
    }

    [Fact]
    public async Task Get_RepeatedKey_AsClrList_IsAList_ElementsAreCoerced()
    {
        var (service, captured) = Service(null, ListOf(typeof(long)));
        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = new List<object?> { "10", "20" } }, User, Tenant);
        Elements(Captured(captured()!)).ShouldBe(new object[] { 10L, 20L });
    }

    [Fact]
    public async Task SingleValue_IsAOneElementList_AndACommaSeparatedValueIsOneString()
    {
        var (service, captured) = Service(null, ListOf(typeof(string)));
        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = "a,b,c" }, User, Tenant);
        Elements(Captured(captured()!)).ShouldBe(new object[] { "a,b,c" });

        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json("\"x\"") }, User, Tenant);
        Elements(Captured(captured()!)).ShouldBe(new object[] { "x" });

        var (intService, intCaptured) = Service(null, ListOf(typeof(int)));
        await intService.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json("7") }, User, Tenant);
        Elements(Captured(intCaptured()!)).ShouldBe(new object[] { 7 });
    }

    [Fact]
    public async Task GuidList_IsBoundAsCanonicalText()
    {
        var (service, captured) = Service(null, ListOf(typeof(Guid)));
        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json("[\"0F8FAD5B-D9CB-469F-A165-70867728950E\"]") }, User, Tenant);
        var list = Captured(captured()!);
        list.ElementType.ShouldBe(SqlParameterType.String);
        list[0].ShouldBe("0f8fad5b-d9cb-469f-a165-70867728950e");
    }

    [Fact]
    public async Task DateList_WithoutOffset_UsesUtc()
    {
        var (service, captured) = Service(null, ListOf(typeof(DateTimeOffset)));
        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json("[\"2026-03-04T05:06:07\"]") }, User, Tenant);
        ((DateTimeOffset)Captured(captured()!)[0]).Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Default_IsUsedWhenAbsent_AndMissingRequiredListIsRefused()
    {
        var (service, captured) = Service(null, ListOf(typeof(string), required: false, @default: new List<object?> { "a", "b" }));
        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?>(), User, Tenant);
        Elements(Captured(captured()!)).ShouldBe(new object[] { "a", "b" });

        var (emptyDefault, emptyCaptured) = Service(null, ListOf(typeof(string), required: false, @default: new List<object?>()));
        await emptyDefault.ExecuteEndpointAsync("q", new Dictionary<string, object?>(), User, Tenant);
        Captured(emptyCaptured()!).Count.ShouldBe(0);

        var (required, _) = Service(null, ListOf(typeof(string)));
        await Should.ThrowAsync<ArgumentException>(() => required.ExecuteEndpointAsync("q", new Dictionary<string, object?>(), User, Tenant));
    }

    [Theory]
    [InlineData("[1, \"a\"]", "SQL_LIST_PARAM_TYPE")]
    [InlineData("[1, null]", "SQL_LIST_PARAM_NULL")]
    [InlineData("[[1]]", "SQL_LIST_PARAM_TYPE")]
    [Trait("Category", "Security")]
    public async Task InvalidElements_AreRefused_WithAStableCode(string json, string code)
    {
        var (service, _) = Service(null, ListOf(typeof(int)));
        var ex = await Should.ThrowAsync<ClientListException>(() =>
            service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json(json) }, User, Tenant));
        ex.Code.ShouldBe(code);
    }

    [Fact]
    public async Task ListAboveTheConfiguredMaximum_IsRefused()
    {
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { MaxListParameterValues = 3 } });
        var (service, _) = Service(options, ListOf(typeof(int)));
        var ex = await Should.ThrowAsync<ClientListException>(() =>
            service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json("[1,2,3,4]") }, User, Tenant));
        ex.Code.ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    // SEC-128-07 / M-7
    [Fact]
    [Trait("Category", "Security")]
    public async Task ElementCoercionError_ContainsNoValue()
    {
        var (service, _) = Service(null, ListOf(typeof(int)));
        var ex = await Should.ThrowAsync<ClientListException>(() =>
            service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["ids"] = Json("[\"super-secret\"]") }, User, Tenant));
        ex.Message.ShouldNotContain("secret");
        ex.ToString().ShouldNotContain("secret");
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[\"a\",\"b\"]")]
    [Trait("Category", "Security")]
    public async Task ScalarParameter_GivenAnArray_IsRefused_WithoutTheValue(string json)
    {
        var (service, _) = Service(null, new SqlEndpointParameter("note", "@note", typeof(string)), ListOf(typeof(int), required: false));
        var ex = await Should.ThrowAsync<ClientListException>(() =>
            service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["note"] = Json(json) }, User, Tenant));
        ex.Code.ShouldBe("SQL_LIST_PARAM_TYPE");
        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage("SQL_LIST_PARAM_TYPE"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ScalarParameter_GivenARepeatedKeyList_IsRefused()
    {
        var (service, _) = Service(null, new SqlEndpointParameter("note", "@note", typeof(string)), ListOf(typeof(int), required: false));
        await Should.ThrowAsync<ClientListException>(() =>
            service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["note"] = new List<object?> { "x", "y" } }, User, Tenant));
    }

    [Fact]
    public async Task ExistingScalarCoercion_IsUnchanged()
    {
        var (service, captured) = Service(null, new SqlEndpointParameter("note", "@note", typeof(int)), ListOf(typeof(int), required: false));
        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?> { ["note"] = "5" }, User, Tenant);
        captured()!.Parameters!["note"].ShouldBe(5);
    }

    // ---- routes ------------------------------------------------------------------------------------------------------------------

    private static (HttpContext Context, ISqlEndpointExecutionService Execution, Func<IReadOnlyDictionary<string, object?>?> Inputs) RouteContext(string queryString, params SqlEndpointParameter[] parameters)
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("customers", "c", "SELECT id FROM t WHERE id IN (@ids)", Parameters: parameters.ToList()));
        var services = new ServiceCollection().AddSingleton<ISqlEndpointRegistry>(registry).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1"), new Claim("tenant_id", "tenant-1")], "Test"));
        var execution = Substitute.For<ISqlEndpointExecutionService>();
        IReadOnlyDictionary<string, object?>? inputs = null;
        execution.ExecuteEndpointAsync(Arg.Any<string>(), Arg.Do<IReadOnlyDictionary<string, object?>?>(i => inputs = i), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", ["id"], [], 0, 0));
        return (context, execution, () => inputs);
    }

    [Fact]
    public async Task Rest_Get_RepeatedKey_OfAListParameter_IsAList()
    {
        var (context, execution, inputs) = RouteContext("?ids=a&ids=b&ids=c", ListOf(typeof(string)));
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(200);
        inputs()!["ids"].ShouldBeAssignableTo<IReadOnlyList<object?>>().ShouldBe(new object?[] { "a", "b", "c" });
    }

    [Fact]
    public async Task Rest_Get_CommaSeparatedValue_IsOneString()
    {
        var (context, execution, inputs) = RouteContext("?ids=a,b,c", ListOf(typeof(string)));
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);
        inputs()!["ids"].ShouldBe("a,b,c");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Rest_Get_ReservedKeyRepeated_Unchanged()
    {
        // Keys that name no list parameter keep the joined StringValues text (format, paging): nothing becomes a list.
        var (context, execution, inputs) = RouteContext("?format=rows&format=json&other=1&other=2", ListOf(typeof(string), required: false));
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);
        inputs()!["format"].ShouldBe("rows,json");
        inputs()!["other"].ShouldBe("1,2");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Rest_Get_ScalarWithRepeatedKey_IsRefused_ErrorContainsNoValue()
    {
        var (context, execution, _) = RouteContext("?note=super-secret-1&note=super-secret-2", new SqlEndpointParameter("note", "@note", typeof(string)));
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(400);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("SQL_LIST_PARAM_TYPE");
        body.ShouldNotContain("secret");
        await execution.DidNotReceiveWithAnyArgs().ExecuteEndpointAsync(default!, default, default!, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public async Task Rest_Get_RepeatedKey_WithoutAnEndpointDefinition_IsRefused_NeverJoinedIntoAList(bool registryRegistered)
    {
        var (context, execution, _) = RouteContext("?ids=a&ids=b", ListOf(typeof(string)));
        if (!registryRegistered)
        {
            context.RequestServices = new ServiceCollection().BuildServiceProvider();
        }
        else
        {
            // a registry that does not know the endpoint
            context.RequestServices = new ServiceCollection().AddSingleton<ISqlEndpointRegistry>(new InMemorySqlEndpointRegistry()).BuildServiceProvider();
        }

        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(500);
        await execution.DidNotReceiveWithAnyArgs().ExecuteEndpointAsync(default!, default, default!, default, default);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldNotContain("a,b");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Rest_ListError_IsMappedWithAStableCode_AndFixedMessage()
    {
        var (context, execution, _) = RouteContext("");
        execution.ExecuteEndpointAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new ClientListException("SQL_LIST_PARAM_TOO_LARGE")));

        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(400);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("SQL_LIST_PARAM_TOO_LARGE");
        body.ShouldContain(GovernedSqlErrorCodes.FixedMessage("SQL_LIST_PARAM_TOO_LARGE"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Rest_Post_ListError_IsMappedWithAStableCode_InProductionToo()
    {
        var (context, execution, _) = RouteContext("");
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        context.RequestServices = new ServiceCollection().AddSingleton(env).BuildServiceProvider();
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"ids\":[1,2]}"));
        context.Request.ContentLength = 13;
        execution.ExecuteEndpointAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new ClientListException("SQL_LIST_PARAM_POSITION")));

        await SqlEndpointRoutes.HandlePostEndpoint("customers", context, execution, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(400);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldContain("SQL_LIST_PARAM_POSITION");
    }

    // AC-128-7
    [Fact]
    public void Listing_ShowsArrayWithElementTypeAndMaximum()
    {
        var def = new SqlEndpointDefinition("customers", "c", "SELECT 1", Parameters: [ListOf(typeof(int), required: false, @default: new List<object?> { 1, 2 })]);
        string json = JsonSerializer.Serialize(SqlEndpointRoutes.ToPublicListing([def]));
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement[0].GetProperty("parameters")[0];
        p.GetProperty("type").GetString().ShouldBe("Int32");
        p.GetProperty("isList").GetBoolean().ShouldBeTrue();
        p.GetProperty("maxItems").GetInt32().ShouldBe(1000);
    }

    [Fact]
    public async Task OpenApi_ShowsAnArraySchema_WithElementTypeAndMaximum()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("customers", "c", "SELECT id FROM t WHERE id IN (@ids)", Parameters: [ListOf(typeof(int)), new SqlEndpointParameter("note", "@note", typeof(string))]));
        var result = SqlEndpointRoutes.HandleOpenApiSpec(registry, Options.Create(new GatewayOptions()));
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        var parameters = doc.RootElement.GetProperty("paths").EnumerateObject().First().Value.EnumerateObject().First().Value.GetProperty("parameters");
        var ids = parameters.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "ids");
        var schema = ids.GetProperty("schema");
        schema.GetProperty("type").GetString().ShouldBe("array");
        schema.GetProperty("items").GetProperty("type").GetString().ShouldBe("integer");
        schema.GetProperty("maxItems").GetInt32().ShouldBe(1000);
        ids.GetProperty("style").GetString().ShouldBe("form");
        ids.GetProperty("explode").GetBoolean().ShouldBeTrue();
        var note = parameters.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "note");
        note.GetProperty("schema").GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task HotReload_InvalidHeader_KeepsPreviousDefinition()
    {
        string dir = Path.Combine(Path.GetTempPath(), "list-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "q.sql");
            await File.WriteAllTextAsync(file, "-- @name q\n-- @param ids: int[]\nSELECT id FROM t WHERE id IN (@ids)");
            var registry = new InMemorySqlEndpointRegistry();
            using var loader = new SqlEndpointLoader(registry);
            loader.LoadFromDirectory(dir, enableHotReload: false).ShouldBe(1);
            registry.TryGet("q", out var before).ShouldBeTrue();
            before!.Parameters.Single().IsList.ShouldBeTrue();

            await File.WriteAllTextAsync(file, "-- @name q\n-- @param ids: geography[]\nSELECT id FROM t WHERE id IN (@ids)");
            Should.Throw<FormatException>(() => loader.LoadFile(file));

            registry.TryGet("q", out var after).ShouldBeTrue();
            after!.Parameters.Single().IsList.ShouldBeTrue();   // the previous definition stays
            after.Parameters.Single().ClrType.ShouldBe(typeof(int));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadFromDirectory_InvalidHeader_DoesNotRegisterANewFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "list-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.sql"), "-- @name bad\n-- @param ids: int[ = [1]\nSELECT id FROM t WHERE id IN (@ids)");
            var registry = new InMemorySqlEndpointRegistry();
            using var loader = new SqlEndpointLoader(registry);
            loader.LoadFromDirectory(dir, enableHotReload: false).ShouldBe(0);
            registry.TryGet("bad", out _).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
