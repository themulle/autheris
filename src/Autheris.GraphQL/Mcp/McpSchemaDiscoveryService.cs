namespace Autheris.GraphQL.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted background service discovering GraphQL schema fields annotated with @mcpTool
/// or standard registered query roots, and dynamically registering them in the IMcpToolRegistry.
/// </summary>
public sealed class McpSchemaDiscoveryService : IHostedService
{
    private readonly IRequestExecutorProvider _executorProvider;
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly ILogger<McpSchemaDiscoveryService> _logger;

    public McpSchemaDiscoveryService(
        IRequestExecutorProvider executorProvider,
        IMcpToolRegistry toolRegistry,
        ILogger<McpSchemaDiscoveryService> logger)
    {
        _executorProvider = executorProvider ?? throw new ArgumentNullException(nameof(executorProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var executor = await _executorProvider.GetExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var schema = executor.Schema;

            if (schema.QueryType is { } queryType)
            {
                int discoveredCount = 0;
                foreach (var field in queryType.Fields)
                {
                    var directive = field.Directives.FirstOrDefault("mcpTool");
                    if (directive != null)
                    {
                        string? toolName = null;
                        if (directive.Arguments.TryGetValue("name", out var nameVal) && nameVal is StringValueNode sn)
                        {
                            toolName = sn.Value;
                        }
                        toolName ??= field.Name;

                        string? description = null;
                        if (directive.Arguments.TryGetValue("description", out var descVal) && descVal is StringValueNode ds)
                        {
                            description = ds.Value;
                        }
                        description ??= field.Description ?? $"Executes GraphQL query operation {field.Name}";

                        var inputSchema = BuildInputJsonSchema(field.Arguments);
                        var targetOp = BuildGraphQLOperation(field);

                        TableIdentifier? targetTable = null;
                        if (directive.Arguments.TryGetValue("targetTable", out var ttVal) && ttVal is StringValueNode ttn)
                        {
                            var parts = ttn.Value.Split('.');
                            if (parts.Length == 3) targetTable = new TableIdentifier(parts[0], parts[1], parts[2]);
                        }
                        else if (directive.Arguments.TryGetValue("table", out var tVal) && tVal is StringValueNode tn)
                        {
                            var parts = tn.Value.Split('.');
                            if (parts.Length == 3) targetTable = new TableIdentifier(parts[0], parts[1], parts[2]);
                        }
                        // MCP-6: Do NOT guess TargetTable by splitting field names or assuming default.dbo.
                        // When no explicit targetTable is provided, leave TargetTable null so that
                        // AiDataGuardrailService and McpGraphQlTableResolver resolve the actual tables from the GraphQL operation AST.

                        var toolDef = new McpToolDefinition(toolName, description, inputSchema, targetOp, targetTable);
                        _toolRegistry.RegisterTool(toolDef);
                        discoveredCount++;

                        _logger.LogInformation("Discovered and registered MCP Tool '{ToolName}' (Target: {Table}) from GraphQL field '{FieldName}'.",
                            toolName, targetTable, field.Name);
                    }
                }

                _logger.LogInformation("MCP Schema Discovery completed: {Count} dynamic tools registered.", discoveredCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "McpSchemaDiscoveryService encountered an issue during startup schema reflection. Default tools remain active.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string BuildInputJsonSchema(IEnumerable<IInputValueDefinition> arguments)
    {
        var properties = new Dictionary<string, object>();
        var requiredList = new List<string>();

        foreach (var arg in arguments)
        {
            string jsonType = "string";
            bool isNonNull = arg.Type.Kind == TypeKind.NonNull;
            if (isNonNull)
            {
                requiredList.Add(arg.Name);
            }

            IType innerType = arg.Type;
            while (innerType is IWrapperType wrapper)
            {
                innerType = wrapper.InnerType;
            }

            string typeName = innerType is INameProvider nameProvider ? nameProvider.Name : innerType.ToString() ?? "";
            if (typeName.Contains("Int", StringComparison.OrdinalIgnoreCase)) jsonType = "integer";
            else if (typeName.Contains("Float", StringComparison.OrdinalIgnoreCase)) jsonType = "number";
            else if (typeName.Contains("Boolean", StringComparison.OrdinalIgnoreCase)) jsonType = "boolean";

            properties[arg.Name] = new
            {
                type = jsonType,
                description = arg.Description ?? $"Parameter {arg.Name}"
            };
        }

        var schemaObj = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (requiredList.Count > 0)
        {
            schemaObj["required"] = requiredList;
        }

        return JsonSerializer.Serialize(schemaObj);
    }

    private static string BuildGraphQLOperation(IOutputFieldDefinition field)
    {
        var argDefs = new List<string>();
        var argUsages = new List<string>();

        foreach (var arg in field.Arguments)
        {
            string typeStr = FormatTypeString(arg.Type);
            argDefs.Add($"${arg.Name}: {typeStr}");
            argUsages.Add($"{arg.Name}: ${arg.Name}");
        }

        string argDefString = argDefs.Count > 0 ? $"({string.Join(", ", argDefs)})" : "";
        string argUsageString = argUsages.Count > 0 ? $"({string.Join(", ", argUsages)})" : "";

        return $"query AutoGenerated_{field.Name}{argDefString} {{ {field.Name}{argUsageString} }}";
    }

    private static string FormatTypeString(IType type)
    {
        return type switch
        {
            IWrapperType wrapper when type.Kind == TypeKind.NonNull => $"{FormatTypeString(wrapper.InnerType)}!",
            IWrapperType wrapper when type.Kind == TypeKind.List => $"[{FormatTypeString(wrapper.InnerType)}]",
            INameProvider named => named.Name,
            _ => type.ToString() ?? "String"
        };
    }
}
