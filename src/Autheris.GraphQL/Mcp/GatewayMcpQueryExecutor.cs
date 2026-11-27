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
using Autheris.GraphQL.Security;
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
    private readonly Autheris.Application.Common.Quota.IScopeQueryExecutionGuard _scopeGuard;

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

    private int RowCap()
    {
        var cap = McpRowCap.ResolveCap(_options.CurrentValue.Mcp.MaxResultRows);
        return Autheris.Application.Common.Quota.ScopeQueryExecutionGuard.TryGetCurrent(out var scopeLease)
            ? scopeLease.ClampRows(cap)
            : cap;
    }

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
        Autheris.Application.Catalog.Discovery.IDiscoveryAccessRequestService? accessRequests = null,
        Autheris.Application.Common.Quota.IScopeQueryExecutionGuard? scopeGuard = null)
    {
        _scopeGuard = scopeGuard ?? Autheris.Application.Common.Quota.NullScopeQueryExecutionGuard.Instance;
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

        var principal = GraphQlCallerPrincipal.BuildMcpSessionPrincipal(sessionContext);

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
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, "The row limit argument must be a positive integer.");
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

        try
        {
            return await _scopeGuard.RunAsync(principal, apiKey: null, sessionContext.ClientIp, (_, scopedCt) =>
                ExecuteOperationCoreAsync(tool, principal, sessionContext, variables, argumentsJson, scopedCt), cancellationToken).ConfigureAwait(false);
        }
        catch (Autheris.Domain.Exceptions.ScopeConcurrencyExceededException ex)
        {
            return CreateErrorResult(
                sessionContext.TenantId,
                tool.Name,
                McpErrorCodes.ScopeConcurrencyExceeded,
                McpErrorCodes.FixedMessage(McpErrorCodes.ScopeConcurrencyExceeded),
                retryAfterSeconds: ex.RetryAfterSeconds);
        }
        catch (TimeoutException)
        {
            return CreateErrorResult(
                sessionContext.TenantId,
                tool.Name,
                "QUERY_TIMEOUT",
                "The query exceeded the maximum allowed execution duration.");
        }
    }

    private async Task<string> ExecuteOperationCoreAsync(
        McpToolDefinition tool,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        Dictionary<string, object?> variables,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        // Hybrid tools: SQL, dataset REST, catalog discovery, permissions, datasources, lineage, dynamic API
        if (_toolExecutionHandler != null && _toolExecutionHandler.CanHandle(tool.Name))
        {
            try
            {
                var handled = await _toolExecutionHandler.ExecuteToolAsync(tool, argumentsJson, sessionContext, cancellationToken).ConfigureAwait(false);
                // query_sql pages before execution (page size capped, continuation stays valid), so it has no post-execution trim.
                return string.Equals(tool.Name, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase)
                    ? handled
                    : Capped(tool.Name, handled, McpRowCap.TrimRows(handled, RowCap()));
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

        // 4b.2: Query Data Catalog Metadata using dataset catalog
        if (tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteQueryDataCatalogAsync(tool, principal, sessionContext, variables, cancellationToken).ConfigureAwait(false);
        }

        // Fast-path / Specialized execution for registered tables if tool declares TargetTable
        if (tool.TargetTable is { } targetTable)
        {
            var fastPathResult = await TryExecuteTableFastPathAsync(tool.Name, targetTable, principal, sessionContext, variables, cancellationToken).ConfigureAwait(false);
            if (fastPathResult != null)
            {
                return await EnrichWithProvenanceAsync(tool, fastPathResult, cancellationToken).ConfigureAwait(false);
            }
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
                var executor = await _executorProvider.GetExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var requestBuilder = OperationRequestBuilder.New()
                    .SetDocument(tool.TargetGraphQLOperation)
                    .SetServices(_services);
                GraphQlCallerPrincipal.SetMcpSessionState(requestBuilder, principal, sessionContext);

                if (variables.Count > 0)
                {
                    requestBuilder.SetVariableValues(variables);
                }

                var executionResult = await executor.ExecuteAsync(requestBuilder.Build(), cancellationToken).ConfigureAwait(false);
                if (executionResult is OperationResult op)
                {
                    McpResultMasking.Report(HasMaskedFields(op.Extensions));
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
            catch (TimeoutException)
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
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d when !double.IsNaN(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
            _ => null
        } : null;
        bool? Bool(string name) => variables.TryGetValue(name, out var v) ? McpDatasetTools.ToBooleanArgument(v) : null;

        try
        {
            McpDescribeNarrowing.Request? describeNarrowing = null;
            if (string.Equals(tool.Name, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase))
            {
                var columnOffset = Number("columnOffset");
                var columnLimit = Number("columnLimit");
                if ((variables.GetValueOrDefault("columnOffset") is not null && columnOffset is null) ||
                    (variables.GetValueOrDefault("columnLimit") is not null && columnLimit is null) ||
                    !McpDescribeNarrowing.TryParse(
                        variables.GetValueOrDefault("include"),
                        variables.GetValueOrDefault("columns"),
                        columnOffset,
                        columnLimit,
                        variables.GetValueOrDefault("relations"),
                        out var parsedNarrowing))
                {
                    return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.InvalidParams, PersistedToolValidator.InvalidArgumentsCode);
                }

                if ((Bool("brief") ?? false) && parsedNarrowing.ConflictsWithBrief)
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

            McpDatasetDescription? preNarrowed = null;
            if (string.Equals(tool.Name, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase) &&
                describeNarrowing is { IsEmpty: false })
            {
                preNarrowed = await _datasetCatalog.DescribeDatasetAsync(
                    principal,
                    Text("dataset"),
                    Text("graphQlField"),
                    Text("sqlName"),
                    Text("language"),
                    Bool("brief") ?? false,
                    describeNarrowing,
                    cancellationToken).ConfigureAwait(false);
            }

            object result = preNarrowed is not null
                ? preNarrowed
                : tool.Name.ToLowerInvariant() switch
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

            if (preNarrowed is null && describeNarrowing is not null && result is McpDatasetDescription described)
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
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.DatasetNotFound, McpErrorCodes.FixedMessage(McpErrorCodes.DatasetNotFound));
        }
        catch (GatewayForbiddenException ex)
        {
            _logger.LogWarning(ex, "MCP tool '{ToolName}' was denied by governance policy.", tool.Name);
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.DatasetNotFound, McpErrorCodes.FixedMessage(McpErrorCodes.DatasetNotFound));
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
        catch (TimeoutException)
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
        var datasetFacts = false;
        using (var args = JsonDocument.Parse(argumentsJson!))
        {
            foreach (var property in args.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "datasetFacts", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    {
                        return Invalid("'datasetFacts' must be a boolean.");
                    }

                    datasetFacts = property.Value.ValueKind == JsonValueKind.True;
                    continue;
                }

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
            _logger.LogWarning(ex, "GraphQL document of tool '{ToolName}' could not be parsed.", tool.Name);
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.GraphQlParseFailed, McpErrorCodes.FixedMessage(McpErrorCodes.GraphQlParseFailed));
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

        var executor = await _executorProvider.GetExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var requestBuilder = OperationRequestBuilder.New()
            .SetDocument(document)
            .SetServices(_services);
        GraphQlCallerPrincipal.SetMcpSessionState(requestBuilder, principal, sessionContext);
        if (variableValues != null)
        {
            requestBuilder.SetVariableValues(variableValues);
        }

        if (datasetFacts)
        {
            requestBuilder.SetExtensions(new Dictionary<string, object?>
            {
                ["autheris"] = new Dictionary<string, object?> { ["datasetFacts"] = true }
            });
        }

        var executionResult = await executor.ExecuteAsync(requestBuilder.Build(), cancellationToken).ConfigureAwait(false);
        if (executionResult is not OperationResult result)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ExecutionFailed, "Unsupported GraphQL execution result.");
        }

        McpResultMasking.Report(HasMaskedFields(result.Extensions));
        var raw = FormatOperationResult(result);

        // The body is what POST /graphql answers. Errors next to data are a normal result; without data the failure is the envelope.
        return result.Errors is not { Count: > 0 } || result.Data is { IsValueNull: false } ? raw : McpToolError.SerializeGraphQl(raw);
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
        return McpToolError.Serialize(McpToolError.FromSql(exception, mapped));
    }

    /// <summary>
    /// Structured error result for MCP tool invocations (deny, execution failure, unavailable). The envelope carries neither the tenant nor
    /// the tool name, so <paramref name="tenantId"/> and <paramref name="toolName"/> are not written.
    /// </summary>
    internal static string CreateErrorResult(string tenantId, string toolName, string code, string message, string? graphQLResultJson = null, int? retryAfterSeconds = null, string? hint = null)
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

        return McpToolError.Serialize(new McpToolErrorBody(code, message) { RetryAfterSeconds = retryAfterSeconds, Hint = hint, GraphQL = graphQL });
    }

    private async Task<string?> TryExecuteTableFastPathAsync(
        string toolName,
        TableIdentifier tableId,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        Dictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
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

            McpResultMasking.Report(McpResultMasking.HasMaskedValues(decision, null, rows));
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

    /// <summary>True when the GraphQL result carries the masked-fields extension, i.e. a column resolver returned a masked value.</summary>
    internal static bool HasMaskedFields(IReadOnlyDictionary<string, object?>? extensions) =>
        extensions is not null && extensions.ContainsKey(Interceptors.MaskedFieldsMiddleware.ExtensionKey);

    private static string FormatOperationResult(OperationResult op)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        HotChocolate.Transport.Formatters.JsonResultFormatter.Default.Format(op, writer);
        return System.Text.Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    internal static ClaimsPrincipal BuildPrincipal(McpSessionContext sessionContext) =>
        GraphQlCallerPrincipal.BuildMcpSessionPrincipal(sessionContext);

    private async Task<string> ExecuteRequestAccessAsync(
        McpToolDefinition tool,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext,
        Dictionary<string, object?> variables,
        CancellationToken ct)
    {
        if (sessionContext.IsReadOnly)
        {
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.ToolNotAvailable, McpErrorCodes.FixedMessage(McpErrorCodes.ToolNotAvailable));
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
            return CreateErrorResult(sessionContext.TenantId, tool.Name, McpErrorCodes.DatasetNotFound, McpErrorCodes.FixedMessage(McpErrorCodes.DatasetNotFound));
        }

        try
        {
            var tenant = Autheris.Application.ConsentRequests.ConsentTenantResolver.Resolve(
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
            // The code only; the exception text (it can quote the request) stays out of the result.
            var code = ex.Code switch
            {
                "NOT_FOUND" => McpErrorCodes.DatasetNotFound,
                "FORBIDDEN" => McpErrorCodes.Forbidden,
                "VALIDATION_ERROR" => McpErrorCodes.InvalidParams,
                _ => McpErrorCodes.IsCatalogueCode(ex.Code) ? ex.Code : McpErrorCodes.ExecutionFailed
            };
            return CreateErrorResult(sessionContext.TenantId, tool.Name, code, McpErrorCodes.FixedMessage(code), retryAfterSeconds: ex.RetryAfterSeconds);
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
            Autheris.Application.Catalog.Discovery.DiscoveryFailureKind.Unauthenticated => McpErrorCodes.Unauthorized,
            Autheris.Application.Catalog.Discovery.DiscoveryFailureKind.Forbidden => McpErrorCodes.Forbidden,
            Autheris.Application.Catalog.Discovery.DiscoveryFailureKind.RateLimited => McpErrorCodes.RateLimitExceeded,
            _ => McpErrorCodes.InvalidParams
        };
        return CreateErrorResult(tenantId, toolName, code, McpErrorCodes.FixedMessage(code), retryAfterSeconds: ex.RetryAfterSeconds);
    }
}
