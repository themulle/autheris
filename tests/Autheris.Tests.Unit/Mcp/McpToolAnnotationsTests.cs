namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Mcp;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #706 (PLAN-OI-706): every tool in <c>tools/list</c> carries <c>readOnlyHint</c>; a tool that can change state never
/// reports itself as read-only, and an unclassified name is treated as state-changing and destructive.
/// </summary>
public sealed class McpToolAnnotationsTests
{
    private static readonly string[] OverwritingTools =
    [
        McpAdminTools.ApplyAccess, McpAdminTools.ApplyRowFilterEvaluation, McpAdminTools.SetDatasetState,
        McpAdminTools.SetDatasetCatalog, McpDocTools.DepositDocumentation,
    ];

    private static readonly string[] AddingTools =
    [
        McpAdminTools.PlanAccess, McpAdminTools.PlanRowFilterEvaluation, McpAdminTools.RegisterDatasource,
    ];

    private static readonly string[] WritesForWritableSession =
    [
        McpDatasetTools.QuerySql, McpDatasetTools.InvokeApi, McpDatasetTools.CallProcedure,
    ];

    private static McpSessionContext Session(bool readOnly, params string[] roles) =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: roles, IsReadOnly: readOnly);

    [Fact]
    [Trait("Category", "Security")]
    public void Every_built_in_tool_is_classified()
    {
        McpToolGroups.BuiltInNames.Where(n => !McpToolAnnotations.IsClassified(n)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Security")]
    public void A_state_changing_admin_or_documentation_tool_never_reports_read_only(bool readOnlySession, bool dmlAllowed)
    {
        foreach (var name in OverwritingTools.Concat(AddingTools))
        {
            McpToolAnnotations.For(name, readOnlySession, dmlAllowed).ReadOnly.ShouldBeFalse(name);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Overwriting_tools_are_destructive_and_adding_tools_are_not()
    {
        foreach (var name in OverwritingTools)
        {
            McpToolAnnotations.For(name, readOnlySession: false, sqlDmlAllowed: false).Destructive.ShouldBeTrue(name);
        }

        foreach (var name in AddingTools)
        {
            McpToolAnnotations.For(name, readOnlySession: false, sqlDmlAllowed: false).Destructive.ShouldBeFalse(name);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sql_and_procedure_tools_are_destructive_for_a_writable_session_with_dml_on()
    {
        foreach (var name in WritesForWritableSession)
        {
            var hints = McpToolAnnotations.For(name, readOnlySession: false, sqlDmlAllowed: true);
            hints.ReadOnly.ShouldBeFalse(name);
            hints.Destructive.ShouldBeTrue(name);
        }
    }

    [Fact]
    public void Sql_and_procedure_tools_are_read_only_for_a_read_only_session()
    {
        foreach (var name in WritesForWritableSession)
        {
            McpToolAnnotations.For(name, readOnlySession: true, sqlDmlAllowed: true).ReadOnly.ShouldBeTrue(name);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sql_tools_follow_the_dml_switch_but_call_procedure_does_not()
    {
        McpToolAnnotations.For(McpDatasetTools.QuerySql, readOnlySession: false, sqlDmlAllowed: false).ReadOnly.ShouldBeTrue();
        McpToolAnnotations.For(McpDatasetTools.InvokeApi, readOnlySession: false, sqlDmlAllowed: false).ReadOnly.ShouldBeTrue();
        // Write procedures do not depend on WebSql:AllowDml.
        McpToolAnnotations.For(McpDatasetTools.CallProcedure, readOnlySession: false, sqlDmlAllowed: false).ReadOnly.ShouldBeFalse();
    }

    [Theory]
    [InlineData("list_datasets")]
    [InlineData("describe_dataset")]
    [InlineData("search_catalog")]
    [InlineData("find_relevant_tables")]
    [InlineData("get_table_schema")]
    [InlineData("describe_my_access")]
    [InlineData("get_my_permissions")]
    [InlineData("query_graphql")]
    [InlineData("query_dataset")]
    [InlineData("sample_rows")]
    [InlineData("search_docs")]
    [InlineData("admin_resolve_principal")]
    [InlineData("DESCRIBE_DATASET")]
    public void Read_tools_report_read_only_for_every_session(string name)
    {
        McpToolAnnotations.For(name, readOnlySession: false, sqlDmlAllowed: true).ReadOnly.ShouldBeTrue();
        McpToolAnnotations.For(name, readOnlySession: true, sqlDmlAllowed: false).ReadOnly.ShouldBeTrue();
    }

    [Theory]
    [InlineData("some_dynamic_tool")]
    [InlineData("list_orders")]
    [InlineData("")]
    [InlineData("list_datasets\0")]
    [InlineData("request_access")]
    [Trait("Category", "Security")]
    public void An_unclassified_name_is_state_changing_and_destructive(string name)
    {
        McpToolAnnotations.IsClassified(name).ShouldBeFalse();
        var hints = McpToolAnnotations.For(name, readOnlySession: true, sqlDmlAllowed: false);
        hints.ReadOnly.ShouldBeFalse();
        hints.Destructive.ShouldBeTrue();
    }

    [Fact]
    public void Sdk_tool_carries_the_hints()
    {
        var read = GatewayMcpServer.ToTool(new McpToolDefinition(McpDatasetTools.ListDatasets, "d", "{}", "op"), readOnlySession: false, sqlDmlAllowed: false);
        read.Annotations.ShouldNotBeNull();
        read.Annotations!.ReadOnlyHint.ShouldBe(true);
        read.Annotations.DestructiveHint.ShouldBeNull();

        var write = GatewayMcpServer.ToTool(new McpToolDefinition(McpAdminTools.ApplyAccess, "d", "{}", "op"), readOnlySession: false, sqlDmlAllowed: false);
        write.Annotations!.ReadOnlyHint.ShouldBe(false);
        write.Annotations.DestructiveHint.ShouldBe(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public async Task Protocol_handler_tools_list_annotates_every_tool(bool readOnlySession)
    {
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { AllowedOperations = ["List Orders"] },
            WebSql = new WebSqlOptions { AllowDml = true },
        };
        var registry = new McpToolRegistry(Options.Create(options));
        var store = Substitute.For<IMcpSessionStore>();
        store.GetSessionAsync("s1", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpSessionContext?>(Session(readOnlySession, "ClusterAdmin")));
        var handler = new McpProtocolHandler(store, registry, Substitute.For<IAiDataGuardrailService>(),
            NullLogger<McpProtocolHandler>.Instance, options: Options.Create(options));

        var json = await handler.HandleMessageAsync("s1", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        using var doc = JsonDocument.Parse(json!);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        tools.Count.ShouldBeGreaterThan(20);
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            var annotations = tool.GetProperty("annotations");
            var expected = McpToolAnnotations.For(name, readOnlySession, sqlDmlAllowed: true);
            annotations.GetProperty("readOnlyHint").GetBoolean().ShouldBe(expected.ReadOnly, name);
            if (expected.ReadOnly)
            {
                annotations.TryGetProperty("destructiveHint", out _).ShouldBeFalse(name);
            }
            else
            {
                annotations.GetProperty("destructiveHint").GetBoolean().ShouldBe(expected.Destructive, name);
            }
        }

        // The dynamic tool from Mcp:AllowedOperations is unclassified, so it is never read-only.
        tools.Single(t => t.GetProperty("name").GetString() == "list_orders")
            .GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().ShouldBeFalse();
    }
}
