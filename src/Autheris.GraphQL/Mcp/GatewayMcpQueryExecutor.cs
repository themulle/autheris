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
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Kernel;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using HotChocolate.Execution;
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
    private readonly IGovernedExecutionKernel? _governedKernel;
    private readonly IPersistedToolValidator? _persistedToolValidator;
    private readonly ILogger<GatewayMcpQueryExecutor> _logger;

    public GatewayMcpQueryExecutor(
        IRequestExecutorProvider executorProvider,
        IGatewayExecutionService gatewayExecutionService,
        ILogger<GatewayMcpQueryExecutor> logger,
        IPreFlightQuerySimulator? querySimulator = null,
        IMcpProvenanceEnricher? provenanceEnricher = null,
        IGovernedExecutionKernel? governedKernel = null,
        IPersistedToolValidator? persistedToolValidator = null)
    {
        _executorProvider = executorProvider ?? throw new ArgumentNullException(nameof(executorProvider));
        _gatewayExecutionService = gatewayExecutionService ?? throw new ArgumentNullException(nameof(gatewayExecutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _querySimulator = querySimulator;
        _provenanceEnricher = provenanceEnricher;
        _governedKernel = governedKernel;
        _persistedToolValidator = persistedToolValidator;
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
            new("tenant_id", sessionContext.TenantId),
            new(ClaimTypes.Role, "AiAgent")
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
        else
        {
            claims.Add(new Claim(ClaimTypes.Role, "Reader"));
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

        // Native Governed Vector & RAG Egress Tool (F-AI-09)
        if (tool.Name.Equals("search_rag_context", StringComparison.OrdinalIgnoreCase))
        {
            if (_governedKernel != null)
            {
                if (string.IsNullOrWhiteSpace(sessionContext.TenantId))
                {
                    return CreateErrorResult("unknown", tool.Name, McpErrorCodes.Forbidden, "Access denied: Missing TenantId in session context.");
                }

                var callerSidStr = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
                    ? sessionContext.UserSid
                    : sessionContext.ServicePrincipalId;

                if (string.IsNullOrWhiteSpace(callerSidStr))
                {
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, "Access denied: Missing caller SID in session context.");
                }

                var collectionName = variables.TryGetValue("collection", out var cVal) && !string.IsNullOrWhiteSpace(cVal?.ToString())
                    ? cVal.ToString()!
                    : "documents";
                var queryText = variables.TryGetValue("query", out var qVal) ? qVal?.ToString() ?? "" : "";
                var topK = variables.TryGetValue("topK", out var kVal) && int.TryParse(kVal?.ToString(), out var parsedK)
                    ? Math.Clamp(parsedK, 1, 200)
                    : 5;

                var userSid = new Sid(callerSidStr);

                var groupSids = sessionContext.GroupSids != null
                    ? sessionContext.GroupSids.Select(s => new Sid(s)).ToHashSet()
                    : new HashSet<Sid>();

                // Zero-Trust: Do not assign default roles "out of thin air"
                var roles = sessionContext.Roles != null && sessionContext.Roles.Count > 0
                    ? sessionContext.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var secContext = new SecurityPrincipalContext
                {
                    UserSid = userSid,
                    TenantId = new TenantId(sessionContext.TenantId),
                    GroupSids = groupSids,
                    TenantRoles = roles,
                    ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    AuthenticationScheme = "McpAuth"
                };

                IReadOnlyList<float>? queryVector = null;
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(argumentsJson);
                        if (doc.RootElement.TryGetProperty("query_vector", out var qvElem) && qvElem.ValueKind == JsonValueKind.Array)
                        {
                            var vecList = new List<float>();
                            foreach (var item in qvElem.EnumerateArray())
                            {
                                if (item.TryGetSingle(out var f))
                                {
                                    vecList.Add(f);
                                }
                            }
                            if (vecList.Count > 0)
                            {
                                queryVector = vecList;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore invalid vector payload in json
                    }
                }

                var searchReq = new VectorSearchRequest(
                    TargetCollection: new TableIdentifier("ai", "public", collectionName),
                    QueryVector: queryVector,
                    RawQueryText: queryText,
                    TopK: topK
                );

                try
                {
                    var result = await _governedKernel.ExecuteVectorQueryAsync(searchReq, secContext, cancellationToken).ConfigureAwait(false);
                    // Information Disclosure Protection: Do not leak internal AccessDecision (RLS SQL, ColumnAccess map, DeniedReasons) to the AI agent
                    var clientDto = new
                    {
                        collection = result.Collection.ToQualifiedName(),
                        chunks = result.Chunks,
                        metrics = result.Metrics
                    };
                    var json = JsonSerializer.Serialize(clientDto, CamelCaseJsonOptions);
                    return await EnrichWithProvenanceAsync(tool, json, cancellationToken).ConfigureAwait(false);
                }
                catch (SecurityException ex)
                {
                    _logger.LogWarning(ex, "MCP search_rag_context access denied for tenant '{TenantId}', collection '{Collection}': {Message}", sessionContext.TenantId, collectionName, ex.Message);
                    // Return generic message to prevent oracle / policy detail disclosure
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, "Access to vector collection denied by governance policy.");
                }
                catch (TableNotFoundException ex)
                {
                    _logger.LogWarning(ex, "MCP search_rag_context table not found for tenant '{TenantId}', collection '{Collection}'", sessionContext.TenantId, collectionName);
                    // Return generic message to prevent collection enumeration oracle
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, "Access to vector collection denied by governance policy.");
                }
            }

            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotAvailable, "Governed execution kernel is not available for vector search.");
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
                        new[] { "AiAgent", "Reader" },
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
}

internal sealed record McpToolError(string Code, string Message);

internal sealed record McpToolErrorPayload(
    string TenantId,
    string Tool,
    bool IsError,
    McpToolError Error,
    JsonElement? GraphQL);
