namespace Autheris.Application.Mcp.Services;

using System.Collections.Frozen;
using System.Reflection;
using Autheris.Application.Analytics;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// The catalogue of error codes of MCP tool results. Every tool failure carries one of these codes (or a governed SQL code of
/// <see cref="GovernedSqlErrorCodes"/>) together with the fixed message of <see cref="FixedMessage"/>.
/// </summary>
public static class McpErrorCodes
{
    /// <summary>The caller may not use the requested data.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>An argument is missing, malformed or ambiguous.</summary>
    public const string InvalidParams = "INVALID_PARAMS";

    /// <summary>The tool failed for a reason that has no more specific code.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>A backing service is not configured or not ready.</summary>
    public const string NotAvailable = "NOT_AVAILABLE";

    /// <summary>A documentation item does not exist.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>The tool is unknown, disabled, hidden for the session or denied for the caller (one answer for all causes).</summary>
    public const string ToolNotAvailable = "TOOL_NOT_AVAILABLE";

    /// <summary>The dataset is unknown, invisible, inactive or cannot be classified (one answer for all causes).</summary>
    public const string DatasetNotFound = "DATASET_NOT_FOUND";

    /// <summary>The input guardrail refused the request.</summary>
    public const string RequestBlocked = "REQUEST_BLOCKED";

    /// <summary>An unexpected failure; details stay in the server log.</summary>
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>The result exceeds the per-call token budget.</summary>
    public const string TokenBudgetExceeded = "TOKEN_BUDGET_EXCEEDED";

    /// <summary>The call ran into a time limit.</summary>
    public const string QueryTimeout = "QUERY_TIMEOUT";

    /// <summary>The scope has too many concurrent queries.</summary>
    public const string ScopeConcurrencyExceeded = "SCOPE_CONCURRENCY_EXCEEDED";

    /// <summary>An administrator tool rejected the operation.</summary>
    public const string AdminOperationRejected = "ADMIN_OPERATION_REJECTED";

    /// <summary>The filter, projection or ordering of a dataset query is invalid.</summary>
    public const string InvalidQuery = "INVALID_QUERY";

    /// <summary>A filter column that the dataset requires was not constrained.</summary>
    public const string RequiredFilterColumnMissing = "REQUIRED_FILTER_COLUMN_MISSING";

    /// <summary>The metric is unknown or not visible (one answer for both).</summary>
    public const string MetricNotFound = "METRIC_NOT_FOUND";

    /// <summary>The parameters of a semantic metric query are invalid.</summary>
    public const string InvalidMetricQuery = "INVALID_METRIC_QUERY";

    /// <summary>The resource (procedure endpoint) is unknown or not visible.</summary>
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";

    /// <summary>A procedure refused the call for a business reason.</summary>
    public const string ProcedureRejected = "PROCEDURE_REJECTED";

    /// <summary>The GraphQL document of a query_graphql call cannot be parsed (the code /graphql answers).</summary>
    public const string GraphQlParseFailed = "GRAPHQL_PARSE_FAILED";

    /// <summary>The arguments of a tool are not valid JSON.</summary>
    public const string InvalidJson = "INVALID_JSON";

    /// <summary>A documentation store operation failed.</summary>
    public const string DocError = "DOC_ERROR";

    /// <summary>A deposited document is empty.</summary>
    public const string DocEmpty = "DOC_EMPTY";

    /// <summary>A deposited document exceeds the size limit.</summary>
    public const string DocTooLarge = "DOC_TOO_LARGE";

    /// <summary>The caller is not authenticated.</summary>
    public const string Unauthorized = "UNAUTHORIZED";

    /// <summary>An aggregation column is not available to the caller (the exception class keeps this code).</summary>
    public const string AggregationColumnUnavailable = "AGGREGATION_COLUMN_UNAVAILABLE";

    /// <summary>An aggregation exceeds the row cap (the exception class keeps this code).</summary>
    public const string AggregationRowCapExceeded = "AGGREGATION_ROW_CAP_EXCEEDED";

    /// <summary>Quota or rate limit reached.</summary>
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";

    /// <summary>Generic fixed message for every code without a dedicated text.</summary>
    /// <summary>The data source does not admit the MCP transport (same code and text as the HTTP surfaces).</summary>
    public const string TransportNotAllowed = Autheris.Domain.Exceptions.TransportNotAllowedException.Code;

    internal const string DefaultMessage = "Tool execution failed.";

    // The GraphQL query simulator reads the same datasets as query_graphql and is answered alike.
    private const string SimulateQueryTool = "simulate_query";

    private static readonly FrozenSet<string> Catalogue = BuildCatalogue();

    /// <summary>Whether a code belongs to the catalogue (MCP codes and governed SQL codes).</summary>
    public static bool IsCatalogueCode(string? code) => code is not null && Catalogue.Contains(code);

    /// <summary>The fixed, value-free message of a code.</summary>
    public static string FixedMessage(string? code) => code switch
    {
        ToolNotAvailable => "The tool is not available.",
        DatasetNotFound => "Dataset not found or not visible.",
        RequestBlocked => "The request was blocked by the input guardrail.",
        InternalError => "An internal error occurred.",
        TokenBudgetExceeded => "The result exceeds the per-call token budget.",
        QueryTimeout => "The query timed out.",
        ScopeConcurrencyExceeded => "Too many concurrent queries for this scope. Retry later.",
        AdminOperationRejected => "The admin operation was rejected.",
        InvalidQuery => "The query parameters are invalid.",
        RequiredFilterColumnMissing => "A required filter column was not constrained by the query.",
        MetricNotFound => "One or more requested metrics were not found in the semantic catalog.",
        InvalidMetricQuery => "The semantic metric query parameters are invalid.",
        ResourceNotFound => "Procedure endpoint not found.",
        ProcedureRejected => "The procedure rejected the request.",
        InvalidParams => "The request arguments are invalid.",
        NotAvailable => "The requested service is not available.",
        NotFound => "The requested item was not found.",
        Forbidden => "Access denied by data governance policy.",
        RateLimitExceeded => "Query cost quota exhausted.",
        GraphQlParseFailed => "The GraphQL document could not be parsed.",
        InvalidJson => "Arguments must be valid JSON.",
        DocError => "The documentation operation failed.",
        DocEmpty => "The document is empty.",
        DocTooLarge => "The document exceeds the size limit.",
        Unauthorized => "Authentication is required.",
        TransportNotAllowed => "Access to the data source is not allowed over this transport.",
        ExecutionFailed => DefaultMessage,
        AggregationColumnUnavailable => "An aggregation column is not available.",
        AggregationRowCapExceeded => "The aggregation exceeds the row cap.",
        not null when IsAggregationCode(code) => "The aggregation request was rejected.",
        not null when IsGovernedSqlCode(code) => GovernedSqlErrorCodes.FixedMessage(code),
        _ => DefaultMessage
    };

    /// <summary>
    /// The code that answers every kind of denied or unresolvable target of a tool, so that "unknown" and "not allowed" are one
    /// answer (query_sql: the governed SQL policy code; query_metrics: the metric code; call_procedure: the resource code; dataset
    /// tools: the dataset code; every other tool: the tool code).
    /// </summary>
    public static string TargetDeniedCode(string? toolName)
    {
        if (string.Equals(toolName, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase)) return GovernedSqlErrorCodes.PolicyDenied;
        if (string.Equals(toolName, McpSemanticTools.QueryMetrics, StringComparison.OrdinalIgnoreCase)) return MetricNotFound;
        if (string.Equals(toolName, McpDatasetTools.CallProcedure, StringComparison.OrdinalIgnoreCase)) return ResourceNotFound;
        return McpDatasetTools.IsDatasetTool(toolName) || string.Equals(toolName, SimulateQueryTool, StringComparison.OrdinalIgnoreCase) ? DatasetNotFound : ToolNotAvailable;
    }

    /// <summary>A sentence for a tool description that names the error codes the caller can act on.</summary>
    public static string ErrorsSentence(params string[] codes) => " Errors (see the query guide): " + string.Join(", ", codes) + ".";

    /// <summary>The codes an agent can act on, each with a one-line action.</summary>
    public static IReadOnlyList<(string Code, string Action)> AgentActionable { get; } =
    [
        (ToolNotAvailable, "Do not retry; use one of the tools the server lists."),
        (DatasetNotFound, "Check the dataset id against the dataset catalog; do not retry with the same id."),
        (InvalidParams, "Fix the arguments and call again."),
        (GovernedSqlErrorCodes.SyntaxError, "Correct the statement at the given line and column."),
        (GovernedSqlErrorCodes.PolicyDenied, "Use only tables the dataset catalog lists for you."),
        (GovernedSqlErrorCodes.PagingTokenInvalid, "Repeat the call without 'next' to restart; a 'next' is valid only with the same sql, parameters and dataSource."),
        (InvalidQuery, "Fix the filter, select or orderBy and call again."),
        (RequiredFilterColumnMissing, "Add a filter on the required column."),
        (MetricNotFound, "Use a metric name returned by the semantic catalog."),
        (TokenBudgetExceeded, "Narrow the request (fewer columns, a filter or a smaller limit)."),
        (QueryTimeout, "Narrow the request or retry later."),
        (RateLimitExceeded, "Retry after retryAfterSeconds."),
        (ScopeConcurrencyExceeded, "Retry after retryAfterSeconds."),
        (RequestBlocked, "Rephrase the request without instructions aimed at the assistant."),
        (InternalError, "Retry later; report the problem to an operator if it persists.")
    ];

    private static readonly FrozenSet<string> GovernedSqlCodes = typeof(GovernedSqlErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.GetRawConstantValue() is string)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AggregationCodes = typeof(AggregationErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.GetRawConstantValue() is string)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToFrozenSet(StringComparer.Ordinal);

    private static bool IsAggregationCode(string code) => AggregationCodes.Contains(code) && !GovernedSqlCodes.Contains(code);

    private static bool IsGovernedSqlCode(string code) => GovernedSqlCodes.Contains(code);

    /// <summary>Whether a code is one of the governed SQL codes (their mapper hints are server-generated).</summary>
    internal static bool IsGovernedSql(string? code) => code is not null && GovernedSqlCodes.Contains(code);

    private static FrozenSet<string> BuildCatalogue()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(McpErrorCodes), typeof(GovernedSqlErrorCodes), typeof(AggregationErrorCodes) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.FieldType == typeof(string) && field.GetRawConstantValue() is string value)
                {
                    set.Add(value);
                }
            }
        }

        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}
