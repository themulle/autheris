namespace Autheris.GraphQL.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.Logging;

/// <summary>
/// Production-grade MCP Query Executor bridging MCP tool calls into HotChocolate's execution engine
/// and Autheris's zero-trust execution pipeline with tenant isolation and authenticated principal context.
/// </summary>
public sealed class GatewayMcpQueryExecutor : IMcpQueryExecutor
{
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IRequestExecutorProvider _executorProvider;
    private readonly IGatewayExecutionService _gatewayExecutionService;
    private readonly IPreFlightQuerySimulator? _querySimulator;
    private readonly IMcpProvenanceEnricher? _provenanceEnricher;
    private readonly IPersistedToolValidator? _persistedToolValidator;
    private readonly IMcpDatasetCatalog? _datasetCatalog;
    private readonly ILogger<GatewayMcpQueryExecutor> _logger;

    public GatewayMcpQueryExecutor(
        IRequestExecutorProvider executorProvider,
        IGatewayExecutionService gatewayExecutionService,
        ILogger<GatewayMcpQueryExecutor> logger,
        IPreFlightQuerySimulator? querySimulator = null,
        IMcpProvenanceEnricher? provenanceEnricher = null,
        IPersistedToolValidator? persistedToolValidator = null,
        IMcpDatasetCatalog? datasetCatalog = null)
    {
        _executorProvider = executorProvider ?? throw new ArgumentNullException(nameof(executorProvider));
        _gatewayExecutionService = gatewayExecutionService ?? throw new ArgumentNullException(nameof(gatewayExecutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _querySimulator = querySimulator;
        _provenanceEnricher = provenanceEnricher;
        _persistedToolValidator = persistedToolValidator;
        _datasetCatalog = datasetCatalog;
    }

    public async Task<string> ExecuteOperationAsync(
        McpToolDefinition tool,
        string argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(sessionContext);

        _logger.LogInformation("Executing MCP Tool '{ToolName}' for Principal '{PrincipalId}' on Tenant '{TenantId}'.",
            tool.Name, sessionContext.ServicePrincipalId, sessionContext.TenantId);

        // Build authenticated ClaimsPrincipal from active MCP session preserving actual caller identity
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sessionContext.ServicePrincipalId),
            new("sub", sessionContext.ServicePrincipalId),
            new("tenant_id", sessionContext.TenantId)
        };

        if (!string.IsNullOrWhiteSpace(sessionContext.UserSid))
        {
            claims.Add(new Claim(ClaimTypes.PrimarySid, sessionContext.UserSid));
        }

        if (sessionContext.Roles != null && sessionContext.Roles.Count > 0)
        {
            foreach (var role in sessionContext.Roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        if (sessionContext.GroupSids != null)
        {
            foreach (var groupSid in sessionContext.GroupSids)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, groupSid));
            }
        }

        var identity = new ClaimsIdentity(claims, "McpAuth");
        var principal = new ClaimsPrincipal(identity);

        // Parse input arguments if provided
        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(argumentsJson) && argumentsJson.Trim() != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                // F-AI-11: Validate tool parameters and prompt injection shield
                if (_persistedToolValidator != null && !_persistedToolValidator.ValidateToolInvocation(tool, doc.RootElement, out var failureReason))
                {
                    _logger.LogWarning("MCP tool '{ToolName}' failed persisted tool validation: {Reason}", tool.Name, failureReason);
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, $"Invocation blocked by MCP Persisted Tool Guardrail: {failureReason}");
                }

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        variables[prop.Name] = ConvertJsonElement(prop.Value);
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse arguments JSON for tool '{ToolName}'.", tool.Name);
                return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, "Invalid arguments JSON payload.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error parsing arguments JSON for tool '{ToolName}'.", tool.Name);
                return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, "Invalid arguments JSON payload.");
            }
        }

        // Pre-Flight Query Simulator Tool (F-AI-04)
        if (tool.Name.Equals("simulate_query", StringComparison.OrdinalIgnoreCase))
        {
            string queryString = variables.TryGetValue("query", out var qObj) ? qObj?.ToString() ?? "" : "";
            if (_querySimulator != null)
            {
                var simResult = await _querySimulator.SimulateQueryAsync(queryString, tool.TargetTable, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(simResult, CamelCaseJsonOptions);
            }

            // SEC M-17: Ohne Simulator keine erfundene Freigabe ("isAllowed":true) zurückgeben.
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotAvailable, "Query simulator is not available.");
        }

        // Dataset tools: catalog discovery and governed sample rows
        if (McpDatasetTools.IsDatasetTool(tool.Name))
        {
            return await ExecuteDatasetToolAsync(tool, principal, sessionContext, variables, argumentsJson, cancellationToken).ConfigureAwait(false);
        }

        // Fast-path / Specialized execution for registered tables if operation is standard table query
        if (tool.Name.Equals("query_customers", StringComparison.OrdinalIgnoreCase) ||
            tool.Name.Equals("query_invoices", StringComparison.OrdinalIgnoreCase))
        {
            var fastPathResult = await TryExecuteTableFastPathAsync(tool.Name, principal, sessionContext, variables, cancellationToken).ConfigureAwait(false);
            if (fastPathResult != null)
            {
                return await EnrichWithProvenanceAsync(tool, fastPathResult, cancellationToken).ConfigureAwait(false);
            }
        }

        // 4b.2: Query Data Catalog Metadata using dataset catalog
        if (tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteQueryDataCatalogAsync(tool, principal, sessionContext, variables, cancellationToken).ConfigureAwait(false);
        }

        // Standard GraphQL execution via HotChocolate IRequestExecutor
        // Hinweis (SEC M-17): Die Resolver lesen den Principal derzeit aus IHttpContextAccessor (HTTP-Aufrufer der MCP-Session),
        // nicht aus dem GlobalState "ClaimsPrincipal". Die hier aufgebaute MCP-Identität wirkt daher nur für Komponenten,
        // die den GlobalState auswerten; Resolver-Umstellung ist als Folgearbeit dokumentiert.
        if (!string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation))
        {
            try
            {
                var effectiveCallerSid = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
                    ? new Sid(sessionContext.UserSid)
                    : new Sid(sessionContext.ServicePrincipalId);

                var groupSids = sessionContext.GroupSids != null && sessionContext.GroupSids.Count > 0
                    ? sessionContext.GroupSids.Select(s => new Sid(s)).ToArray()
                    : Array.Empty<Sid>();

                var executor = await _executorProvider.GetExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var requestBuilder = OperationRequestBuilder.New()
                    .SetDocument(tool.TargetGraphQLOperation)
                    .AddGlobalState("ClaimsPrincipal", principal)
                    .AddGlobalState("CallerSecurityContext", new CallerSecurityContext(
                        effectiveCallerSid,
                        groupSids,
                        sessionContext.Roles?.ToArray() ?? Array.Empty<string>(),
                        new TenantId(sessionContext.TenantId),
                        IsGovernanceAdmin: false,
                        IsClusterAdmin: false
                    ));

                if (variables.Count > 0)
                {
                    requestBuilder.SetVariableValues(variables);
                }

                var executionResult = await executor.ExecuteAsync(requestBuilder.Build(), cancellationToken).ConfigureAwait(false);
                if (executionResult is OperationResult op)
                {
                    var json = FormatOperationResult(op);
                    if (op.Errors is null || op.Errors.Count == 0)
                    {
                        return await EnrichWithProvenanceAsync(tool, json, cancellationToken).ConfigureAwait(false);
                    }

                    // SEC M-17: GraphQL-Fehler (bereits durch den Error-Filter maskiert) strukturiert an den Agenten durchreichen.
                    _logger.LogWarning("GraphQL execution returned {ErrorCount} error(s) for MCP tool '{ToolName}'.", op.Errors.Count, tool.Name);
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ExecutionFailed, "GraphQL execution returned errors.", json);
                }

                return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ExecutionFailed, "Unsupported GraphQL execution result.");
            }
            catch (GatewayForbiddenException ex)
            {
                _logger.LogWarning(ex, "MCP tool '{ToolName}' was denied by governance policy.", tool.Name);
                return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, "Access denied by data governance policy.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GraphQL execution failed for MCP tool '{ToolName}'.", tool.Name);
                return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ExecutionFailed, "Tool execution failed.");
            }
        }

        // SEC M-17: Kein Mock-/Beispieldaten-Fallback im Produktionspfad.
        _logger.LogWarning("MCP tool '{ToolName}' has no executable target operation.", tool.Name);
        return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotAvailable, "Tool has no executable target operation.");
    }

    private async Task<string> ExecuteDatasetToolAsync(
        McpToolDefinition tool,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        Dictionary<string, object?> variables,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        if (string.Equals(tool.Name, McpDatasetTools.QueryGraphQl, StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteGraphQlQueryAsync(tool, principal, sessionContext, argumentsJson, cancellationToken).ConfigureAwait(false);
        }

        if (_datasetCatalog == null)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotAvailable, "The dataset catalog is not available.");
        }

        string? Text(string name) => variables.TryGetValue(name, out var v) ? v as string : null;
        int? Number(string name) => variables.TryGetValue(name, out var v) ? v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => null
        } : null;

        try
        {
            object result = tool.Name.ToLowerInvariant() switch
            {
                McpDatasetTools.ListDatasets => (Number("offset") != null || Number("limit") != null)
                    ? await _datasetCatalog.ListDatasetsAsync(principal, Text("search"), Text("domain"), Number("offset"), Number("limit"), cancellationToken).ConfigureAwait(false)
                    : await _datasetCatalog.ListDatasetsAsync(principal, Text("search"), Text("domain"), cancellationToken).ConfigureAwait(false),
                McpDatasetTools.DescribeDataset => await _datasetCatalog.DescribeDatasetAsync(principal, Text("dataset") ?? string.Empty, cancellationToken).ConfigureAwait(false),
                _ => await _datasetCatalog.SampleRowsAsync(principal, Text("dataset") ?? string.Empty, ToCount(variables), cancellationToken).ConfigureAwait(false)
            };

            return JsonSerializer.Serialize(result, CamelCaseJsonOptions);
        }
        catch (TableNotFoundException)
        {
            // Hidden and missing datasets look the same to the agent.
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotFound, "Dataset not found or not visible.");
        }
        catch (GatewayForbiddenException ex)
        {
            _logger.LogWarning(ex, "MCP tool '{ToolName}' was denied by governance policy.", tool.Name);
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, "Access denied by data governance policy.");
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dataset tool '{ToolName}' failed.", tool.Name);
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ExecutionFailed, "Tool execution failed.");
        }
    }

    private async Task<string> ExecuteQueryDataCatalogAsync(
        McpToolDefinition tool,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        Dictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        if (_datasetCatalog == null)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotAvailable, "The dataset catalog is not available.");
        }

        string? tableName = variables.TryGetValue("tableName", out var tn) ? tn as string : null;
        var list = await _datasetCatalog.ListDatasetsAsync(principal, search: tableName, domain: null, ct: cancellationToken).ConfigureAwait(false);

        var datasets = list?.Datasets ?? Array.Empty<McpDatasetSummary>();
        var assets = datasets.Select(d => new
        {
            tableName = d.Id,
            sensitivity = d.Sensitivity,
            classification = d.Sensitivity,
            owner = "data-governance@autheris.local",
            tags = new string[] { "catalog", d.Sensitivity.ToLowerInvariant() }
        }).ToList();

        return JsonSerializer.Serialize(new { tenantId = sessionContext.TenantId, assets }, CamelCaseJsonOptions);
    }

    /// <summary>
    /// query_graphql: one read-only GraphQL query against the gateway schema. The catalog resolvers authorize every
    /// table with the caller's identity (consent, ReBAC, Casbin, row filters, masking), as for POST /graphql.
    /// </summary>
    private async Task<string> ExecuteGraphQlQueryAsync(
        McpToolDefinition tool,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        string Invalid(string message) => CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, message);

        if (!McpDatasetTools.TryGetStringArgument(argumentsJson, "query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return Invalid("The 'query' argument (one GraphQL query) is required.");
        }

        Dictionary<string, object?>? variableValues = null;
        using (var args = JsonDocument.Parse(argumentsJson!))
        {
            foreach (var property in args.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "variables", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    return Invalid("'variables' must be an object.");
                }

                variableValues = (Dictionary<string, object?>)ToVariableValue(property.Value)!;
            }
        }

        DocumentNode document;
        try
        {
            document = Utf8GraphQLParser.Parse(query);
        }
        catch (SyntaxException ex)
        {
            return Invalid($"The query could not be parsed: {ex.Message}");
        }

        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count != 1)
        {
            return Invalid("Send exactly one operation per call.");
        }

        if (operations[0].Operation != OperationType.Query)
        {
            return Invalid("Only queries are allowed over MCP; mutations and subscriptions are not available.");
        }

        var op = operations[0];
        if (variableValues != null && variableValues.Count > 0 && op.VariableDefinitions.Count > 0)
        {
            // 4b.3: If query uses variables for limits without explicit defaults in the AST,
            // augment the operation's variable definitions with default values from the provided runtime variables
            // so that QueryCostAnalyzerRule can evaluate the actual requested limit instead of assuming worst-case rows.
            bool modified = false;
            var newVarDefs = new List<VariableDefinitionNode>();
            foreach (var vDef in op.VariableDefinitions)
            {
                if (variableValues.TryGetValue(vDef.Variable.Name.Value, out var val) && TryExtractPositiveInt(val, out var numVal))
                {
                    if (numVal <= 100_000)
                    {
                        newVarDefs.Add(vDef.WithDefaultValue(new IntValueNode(numVal)));
                        modified = true;
                        continue;
                    }
                }
                newVarDefs.Add(vDef);
            }

            if (modified)
            {
                var newOp = op.WithVariableDefinitions(newVarDefs);
                document = document.WithDefinitions(document.Definitions.Select(d => ReferenceEquals(d, op) ? (IDefinitionNode)newOp : d).ToList());
            }
        }

        var executor = await _executorProvider.GetExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var requestBuilder = OperationRequestBuilder.New()
            .SetDocument(document)
            .AddGlobalState("ClaimsPrincipal", principal);
        if (variableValues != null)
        {
            requestBuilder.SetVariableValues(variableValues);
        }

        var executionResult = await executor.ExecuteAsync(requestBuilder.Build(), cancellationToken).ConfigureAwait(false);
        if (executionResult is not OperationResult result)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ExecutionFailed, "Unsupported GraphQL execution result.");
        }

        var json = FormatOperationResult(result);
        return result.Errors is null || result.Errors.Count == 0
            ? json
            : CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ExecutionFailed, "GraphQL execution returned errors.", json);
    }

    private static object? ToVariableValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ToVariableValue(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(ToVariableValue).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt32(out var i) => i,
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static int? ToCount(Dictionary<string, object?> variables) =>
        variables.TryGetValue("count", out var v) ? v switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            double d => (int)Math.Clamp(d, int.MinValue, int.MaxValue),
            _ => null
        } : null;

    private async Task<string> EnrichWithProvenanceAsync(McpToolDefinition tool, string json, CancellationToken cancellationToken)
    {
        if (_provenanceEnricher != null && tool.TargetTable.HasValue)
        {
            var prov = await _provenanceEnricher.CreateProvenanceAsync(tool.TargetTable.Value, cancellationToken).ConfigureAwait(false);
            return _provenanceEnricher.EnrichPayloadWithProvenance(json, prov);
        }

        return json;
    }

    /// <summary>
    /// SEC M-17: Strukturiertes Fehler-Ergebnis für MCP-Tool-Aufrufe (Deny, Ausführungsfehler, nicht verfügbar).
    /// </summary>
    internal static string CreateErrorResult(string tenantId, string toolName, string code, string message, string? graphQLResultJson = null)
    {
        JsonElement? graphQL = null;
        if (!string.IsNullOrWhiteSpace(graphQLResultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(graphQLResultJson);
                graphQL = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                graphQL = null;
            }
        }

        return JsonSerializer.Serialize(new McpToolErrorPayload(
            tenantId,
            toolName,
            IsError: true,
            new McpToolError(code, message),
            graphQL), CamelCaseJsonOptions);
    }

    private async Task<string?> TryExecuteTableFastPathAsync(
        string toolName,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        Dictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        string domain = "finance";
        string schema = "dbo";
        string table = toolName.Equals("query_customers", StringComparison.OrdinalIgnoreCase) ? "customers" : "invoices";
        var tableId = new TableIdentifier(domain, schema, table);

        try
        {
            int first = variables.TryGetValue("limit", out var limObj) && limObj is int l ? l : 50;

            var (rows, decision) = await _gatewayExecutionService.ExecuteTableQueryAsync(
                principal,
                tableId,
                first: first,
                after: 0,
                queryArguments: variables,
                requestedFields: null,
                requestHeaders: null,
                ct: cancellationToken).ConfigureAwait(false);

            if (!decision.IsAllowed)
            {
                _logger.LogWarning("Access to table '{Table}' for MCP tool was denied by governance engine.", tableId);
                return CreateErrorResult(sessionContext.TenantId, toolName, McpErrorCodes.Forbidden, "Access denied by data governance policy.");
            }

            return JsonSerializer.Serialize(new
            {
                tenantId = sessionContext.TenantId,
                totalCount = rows.Count,
                items = rows
            });
        }
        catch (GatewayForbiddenException ex)
        {
            // SEC M-17: Verweigerungen nicht verschlucken, sondern als strukturiertes Fehler-Ergebnis melden.
            _logger.LogWarning(ex, "Access to table '{Table}' for MCP tool '{ToolName}' was denied.", tableId, toolName);
            return CreateErrorResult(sessionContext.TenantId, toolName, McpErrorCodes.Forbidden, "Access denied by data governance policy.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TableNotFoundException ex)
        {
            _logger.LogDebug(ex, "Fast-path table not found for '{ToolName}'. Falling back to GraphQL operation.", toolName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fast-path execution failed for MCP tool '{ToolName}'.", toolName);
            return CreateErrorResult(sessionContext.TenantId, toolName, McpErrorCodes.ExecutionFailed, "Tool execution failed.");
        }
    }

    private static object? ConvertJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number when el.TryGetInt32(out var i) => i,
        JsonValueKind.Number when el.TryGetInt64(out var l) => l,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText()
    };

    private static bool TryExtractPositiveInt(object? val, out int result)
    {
        result = 0;
        if (val == null) return false;
        if (val is int i) { result = i; return i > 0; }
        if (val is long l && l <= int.MaxValue && l > 0) { result = (int)l; return true; }
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji) && ji > 0)
            {
                result = ji;
                return true;
            }
            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var js) && js > 0)
            {
                result = js;
                return true;
            }
        }
        if (int.TryParse(val.ToString(), out var parsed) && parsed > 0)
        {
            result = parsed;
            return true;
        }
        return false;
    }

    private static string FormatOperationResult(OperationResult op)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        HotChocolate.Transport.Formatters.JsonResultFormatter.Default.Format(op, writer);
        return System.Text.Encoding.UTF8.GetString(writer.WrittenSpan);
    }
}

/// <summary>
/// SEC M-17: Fehlercodes für strukturierte MCP-Tool-Fehlerergebnisse.
/// </summary>
public static class McpErrorCodes
{
    public const string Forbidden = "FORBIDDEN";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string ExecutionFailed = "EXECUTION_FAILED";
    public const string NotAvailable = "NOT_AVAILABLE";
    public const string NotFound = "NOT_FOUND";
}

internal sealed record McpToolError(string Code, string Message);

internal sealed record McpToolErrorPayload(
    string TenantId,
    string Tool,
    bool IsError,
    McpToolError Error,
    JsonElement? GraphQL);
