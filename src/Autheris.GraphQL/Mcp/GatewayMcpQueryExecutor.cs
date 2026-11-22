namespace Autheris.GraphQL.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Production-grade MCP Query Executor bridging MCP tool calls into HotChocolate's execution engine
/// and Autheris's zero-trust execution pipeline with tenant isolation and authenticated principal context.
/// </summary>
public sealed class GatewayMcpQueryExecutor : IMcpQueryExecutor
{
    // Issue #702: no null members and no empty objects in tool results.
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = McpResultJson.Options;

    private readonly IRequestExecutorProvider _executorProvider;
    private readonly IGatewayExecutionService _gatewayExecutionService;
    private readonly IPreFlightQuerySimulator? _querySimulator;
    private readonly IMcpProvenanceEnricher? _provenanceEnricher;
    private readonly IPersistedToolValidator _persistedToolValidator;
    private readonly IMcpDatasetCatalog? _datasetCatalog;
    private readonly IMcpToolExecutionHandler? _toolExecutionHandler;
    private readonly Autheris.Application.Catalog.Discovery.IDiscoveryAccessRequestService? _accessRequests;
    private readonly ILogger<GatewayMcpQueryExecutor> _logger;

    // Issue #447: the in-process GraphQL request runs in the MCP request scope, so it inherits the stamped ingress.
    // Without it HotChocolate would open a fresh scope with no ingress, which the transport gate denies.
    private readonly IServiceProvider? _services;

    // Mcp:MaxResultRows is re-read per call; a value <= 0 falls back to the default cap (never unbounded).
    private readonly IOptionsMonitor<GatewayOptions> _options;

    // tool and numbers only, never row values. No audit entry records a cut, so the debug log is the trace.
    private string Capped(string toolName, string original, string trimmed)
    {
        if (!ReferenceEquals(original, trimmed))
        {
            var cap = RowCap();
            _logger.LogDebug("MCP tool '{ToolName}' result was cut to the row cap {RowCap}.", toolName, cap);
        }

        return trimmed;
    }

    private int RowCap() => McpRowCap.ResolveCap(_options.CurrentValue.Mcp.MaxResultRows);

    public GatewayMcpQueryExecutor(
        IRequestExecutorProvider executorProvider,
        IGatewayExecutionService gatewayExecutionService,
        ILogger<GatewayMcpQueryExecutor> logger,
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        IPersistedToolValidator persistedToolValidator,
        IPreFlightQuerySimulator? querySimulator = null,
        IMcpProvenanceEnricher? provenanceEnricher = null,
        IMcpDatasetCatalog? datasetCatalog = null,
        IMcpToolExecutionHandler? toolExecutionHandler = null,
        IServiceProvider? services = null,
        Autheris.Application.Catalog.Discovery.IDiscoveryAccessRequestService? accessRequests = null)
    {
        _accessRequests = accessRequests;
        _services = services;
        _options = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _executorProvider = executorProvider ?? throw new ArgumentNullException(nameof(executorProvider));
        _gatewayExecutionService = gatewayExecutionService ?? throw new ArgumentNullException(nameof(gatewayExecutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _querySimulator = querySimulator;
        _provenanceEnricher = provenanceEnricher;
        // The validator is required; a host without it fails at startup instead of running tools unchecked.
        _persistedToolValidator = persistedToolValidator ?? throw new ArgumentNullException(nameof(persistedToolValidator));
        _datasetCatalog = datasetCatalog;
        _toolExecutionHandler = toolExecutionHandler;
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

        var principal = BuildPrincipal(sessionContext);

        // Arguments are validated for every call (empty ones included) against the closed tool schema.
        // Property names are compared ordinally; the validator already rejected duplicates and case variants.
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        var effectiveArguments = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        if (System.Text.Encoding.UTF8.GetByteCount(effectiveArguments) > PersistedToolValidator.MaxArgumentsBytes)
        {
            _logger.LogWarning("MCP tool '{ToolName}' rejected: arguments exceed the size limit.", tool.Name);
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, PersistedToolValidator.InvalidArgumentsCode);
        }

        try
        {
            using var doc = JsonDocument.Parse(effectiveArguments);
            // F-AI-11: Validate tool parameters and prompt injection shield. The failure text is the fixed code only.
            if (!_persistedToolValidator.ValidateToolInvocation(tool, doc.RootElement, out _))
            {
                _logger.LogWarning("MCP tool '{ToolName}' failed persisted tool validation.", tool.Name);
                return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, PersistedToolValidator.InvalidArgumentsCode);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unexpected error parsing arguments JSON for tool '{ToolName}'.", tool.Name);
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, "Invalid arguments JSON payload.");
        }

        // a bad client row limit is rejected before any tool runs.
        if (McpRowCap.LimitArgument(tool.Name) is { } limitArgument
            && !McpRowCap.TryReadLimit(variables.GetValueOrDefault(limitArgument), out _))
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, $"'{limitArgument}' must be a positive integer.");
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

            // M-17: Never return a fabricated approval ("isAllowed":true) when the simulator is absent.
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotAvailable, "Query simulator is not available.");
        }

        // Hybrid tools: SQL, dataset REST, catalog discovery, permissions, datasources, lineage, dynamic API
        if (_toolExecutionHandler != null && _toolExecutionHandler.CanHandle(tool.Name))
        {
            try
            {
                var handled = await _toolExecutionHandler.ExecuteToolAsync(tool, argumentsJson, sessionContext, cancellationToken).ConfigureAwait(false);
                return Capped(tool.Name, handled, McpRowCap.TrimRows(handled, RowCap()));
            }
            catch (Autheris.Application.Catalog.Discovery.DiscoveryStatusException ex)
            {
                return CreateDiscoveryErrorResult(sessionContext.TenantId, tool.Name, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && SqlCompilerErrorMapper.TryMap(ex, out _))
            {
                // WP-X0a: a governed SQL error answers with its stable code and fixed message, never Exception.Message.
                return CreateMappedErrorResult(sessionContext.TenantId, tool.Name, ex);
            }
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

        // Standard GraphQL execution via HotChocolate IRequestExecutor.
        // All GraphQL resolvers and execution middlewares resolve the session ClaimsPrincipal from global state.
        if (!string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation))
        {
            if (principal.IsReadOnly())
            {
                try
                {
                    var doc = HotChocolate.Language.Utf8GraphQLParser.Parse(tool.TargetGraphQLOperation);
                    if (Autheris.GraphQL.Interceptors.ReadOnlyOperationMiddleware.IsMutation(doc, null))
                    {
                        _logger.LogWarning("MCP tool '{ToolName}' rejected: read-only principal attempted mutation.", tool.Name);
                        return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, "This token only permits read access; mutations are rejected.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to parse target GraphQL operation for tool '{ToolName}'.", tool.Name);
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.Forbidden, "Invalid target operation for curated tool.");
                }
            }

            try
            {
                var effectiveCallerSid = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
                    ? new Sid(sessionContext.UserSid)
                    : new Sid(sessionContext.ServicePrincipalId);

                // SR-M-08: an app-only session (no user SID) carries no group SIDs, exactly like its principal.
                var groupSids = !string.IsNullOrWhiteSpace(sessionContext.UserSid) && sessionContext.GroupSids != null && sessionContext.GroupSids.Count > 0
                    ? sessionContext.GroupSids.Select(s => new Sid(s)).ToArray()
                    : Array.Empty<Sid>();

                var executor = await _executorProvider.GetExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var requestBuilder = OperationRequestBuilder.New()
                    .SetDocument(tool.TargetGraphQLOperation)
                    .SetServices(_services)
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

                    // GraphQL errors (masked by the error filter) are returned cleanly to the caller.
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

        if (string.Equals(tool.Name, McpDatasetTools.RequestAccess, StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteRequestAccessAsync(tool, principal, sessionContext, variables, cancellationToken).ConfigureAwait(false);
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
        bool? Bool(string name) => variables.TryGetValue(name, out var v) ? McpDatasetTools.ToBooleanArgument(v) : null;

        try
        {
            McpDescribeNarrowing.Request? describeNarrowing = null;
            if (string.Equals(tool.Name, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase))
            {
                if (!McpDescribeNarrowing.TryParse(
                        variables.GetValueOrDefault("include"),
                        variables.GetValueOrDefault("columns"),
                        Number("columnOffset"),
                        Number("columnLimit"),
                        variables.GetValueOrDefault("relations"),
                        out var parsedNarrowing))
                {
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, PersistedToolValidator.InvalidArgumentsCode);
                }

                describeNarrowing = parsedNarrowing;
            }

            // Issues #798 / #804: a present accessStatus is a known status string; non-string, blank and unknown values are rejected with
            // the fixed text of the search tools (the value is never echoed).
            string? accessStatus = null;
            if (string.Equals(tool.Name, McpDatasetTools.ListDatasets, StringComparison.OrdinalIgnoreCase)
                && variables.TryGetValue("accessStatus", out var rawAccessStatus) && rawAccessStatus is not null)
            {
                if (rawAccessStatus is not string accessText || !Autheris.Application.Catalog.Discovery.CatalogAccessStatus.TryParseFilter(accessText, out _))
                {
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, Autheris.Application.Catalog.Discovery.CatalogAccessStatus.InvalidFilterMessage);
                }

                accessStatus = accessText;
            }

            object result = tool.Name.ToLowerInvariant() switch
            {
                McpDatasetTools.ListDatasets => Text("graphQlField") is { } listGraphQlField
                    ? await _datasetCatalog.ListDatasetsAsync(principal, Text("search"), Text("domain"), Number("offset"), Number("limit"), Text("tag"), Text("language"), Bool("overview"), Text("format"), accessStatus, listGraphQlField, cancellationToken).ConfigureAwait(false)
                    : accessStatus is not null
                    ? await _datasetCatalog.ListDatasetsAsync(principal, Text("search"), Text("domain"), Number("offset"), Number("limit"), Text("tag"), Text("language"), Bool("overview"), Text("format"), accessStatus, cancellationToken).ConfigureAwait(false)
                    : (Text("language") != null || Bool("overview") != null || Text("format") != null)
                    ? await _datasetCatalog.ListDatasetsAsync(principal, Text("search"), Text("domain"), Number("offset"), Number("limit"), Text("tag"), Text("language"), Bool("overview"), Text("format"), cancellationToken).ConfigureAwait(false)
                    : (Number("offset") != null || Number("limit") != null || Text("tag") != null)
                        ? await _datasetCatalog.ListDatasetsAsync(principal, Text("search"), Text("domain"), Number("offset"), Number("limit"), Text("tag"), cancellationToken).ConfigureAwait(false)
                        : await _datasetCatalog.ListDatasetsAsync(principal, Text("search"), Text("domain"), cancellationToken).ConfigureAwait(false),
                McpDatasetTools.DescribeDataset => Text("sqlName") != null
                    ? await _datasetCatalog.DescribeDatasetAsync(principal, Text("dataset"), Text("graphQlField"), Text("sqlName"), Text("language"), Bool("brief") ?? false, cancellationToken).ConfigureAwait(false)
                    : Text("graphQlField") != null || Text("dataset") == null
                    ? await _datasetCatalog.DescribeDatasetAsync(principal, Text("dataset"), Text("graphQlField"), Text("language"), Bool("brief") ?? false, cancellationToken).ConfigureAwait(false)
                    : (Text("language") != null || Bool("brief") != null)
                    ? await _datasetCatalog.DescribeDatasetAsync(principal, Text("dataset") ?? string.Empty, Text("language"), Bool("brief") ?? false, cancellationToken).ConfigureAwait(false)
                    : await _datasetCatalog.DescribeDatasetAsync(principal, Text("dataset") ?? string.Empty, cancellationToken).ConfigureAwait(false),
                McpDatasetTools.DescribeMyAccess => Text("sqlName") != null
                    ? await _datasetCatalog.DescribeMyAccessAsync(principal, Text("dataset") ?? Text("table"), Text("graphQlField"), Text("sqlName"), cancellationToken).ConfigureAwait(false)
                    : Text("graphQlField") != null || (Text("dataset") == null && Text("table") == null)
                    ? await _datasetCatalog.DescribeMyAccessAsync(principal, Text("dataset") ?? Text("table"), Text("graphQlField"), cancellationToken).ConfigureAwait(false)
                    : await _datasetCatalog.DescribeMyAccessAsync(principal, Text("dataset") ?? Text("table") ?? string.Empty, cancellationToken).ConfigureAwait(false),
                McpDatasetTools.SampleRows => await _datasetCatalog.SampleRowsAsync(principal, Text("dataset") ?? string.Empty, ToCount(variables) is { } requestedCount ? McpRowCap.Effective(requestedCount, Math.Min(RowCap(), McpDatasetCatalog.MaxSampleRows)) : null, cancellationToken).ConfigureAwait(false),
                _ => await _datasetCatalog.SampleRowsAsync(principal, Text("dataset") ?? string.Empty, ToCount(variables), cancellationToken).ConfigureAwait(false)
            };

            if (describeNarrowing is not null && result is McpDatasetDescription described)
            {
                result = McpDescribeNarrowing.Apply(described, describeNarrowing);
            }

            // Issue #799: the brief describe and the compact list have their own, smaller wire shapes.
            result = McpCatalogWire.ForTool(result, compactList: string.Equals(Text("format"), "compact", StringComparison.OrdinalIgnoreCase));

            var serialized = JsonSerializer.Serialize(result, CamelCaseJsonOptions);
            return tool.Name.Equals(McpDatasetTools.SampleRows, StringComparison.OrdinalIgnoreCase)
                ? Capped(tool.Name, serialized, McpRowCap.TrimRows(serialized, Math.Min(RowCap(), McpDatasetCatalog.MaxSampleRows)))
                : serialized;
        }
        catch (Autheris.Application.Catalog.Discovery.DiscoveryStatusException ex)
        {
            return CreateDiscoveryErrorResult(sessionContext.TenantId, tool.Name, ex);
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
            // the argument message may quote a request value or an object name; it stays in the log.
            _logger.LogWarning(ex, "Dataset tool '{ToolName}' rejected its arguments.", tool.Name);
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, "The tool arguments are invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dataset tool '{ToolName}' failed.", tool.Name);
            if (SqlCompilerErrorMapper.TryMap(ex, out _))
            {
                return CreateMappedErrorResult(sessionContext.TenantId, tool.Name, ex);
            }

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
        // Only catalog data is reported. The summary carries no owner, so the field is omitted (never a fabricated
        // address), and tags come from the catalog record or are omitted.
        var assets = datasets.Select(d => new
        {
            tableName = d.Id,
            sensitivity = d.Sensitivity,
            classification = d.Sensitivity,
            tags = d.Tags is { Count: > 0 } ? d.Tags : null
        }).ToList();

        return JsonSerializer.Serialize(new { tenantId = sessionContext.TenantId, assets }, CamelCaseJsonOptions);
    }

    /// <summary>
    /// query_graphql: one read-only GraphQL query against the gateway schema. The catalog resolvers authorize every
    /// table with the caller's identity (consent, row filters, masking), as for POST /graphql.
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
            .SetServices(_services)
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

        var raw = FormatOperationResult(result);
        var json = Capped(tool.Name, raw, McpRowCap.TrimGraphQl(raw, RowCap()));
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

    /// <summary>The error result of a governed SQL error: the stable code and the fixed message of that code (plan section 28.4.3).</summary>
    internal static string CreateMappedErrorResult(string tenantId, string toolName, Exception exception)
    {
        SqlCompilerErrorMapper.TryMap(exception, out var mapped);
        SqlCompilerErrorMapper.RecordOutcome(mapped);
        return CreateErrorResult(tenantId, toolName, mapped.Code, mapped.Title);
    }

    /// <summary>
    /// M-17: Structured error result for MCP tool invocations (deny, execution failure, unavailable).
    /// </summary>
    internal static string CreateErrorResult(string tenantId, string toolName, string code, string message, string? graphQLResultJson = null, int? retryAfterSeconds = null)
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
            new McpToolError(code, message, retryAfterSeconds),
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
            // M-17: Do not swallow denials; report them as a structured error result.
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

    /// <summary>
    /// SG-09: Build authenticated ClaimsPrincipal from active MCP session preserving actual caller identity.
    /// Sets sub and NameIdentifier to the user identity (UserSid) while maintaining service principal in client_id and azp.
    /// SR-M-08: the claims are those of <see cref="McpProtocolHandler.BuildPrincipalFromSession"/> (the principal of every other MCP
    /// path): an app-only session (no user SID) keeps the application identity, carries the identity-kind marker and no group SIDs.
    /// Only <c>sub</c>, <c>client_id</c>/<c>azp</c> and the <c>McpAuth</c> scheme are added here.
    /// </summary>
    internal static ClaimsPrincipal BuildPrincipal(McpSessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);

        var effectiveUserId = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
            ? sessionContext.UserSid
            : sessionContext.ServicePrincipalId;

        var sessionPrincipal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext)!;
        var claims = new List<Claim>(sessionPrincipal.Claims);
        // SG-09: sub is always the session subject, never a token claim carried in the additional claims.
        claims.RemoveAll(c => c.Type == "sub");
        if (!string.IsNullOrWhiteSpace(effectiveUserId))
        {
            claims.Add(new Claim("sub", effectiveUserId));
        }

        if (!string.IsNullOrWhiteSpace(sessionContext.ServicePrincipalId) &&
            !string.Equals(sessionContext.ServicePrincipalId, effectiveUserId, StringComparison.Ordinal))
        {
            claims.RemoveAll(c => c.Type is "client_id" or "azp");
            claims.Add(new Claim("client_id", sessionContext.ServicePrincipalId));
            claims.Add(new Claim("azp", sessionContext.ServicePrincipalId));
        }

        // The claims are re-issued on a new identity (ClaimsIdentity clones them, issuers included, so the read-only marker survives).
        var identity = new ClaimsIdentity(claims, Autheris.Application.Identity.Attributes.CallerAttributeSchemeGate.McpAuthScheme);
        if (sessionContext.IsReadOnly)
        {
            Autheris.Domain.Security.TokenAccessScope.MarkReadOnly(identity);
        }
        return new ClaimsPrincipal(identity);
    }

    private async Task<string> ExecuteRequestAccessAsync(
        McpToolDefinition tool,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        Dictionary<string, object?> variables,
        CancellationToken ct)
    {
        if (sessionContext.IsReadOnly)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotFound, "Unknown tool.");
        }

        if (_accessRequests is null)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotAvailable, "Access requests are not available.");
        }

        string? Text(string name) => variables.TryGetValue(name, out var v) ? v as string : null;
        int? days = variables.TryGetValue("durationDays", out var d) ? d switch { int i => i, long l when l is >= 1 and <= int.MaxValue => (int)l, double x when x >= 1 && x <= int.MaxValue && x == Math.Floor(x) => (int)x, _ => 0 } : null;
        if (days is 0)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, PersistedToolValidator.InvalidArgumentsCode);
        }

        List<string>? clearColumns = null;
        if (variables.TryGetValue("clearColumns", out var cc) && cc is not null)
        {
            if (cc is not System.Collections.IEnumerable items || cc is string)
            {
                return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, PersistedToolValidator.InvalidArgumentsCode);
            }

            clearColumns = [];
            foreach (var item in items)
            {
                clearColumns.Add(item as string ?? string.Empty);
            }
        }

        if (!McpDatasetCatalog.TryParseDatasetId(Text("dataset"), out var id))
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.NotFound, "Dataset not found or not visible.");
        }

        try
        {
            var tenant = Autheris.Application.ConsentRequests.ConsentRequestService.ResolveTenantId(
                principal, new Autheris.Domain.Common.TenantId(sessionContext.TenantId), Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal));
            var result = await _accessRequests.RequestAsync(
                new Autheris.Application.Catalog.Discovery.DiscoveryAccessRequest(
                    id.Domain, id.Schema, id.TableName, Text("justification") ?? string.Empty, days, Text("duration"), clearColumns, null),
                new Autheris.Application.Catalog.Interfaces.CatalogCaller(principal, tenant, Autheris.Application.Catalog.Interfaces.CatalogChannel.Mcp),
                ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                dataset = id.ToString(),
                requestId = result.RequestId,
                status = result.Status,
                message = result.Message,
                clearColumns
            }, CamelCaseJsonOptions);
        }
        catch (Autheris.Application.ConsentRequests.ConsentRequestException ex)
        {
            var code = ex.Code switch
            {
                "NOT_FOUND" => McpErrorCodes.NotFound,
                "FORBIDDEN" => McpErrorCodes.Forbidden,
                "VALIDATION_ERROR" => McpErrorCodes.InvalidParams,
                _ => ex.Code
            };
            return CreateErrorResult(sessionContext.TenantId, tool.Name, code, ex.Message, retryAfterSeconds: ex.RetryAfterSeconds);
        }
        catch (Autheris.Application.Catalog.Discovery.DiscoveryStatusException ex)
        {
            return CreateDiscoveryErrorResult(sessionContext.TenantId, tool.Name, ex);
        }
    }

    internal static string CreateDiscoveryErrorResult(string tenantId, string toolName, Autheris.Application.Catalog.Discovery.DiscoveryStatusException ex)
    {
        var code = ex.Kind switch
        {
            Autheris.Application.Catalog.Discovery.DiscoveryFailureKind.Unauthenticated => "UNAUTHORIZED",
            Autheris.Application.Catalog.Discovery.DiscoveryFailureKind.Forbidden => McpErrorCodes.Forbidden,
            Autheris.Application.Catalog.Discovery.DiscoveryFailureKind.RateLimited => ex.Code,
            _ => McpErrorCodes.InvalidParams
        };
        return CreateErrorResult(tenantId, toolName, code, ex.Message, retryAfterSeconds: ex.RetryAfterSeconds);
    }
}

/// <summary>
/// M-17: Error codes for structured MCP tool error results.
/// </summary>
public static class McpErrorCodes
{
    public const string Forbidden = "FORBIDDEN";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string ExecutionFailed = "EXECUTION_FAILED";
    public const string NotAvailable = "NOT_AVAILABLE";
    public const string NotFound = "NOT_FOUND";
}

internal sealed record McpToolError(
    string Code,
    string Message,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? RetryAfterSeconds = null);

internal sealed record McpToolErrorPayload(
    string TenantId,
    string Tool,
    bool IsError,
    McpToolError Error,
    JsonElement? GraphQL);
