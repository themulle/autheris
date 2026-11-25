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
/// <remarks>
/// Issue #1109: the discovery awaits the GraphQL executor, whose first build can take minutes on a large catalog. It runs
/// after host start (<see cref="BackgroundService"/>), so Kestrel listens and <c>/health/live</c> answers during the build;
/// <c>/health/ready</c> stays 503 until <see cref="Phase"/> is <see cref="McpSchemaDiscoveryPhase.Ready"/>. A failure sets
/// <see cref="McpSchemaDiscoveryPhase.Failed"/> and retries with back-off (5 s doubling, cap 60 s); it never stops the host.
/// </remarks>
public sealed partial class McpSchemaDiscoveryService : BackgroundService, IMcpSchemaDiscoveryState
{
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly IRequestExecutorProvider _executorProvider;
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly ILogger<McpSchemaDiscoveryService> _logger;
    private readonly TimeProvider _timeProvider;
    private int _phase;

    public McpSchemaDiscoveryService(
        IRequestExecutorProvider executorProvider,
        IMcpToolRegistry toolRegistry,
        ILogger<McpSchemaDiscoveryService> logger,
        TimeProvider? timeProvider = null)
    {
        _executorProvider = executorProvider ?? throw new ArgumentNullException(nameof(executorProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public McpSchemaDiscoveryPhase Phase => (McpSchemaDiscoveryPhase)Volatile.Read(ref _phase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Return to the host at once: everything below may wait for the full schema build.
        await Task.Yield();

        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            var start = _timeProvider.GetTimestamp();
            LogDiscoveryStarted(_logger);
            try
            {
                var count = await DiscoverAsync(stoppingToken).ConfigureAwait(false);
                Volatile.Write(ref _phase, (int)McpSchemaDiscoveryPhase.Ready);
                LogDiscoveryCompleted(_logger, count, (long)_timeProvider.GetElapsedTime(start).TotalMilliseconds);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _phase, (int)McpSchemaDiscoveryPhase.Failed);
                LogDiscoveryFailed(_logger, ex, (int)backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task<int> DiscoverAsync(CancellationToken cancellationToken)
    {
        var executor = await _executorProvider.GetExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var schema = executor.Schema;

        int discoveredCount = 0;
        if (schema.QueryType is not { } queryType)
        {
            return discoveredCount;
        }

        foreach (var field in queryType.Fields)
        {
            var directive = field.Directives.FirstOrDefault("mcpTool");
            if (directive == null)
            {
                continue;
            }

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
            // MCP-6: Do NOT guess TargetTable by splitting field names or assuming default.dbo.
            // When no explicit targetTable is provided, leave TargetTable null so that
            // AiDataGuardrailService and McpGraphQlTableResolver resolve the actual tables from the GraphQL operation AST.

            var toolDef = new McpToolDefinition(toolName, description, inputSchema, targetOp, targetTable);
            _toolRegistry.RegisterTool(toolDef);
            discoveredCount++;

            LogToolRegistered(_logger, toolName, targetTable, field.Name);
        }

        return discoveredCount;
    }

    [LoggerMessage(EventId = 15131, Level = LogLevel.Information, Message = "MCP schema discovery started; waiting for the GraphQL executor (readiness stays 503 until it completes)")]
    private static partial void LogDiscoveryStarted(ILogger logger);

    [LoggerMessage(EventId = 15132, Level = LogLevel.Information, Message = "MCP Schema Discovery completed: {Count} dynamic tools registered in {ElapsedMs} ms.")]
    private static partial void LogDiscoveryCompleted(ILogger logger, int count, long elapsedMs);

    [LoggerMessage(EventId = 15133, Level = LogLevel.Error, Message = "MCP schema discovery failed; readiness stays 503, retrying in {RetrySeconds} s")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception exception, int retrySeconds);

    [LoggerMessage(EventId = 15134, Level = LogLevel.Information, Message = "Discovered and registered MCP Tool '{ToolName}' (Target: {Table}) from GraphQL field '{FieldName}'.")]
    private static partial void LogToolRegistered(ILogger logger, string toolName, TableIdentifier? table, string fieldName);

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
