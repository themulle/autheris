using System.Collections.Immutable;
using System.Diagnostics;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autheris.Api.Middleware;

public sealed class ErrorSanitizingFilter : IErrorFilter
{
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ErrorSanitizingFilter> _logger;

    public ErrorSanitizingFilter(IHostEnvironment environment, ILogger<ErrorSanitizingFilter> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    private const string NotImplementedMessage = "The requested feature or data source capability is not implemented.";

    private static IError MapGatewayOutcome(IError error, string message, string code) =>
        error.WithExtensions(ImmutableDictionary<string, object?>.Empty).WithMessage(message).WithCode(code);

    private static readonly HashSet<string> WhitelistedSafeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FORBIDDEN",
        "ACCESS_DENIED",
        "UNAUTHORIZED",
        "NOT_FOUND",
        "RATE_LIMIT_EXCEEDED",
        "SCOPE_CONCURRENCY_EXCEEDED",
        "RATE_LIMIT",
        "TOO_MANY_REQUESTS",
        "QUERY_TOO_COMPLEX",
        "BAD_REQUEST",
        "INVALID_QUERY",
        "NOT_IMPLEMENTED",
        "ALREADY_PROCESSED",
        "VALIDATION_ERROR",
        "RESPONSE_TOO_LARGE",
        "TIMEOUT",
        "UNAVAILABLE",
        "COLUMN_NOT_RELEASED",
        "FILTER_ON_MASKED_COLUMN",
        "INVALID_QUERY_OPTION",
        "COST_LIMIT",
        "ROW_LIMIT",
        // fixed messages without ids or names; expected business outcomes, not server faults.
        "DECISION_NOT_PERMITTED",
        "ACCESS_REQUEST_NOT_PENDING",
        "READ_ONLY_TOKEN",
        // Issue #75 and #112 addenda
        "RELATION_KEY_MASKED",
        "CROSS_SOURCE_KEY_MASKED",
        "CROSS_SOURCE_RELATION_NOT_ENABLED",
        // #306: stable column-scoped code for a missing required filter column.
        "REQUIRED_FILTER_COLUMN_MISSING",
        "AGGREGATION_ON_MASKED_COLUMN",
        // #294/#314: fixed-message codes for engine validation, coercion and request-body errors.
        "GRAPHQL_VALIDATION_FAILED",
        "GRAPHQL_VARIABLE_INVALID",
        "GRAPHQL_PARSE_FAILED",
        "INVALID_JSON",
        "MISSING_REQUIRED_FIELD",
        // Typed aggregation errors (AggregationErrorCodes and the aggregation exception codes): fixed messages without table or column names.
        // Without these the Development branch passed them while every other environment masked them as INTERNAL_SERVER_ERROR.
        "AGGREGATION_ALIAS_INVALID",
        "AGGREGATION_ARGUMENT_INVALID",
        "AGGREGATION_INVALID_FUNCTION_FOR_TYPE",
        "AGGREGATION_LIMIT_EXCEEDED",
        "AGGREGATION_ORDER_INVALID",
        "AGGREGATION_RESULT_TYPE_MISMATCH",
        "AGGREGATION_COLUMN_UNAVAILABLE",
        "AGGREGATION_ROW_CAP_EXCEEDED",
        "AGGREGATION_DISABLED",
        "AGG_NEGATED_FILTER_REJECTED",
        "AGGREGATION_PRIVACY_RESTRICTED",
        "AGGREGATION_TIME_BUCKET_UNSUPPORTED",
        "AGGREGATION_TIME_BUCKET_LIMIT_EXCEEDED",
        "AGGREGATION_NOT_SUPPORTED",
        "UNKNOWN_METRIC",
        "METRIC_FILTER_INVALID",
        // ConsentRequestException codes the removed Development passthrough used to expose; fixed messages only.
        "INVALID_STATE",
        "CONSENT_ON_ATTRIBUTE_SOURCE",
        "CONSENT_NOT_EXTENDABLE",
        "INDIVIDUAL_GRANT_REQUIRES_EXPIRY",
        "INDIVIDUAL_GRANT_EXPIRY_IN_PAST",
        "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM",
        "INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES",
        "INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE",
        "INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN",
        "INDIVIDUAL_GRANT_DISABLED_FOR_REALM",
        "INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION",
        "INDIVIDUAL_GRANT_INDETERMINATE",
        // #341: stable codes the resolvers already raised that were masked as INTERNAL_SERVER_ERROR; fixed messages only.
        "SYNC_FAILED",
        "GDPR_SUBJECT_SCOPE_UNRESOLVED",
        // Relay keyset pagination
        "KEYSET_PAGINATION_UNAVAILABLE",
        // Issue #447: fixed text without the data source name.
        "TRANSPORT_NOT_ALLOWED",
        // Consent decision pipeline outcomes raised through GraphQL (unknown or foreign request, stale version, wrong channel); fixed text.
        "CONSENT_REQUEST_NOT_FOUND",
        "PRECONDITION_FAILED",
        "WORKFLOW_MODE_MISMATCH",
        "FOUR_EYES_SAME_APPROVER",
        "ROW_POLICY_EXPRESSION_NOT_ALLOWED",
        "GRANT_END_DATE_REQUIRED",
        "GRANT_DURATION_EXCEEDS_APPROVER_LIMIT",
        "JUSTIFICATION_TOO_SHORT",
        "JUSTIFICATION_TOO_LONG",
        "REQUEST_ALREADY_DECIDED",
        "SELF_GRANT_SECRET_REFUSED",
        "SELF_GRANT_EXCEEDS_CEILING",
        // #1009: expected refusals of the permission request engine (GraphQL requestTableAccess); fixed text, a hidden and an unknown target share one code.
        "TARGET_NOT_FOUND",
        "DUPLICATE_PENDING_REQUEST",
        "PENDING_REQUEST_LIMIT_EXCEEDED",
        "BUNDLE_LIMIT_EXCEEDED",
        "UNAUTHENTICATED",
        "INVALID_TENANT",
        "INVALID_COLUMNS",
        "COLUMN_METADATA_UNAVAILABLE"
    };

    /// <summary>
    /// the client text of every typed code is fixed here, never taken from the thrower, so a changed or
    /// verbose exception message (policy names, column names, privacy wording) cannot reach a client.
    /// </summary>
    private static readonly Dictionary<string, string> FixedClientMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KEYSET_PAGINATION_UNAVAILABLE"] = "Keyset pagination is unavailable for this table or caller.",
        ["TRANSPORT_NOT_ALLOWED"] = "Access to the data source is not allowed over this transport.",
        ["CONSENT_REQUEST_NOT_FOUND"] = "The consent request was not found.",
        ["PRECONDITION_FAILED"] = "The consent request was changed in the meantime.",
        ["WORKFLOW_MODE_MISMATCH"] = "The consent request cannot be decided through this channel.",
        ["FOUR_EYES_SAME_APPROVER"] = "A different approver is required for the next approval step.",
        ["GRANT_END_DATE_REQUIRED"] = "An explicit expiry date (validTo) is required.",
        ["GRANT_DURATION_EXCEEDS_APPROVER_LIMIT"] = "The requested grant duration exceeds the approver's maximum allowed lifetime.",
        ["JUSTIFICATION_TOO_SHORT"] = "A business justification of at least 10 characters is required.",
        ["JUSTIFICATION_TOO_LONG"] = "The business justification exceeds the maximum allowed length.",
        ["REQUEST_ALREADY_DECIDED"] = "This permission request has already been decided.",
        ["SELF_GRANT_SECRET_REFUSED"] = "Secret objects cannot be granted through self-grant.",
        ["SELF_GRANT_EXCEEDS_CEILING"] = "The target object protection class exceeds the self-grant ceiling.",
        ["TARGET_NOT_FOUND"] = "The requested target was not found.",
        ["DUPLICATE_PENDING_REQUEST"] = "An identical permission request is already pending.",
        ["PENDING_REQUEST_LIMIT_EXCEEDED"] = "The maximum number of pending permission requests has been reached.",
        ["BUNDLE_LIMIT_EXCEEDED"] = "The request bundle contains too many items.",
        ["UNAUTHENTICATED"] = "An authenticated subject is required.",
        ["INVALID_TENANT"] = "A valid tenant is required.",
        ["INVALID_COLUMNS"] = "One or more requested columns are invalid.",
        ["COLUMN_METADATA_UNAVAILABLE"] = "Column metadata is unavailable for a column-scoped request.",
        ["GRAPHQL_VALIDATION_FAILED"] = ValidationMessage,
        ["GRAPHQL_VARIABLE_INVALID"] = VariableMessage,
        ["GRAPHQL_PARSE_FAILED"] = ParseMessage,
        ["NOT_IMPLEMENTED"] = NotImplementedMessage,
        ["INVALID_JSON"] = InvalidJsonMessage,
        ["MISSING_REQUIRED_FIELD"] = MissingFieldMessage,
        ["REQUIRED_FILTER_COLUMN_MISSING"] = "A required filter column is missing.",
        ["AGGREGATION_ALIAS_INVALID"] = "The aggregate alias is not valid.",
        ["AGGREGATION_ARGUMENT_INVALID"] = "The aggregation arguments are not valid.",
        ["AGGREGATION_INVALID_FUNCTION_FOR_TYPE"] = "The aggregate function is not valid for the column type.",
        ["AGGREGATION_LIMIT_EXCEEDED"] = "The requested limit is above the allowed maximum.",
        ["AGGREGATION_ORDER_INVALID"] = "The aggregation ordering is not valid.",
        ["AGGREGATION_RESULT_TYPE_MISMATCH"] = "An aggregation result could not be converted to its declared type.",
        ["AGGREGATION_COLUMN_UNAVAILABLE"] = "The requested column is not available for aggregation.",
        ["AGGREGATION_ROW_CAP_EXCEEDED"] = "The aggregation covers more rows than the allowed limit; narrow the filter.",
        ["AGGREGATION_DISABLED"] = "Governed aggregation queries are disabled by configuration.",
        ["AGGREGATION_ON_MASKED_COLUMN"] = "A requested column is masked for the current caller and cannot be used in an aggregation.",
        ["FILTER_ON_MASKED_COLUMN"] = "Masked columns cannot be used in filters, joins, or ordering.",
        ["AGG_NEGATED_FILTER_REJECTED"] = "The aggregation filter is rejected by the privacy policy.",
        ["AGGREGATION_PRIVACY_RESTRICTED"] = "The requested aggregate is not available under the privacy policy.",
        ["AGGREGATION_TIME_BUCKET_UNSUPPORTED"] = "Time bucketing is not supported for this column.",
        ["AGGREGATION_TIME_BUCKET_LIMIT_EXCEEDED"] = "Time-bucketed aggregation queries cannot exceed a limit of 1000 groups. Lower limit to 1000 or less.",
        ["AGGREGATION_NOT_SUPPORTED"] = "The requested aggregation is not supported for this dataset.",
        ["UNKNOWN_METRIC"] = "The metric is not available.",
        ["METRIC_FILTER_INVALID"] = "The metric filter does not fit the dimension type.",
        ["INVALID_STATE"] = "The consent request is not in a state that allows this operation.",
        ["CONSENT_ON_ATTRIBUTE_SOURCE"] = "A consent on this table is not possible.",
        ["CONSENT_NOT_EXTENDABLE"] = "The consent cannot be extended. Create a new consent.",
        ["INDIVIDUAL_GRANT_REQUIRES_EXPIRY"] = "An expiry date is required for this grant.",
        ["INDIVIDUAL_GRANT_EXPIRY_IN_PAST"] = "The expiry date must be in the future.",
        ["INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM"] = "The expiry date exceeds the maximum allowed for this grant.",
        ["INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES"] = "This grant requires four-eyes approval.",
        ["INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE"] = "A grantee cannot renew their own grant.",
        ["INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN"] = "This grant can only be renewed through a recertification campaign.",
        ["INDIVIDUAL_GRANT_DISABLED_FOR_REALM"] = "Individual grants are disabled for this realm.",
        ["INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION"] = "A justification is required for this grant.",
        ["INDIVIDUAL_GRANT_INDETERMINATE"] = "The grant policy could not be evaluated.",
        ["SYNC_FAILED"] = "The synchronization failed.",
        ["GDPR_SUBJECT_SCOPE_UNRESOLVED"] = "The report scope cannot be resolved: name the table that holds the data subject's data.",
        ["ROW_POLICY_EXPRESSION_NOT_ALLOWED"] = "The statement uses an expression that is not permitted on a table with a row policy. Use non-raising forms such as TRY_CAST or NULLIF instead.",
        ["SCOPE_CONCURRENCY_EXCEEDED"] = "Too many concurrent queries are active for this client scope. Retry later."
    };

    internal const string MaskedCode = "MASKED";
    internal const string MaskedMessage = "A requested column is masked for the current caller.";
    internal const string ValidationCode = "GRAPHQL_VALIDATION_FAILED";
    internal const string ValidationMessage = "The GraphQL request is invalid.";
    internal const string ComplexMessage = "The query exceeds the allowed complexity.";
    internal const string DepthMessage = "The query exceeds the maximum allowed execution depth.";
    private const string VariableMessage = "A variable value is invalid.";
    private const string ParseMessage = "The GraphQL document could not be parsed.";
    private const string InvalidJsonMessage = "The request body is not valid JSON.";
    private const string MissingFieldMessage = "The request is missing a required field or has a field of the wrong type.";
    private const string InvalidQueryMessage = "The query cannot be processed.";
    private const string InternalMessage = "An internal server error occurred.";

    private static readonly HashSet<string> ExpectedOutcomeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DECISION_NOT_PERMITTED",
        "ACCESS_REQUEST_NOT_PENDING",
        "READ_ONLY_TOKEN",
        "AGGREGATION_TIME_BUCKET_UNSUPPORTED",
        "AGGREGATION_TIME_BUCKET_LIMIT_EXCEEDED",
        "ROW_POLICY_EXPRESSION_NOT_ALLOWED"
    };

    private static readonly string[] SensitivePatterns =
    [
        "password=", "pwd=", "server=", "uid=", "user id=", "connectionstring", "initial catalog=",
        "bearer ", "token=", "secret=", "client_secret", "apikey=", "api-key=", "api_key=", "signing_secret", "signingkey", "kek=",
        "stack trace:", "at system.", "at microsoft.", "at autheris."
    ];

    private static bool ContainsSensitivePatterns(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var lower = message.ToLowerInvariant();
        return SensitivePatterns.Any(pattern => lower.Contains(pattern, StringComparison.Ordinal));
    }

    public IError OnError(IError error)
    {
        var isDevelopment = _environment.IsDevelopment();

        if (error.Exception is Autheris.Domain.Exceptions.GatewaySecurityException secEx)
        {
            var code = secEx.ErrorCode;
            var message = secEx.Message;

            // Anti-enumeration oracle defense: a missing table is indistinguishable from a forbidden one in every environment.
            if (secEx is Autheris.Domain.Exceptions.TableNotFoundException)
            {
                code = "FORBIDDEN";
                message = "Access denied.";
            }

            error = error
                .WithMessage(message)
                .WithCode(code);
        }

        // #341: the governed SQL pipeline refused a table (unregistered, inactive, denied: one cause for the caller, like SQL_POLICY_DENIED on
        // the SQL and OData channels). It answers the GraphQL denial code with the fixed text, never the exception message.
        if (error.Exception is Autheris.Application.Sql.WebSqlTableDeniedException)
        {
            error = error
                .WithMessage("Access denied.")
                .WithCode("ACCESS_DENIED");
        }

        if (FindRowPolicyRejected(error.Exception) is { } rpe)
        {
            var rpeBuilder = ErrorBuilder.FromError(error)
                .SetMessage(FixedClientMessages[TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed])
                .SetCode(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed)
                .SetExtension("hint", rpe.Hint);
            error = rpeBuilder.Build();
        }

        // A1 (legacy table/finance path): gateway client/capability outcomes map by exception type only. Message, ErrorCode, inner
        // exceptions and extensions never reach the client. The page-range subclass is checked before its base type.
        // Only code-less errors (an unhandled resolver exception); an error that already carries a stable code keeps its own mapping.
        var gatewayOutcome = false;
        if (error.Code != null)
        {
            // keep the stable code and its fixed message (handled below)
        }
        else if (error.Exception is Autheris.Domain.Exceptions.GatewayPageRangeException)
        {
            gatewayOutcome = true;
            error = MapGatewayOutcome(error, Autheris.Domain.Exceptions.GatewayPageRangeException.FixedMessage, "INVALID_QUERY_OPTION");
        }
        else if (error.Exception is Autheris.Domain.Exceptions.GatewayInvalidQueryException)
        {
            gatewayOutcome = true;
            error = MapGatewayOutcome(error, "The query is invalid.", "INVALID_QUERY");
        }
        else if (error.Exception is Autheris.Domain.Exceptions.GatewayNotImplementedException)
        {
            gatewayOutcome = true;
            error = MapGatewayOutcome(error, NotImplementedMessage, "NOT_IMPLEMENTED");
        }

        // GQL-17: ReportMasked (CatalogGraphQlTypeModule) code is matched exactly (ordinal) so MASKED_COLUMN or "masked" stay masked.
        var isMaskedCode = string.Equals(error.Code, MaskedCode, StringComparison.Ordinal);
        if (isMaskedCode)
        {
            error = error.WithMessage(MaskedMessage);
        }
        else if (error.Code != null && FixedClientMessages.TryGetValue(error.Code, out var fixedMessage))
        {
            error = error.WithMessage(fixedMessage);
        }

        // Server-side logging keeps the full exception (never sent to the client).
        if (gatewayOutcome)
        {
            // A1-S2: type name and path only; the exception message carries caller column text.
            _logger.LogInformation("GraphQL gateway outcome {ExceptionType} at {Path}.", error.Exception!.GetType().Name, error.Path?.Print());
        }
        else if (error.Exception != null && error.Code != null && ExpectedOutcomeCodes.Contains(error.Code))
        {
            // A refused or lost decision is a normal outcome: no Error entry (the denial itself is audited by the resolver).
            _logger.LogInformation("GraphQL request refused [{Code}].", error.Code);
        }
        else if (error.Exception != null)
        {
            _logger.LogError(error.Exception, "GraphQL Execution Error [{Code}] trace {TraceId}", error.Code, Activity.Current?.TraceId.ToString());
        }

        var isWhitelisted = isMaskedCode || (error.Code != null && WhitelistedSafeCodes.Contains(error.Code));
        if (!isWhitelisted && Classify(error) is { } classified)
        {
            error = classified;
            isWhitelisted = true;
        }

        if (isDevelopment)
        {
            if (!isWhitelisted)
            {
                // Fixed message and code; the exception object is kept for debuggers, but its type and text are never written.
                error = error.WithMessage(InternalMessage).WithCode("INTERNAL_SERVER_ERROR");
            }

            return EnrichWithDevelopmentFixHints(error);
        }

        // Whitelisted client codes: retain safe message/code, but strictly strip internal exception details and sanitize sensitive text
        if (isWhitelisted)
        {
            var cleanError = error.WithException(null);

            // GQL-5: Outside Development, FORBIDDEN and ACCESS_DENIED messages must not disclose table names or denial reasons
            if (string.Equals(cleanError.Code, "FORBIDDEN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanError.Code, "ACCESS_DENIED", StringComparison.OrdinalIgnoreCase))
            {
                var builder = ErrorBuilder.FromError(cleanError)
                    .SetMessage("Access denied.");

                if (cleanError.Extensions != null)
                {
                    builder.RemoveExtension("table")
                           .RemoveExtension("deniedReasons")
                           .RemoveExtension("activeFailures");
                }

                return builder.Build();
            }

            // R-ERR-1: INVALID_QUERY messages name tables and columns; outside Development they stay generic so the
            // catalog cannot be enumerated (same text as GraphQlEnumerationShieldMiddleware).
            if (string.Equals(cleanError.Code, GraphQlEnumerationShieldMiddleware.Code, StringComparison.OrdinalIgnoreCase))
            {
                // #330: the page-range message is a server constant without table or column names; it is kept as is.
                if (GraphQlEnumerationShieldMiddleware.IsPageRangeError(cleanError))
                {
                    return cleanError;
                }

                return cleanError.WithMessage(GraphQlEnumerationShieldMiddleware.Message);
            }

            if (ContainsSensitivePatterns(cleanError.Message))
            {
                return cleanError.WithMessage("The request contains invalid parameters or cannot be processed.");
            }
            return cleanError;
        }

        // Non-whitelisted or unhandled technical exceptions: mask as generic INTERNAL_SERVER_ERROR
        return error
            .WithMessage(InternalMessage)
            .WithCode("INTERNAL_SERVER_ERROR")
            .WithException(null);
    }

    /// <summary>
    /// classifies a code-less or engine-coded error by exception/error type into a stable code with a fixed message.
    /// The engine text, extensions (type, field and argument names, HCnnnn) are dropped. Returns null when nothing matches
    /// (the caller then fails closed to the generic internal error).
    /// </summary>
    private static IError? Classify(IError error)
    {
        var (code, message) = ClassifyKind(error);
        if (code == null)
        {
            return null;
        }

        var builder = ErrorBuilder.New().SetMessage(message!).SetCode(code);
        if (error.Path != null)
        {
            builder.SetPath(error.Path);
        }

        if (error.Locations != null)
        {
            foreach (var location in error.Locations)
            {
                builder.AddLocation(location);
            }
        }

        return builder.Build();
    }

    private static (string? Code, string? Message) ClassifyKind(IError error)
    {
        for (var ex = error.Exception; ex != null; ex = ex.InnerException)
        {
            var typeName = ex.GetType().Name;
            if (typeName == "InvalidGraphQLRequestException")
            {
                return ex.Message.Contains("json", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("body", StringComparison.OrdinalIgnoreCase)
                    ? ("INVALID_JSON", InvalidJsonMessage)
                    : ("MISSING_REQUIRED_FIELD", MissingFieldMessage);
            }

            // Request-parse stage only: a JSON failure inside a resolver (error has a path) is an internal fault.
            if (ex is System.Text.Json.JsonException && error.Path == null)
            {
                return ("INVALID_JSON", InvalidJsonMessage);
            }

            // Input and variable coercion failures raised by HotChocolate's own coercion types. A bare FormatException or
            // OverflowException from application code is not assumed to be a client mistake (fails closed to internal).
            if (typeName is "InvalidFormatException" or "SerializationException")
            {
                return ("GRAPHQL_VARIABLE_INVALID", VariableMessage);
            }

            // HotChocolate 16 reports a variable value its scalar cannot coerce (an Int beyond Int32, JSON number into Int) as a
            // code-less error whose exception is a FormatException thrown below a HotChocolate scalar's CoerceInput* method. A FormatException
            // thrown by application code (resolver, service) has another throw site and stays an internal fault.
            if (ex is FormatException && error.Code == null && IsThrownByHotChocolate(ex))
            {
                return ("GRAPHQL_VARIABLE_INVALID", VariableMessage);
            }

            // Provider layer: a driver error for "LIKE or GLOB pattern too complex".
            if (ex is System.Data.Common.DbException && ex.Message.Contains("LIKE or GLOB", StringComparison.OrdinalIgnoreCase))
            {
                return ("INVALID_QUERY", InvalidQueryMessage);
            }

            if (ex is TimeoutException)
            {
                return ("TIMEOUT", "The query exceeded the maximum allowed execution duration.");
            }

            if (typeName == "SyntaxException")
            {
                return ("GRAPHQL_PARSE_FAILED", ParseMessage);
            }
        }

        // Engine validation and coercion errors carry no exception and either no code or an HCnnnn code. They are told apart by
        // code and extension keys (HotChocolate puts the variable name under a "variable" extension), not by message text;
        // everything else is a validation failure. Pinned by ErrorSanitizingFilterEngineErrorTests against the real engine.
        if (error.Exception == null && IsEngineOrMissingCode(error.Code))
        {
            if (error.Code == "HC0011")
            {
                return ("GRAPHQL_PARSE_FAILED", ParseMessage);
            }

            if (error.Extensions != null && error.Extensions.Keys.Any(k => k.Contains("variable", StringComparison.OrdinalIgnoreCase)))
            {
                return ("GRAPHQL_VARIABLE_INVALID", VariableMessage);
            }

            return ("GRAPHQL_VALIDATION_FAILED", ValidationMessage);
        }

        return (null, null);
    }

    private static bool IsThrownByHotChocolate(Exception ex)
    {
        // The throw site is System.Text.Json (JsonElement.GetInt32), called from a HotChocolate scalar's input coercion.
        foreach (var frame in new StackTrace(ex, false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType?.Assembly.GetName().Name is { } assembly
                && assembly.StartsWith("HotChocolate", StringComparison.Ordinal)
                && method.Name.Contains("CoerceInput", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEngineOrMissingCode(string? code) =>
        code == null || (code.Length == 6 && code.StartsWith("HC", StringComparison.Ordinal) && code.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0);

    private static TrinoSqlEngine.Ast.Security.RowPolicyExpressionRejectedException? FindRowPolicyRejected(Exception? ex)
    {
        for (int depth = 0; ex is not null && depth < 8; ex = ex.InnerException, depth++)
        {
            if (ex is TrinoSqlEngine.Ast.Security.RowPolicyExpressionRejectedException rpe)
            {
                return rpe;
            }
        }

        return null;
    }

    private static IError EnrichWithDevelopmentFixHints(IError error)
    {
        var code = error.Code?.ToUpperInvariant();
        if (code == "UNAUTHORIZED" || code == "AUTH_REQUIRED")
        {
            return error.SetExtension("dev_fix_hints", new[]
            {
                "Set headers 'X-Test-User-Sid: S-1-5-21-ALICE-FINANCE' & 'X-Test-Roles: FinanceManager'",
                "Or enable 'Gateway:Dev:Preset: Quickstart' in appsettings.Development.json",
                "Visit the developer dashboard at http://localhost:5000/ to copy predefined test personas"
            });
        }

        if (code == "FORBIDDEN" || code == "CONSENT_DENIED" || code == "ACCESS_DENIED")
        {
            return error.SetExtension("dev_fix_hints", new[]
            {
                "Request consent via GraphQL mutation 'requestConsent(domain: ..., tableName: ...)'",
                "Or enable 'Insecure:warn_auto_approve_access_requests: true' in appsettings.Development.json",
                "Or enable 'Insecure:danger_bypass_consent_checks: true' for unrestricted dev access"
            });
        }

        return error;
    }
}
