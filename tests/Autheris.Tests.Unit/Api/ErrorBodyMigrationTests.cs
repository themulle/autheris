namespace Autheris.Tests.Unit.Api;

using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Procedures.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>O-5b step 2, WP-1/WP-2 (NL-SC-20/21, NL-T-20/21): migrated bodies keep status and code and never echo exception text or caller input.</summary>
[Trait("Category", "Security")]
public sealed class ErrorBodyMigrationTests
{
    private const string Canary = "CANARY-9f3a-secret-id";

    [Theory]
    [InlineData("unknown_tool_" + Canary, "NOT_FOUND")]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_McpDocTools_UnknownTool(string tool, string code)
    {
        var json = await McpDocTools.ExecuteDocToolAsync(tool, "{}", null, null);

        AiDataGuardrailService.TryGetExecutorError(json, out var actual).ShouldBeTrue();
        actual.ShouldBe(code);
        json.ShouldNotContain(Canary);
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_McpDocTools_NotConfigured()
    {
        var json = await McpDocTools.ExecuteDocToolAsync(McpDocTools.SearchDocs, "{}", null, null);
        AiDataGuardrailService.TryGetExecutorError(json, out var code).ShouldBeTrue();
        code.ShouldBe("NOT_AVAILABLE");
    }

    [Fact]
    public async Task Migrated_Error_Bodies_Do_Not_Echo_Exception_Or_Input_McpDocTools_InvalidJson()
    {
        var json = await McpDocTools.ExecuteDocToolAsync(McpDocTools.DepositDocumentation, "{ " + Canary, Substitute.For<Autheris.Application.Docs.Interfaces.IDocSearchService>(), Substitute.For<Autheris.Application.Docs.Interfaces.IUniversalDocStore>(),
            new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER"));

        json.ShouldNotContain(Canary);
        AiDataGuardrailService.TryGetExecutorError(json, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_Procedure_BodyTooLarge()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Request.ContentLength = 10L * 1024 * 1024 * 1024;

        await ProcedureEndpointRoutes.HandlePost("x", http, Substitute.For<IProcedureExecutionService>(), NullLoggerFactory.Instance);

        http.Response.StatusCode.ShouldBe(413);
        http.Response.ContentType.ShouldStartWith("application/problem+json");
        http.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(new StreamReader(http.Response.Body).ReadToEnd());
        doc.RootElement.GetProperty("code").GetString().ShouldBe("PAYLOAD_TOO_LARGE");
        doc.RootElement.TryGetProperty("error", out _).ShouldBeFalse();
    }
}
