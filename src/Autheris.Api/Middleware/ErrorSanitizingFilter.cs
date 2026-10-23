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

    private static readonly HashSet<string> WhitelistedSafeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FORBIDDEN",
        "ACCESS_DENIED",
        "UNAUTHORIZED",
        "NOT_FOUND",
        "RATE_LIMIT_EXCEEDED",
        "RATE_LIMIT",
        "TOO_MANY_REQUESTS",
        "QUERY_TOO_COMPLEX",
        "BAD_REQUEST",
        "INVALID_QUERY",
        "ALREADY_PROCESSED",
        "VALIDATION_ERROR",
        "RESPONSE_TOO_LARGE",
        "TIMEOUT",
        "UNAVAILABLE",
        "COLUMN_NOT_RELEASED",
        "FILTER_ON_MASKED_COLUMN",
        "COST_LIMIT",
        "ROW_LIMIT",
        // Issue #107: fixed message without table or rule names (SEC-RF107-08).
        "ROW_FILTER_KEY_SET_UNAVAILABLE",
        // WP-0 (PLAN-GOV-113): fixed messages without ids or names; expected business outcomes, not server faults.
        "DECISION_NOT_PERMITTED",
        "ACCESS_REQUEST_NOT_PENDING",
        "READ_ONLY_TOKEN",
        // Issue #75 and #112 addenda
        "RELATION_KEY_MASKED",
        "CROSS_SOURCE_KEY_MASKED",
        "CROSS_SOURCE_RELATION_NOT_ENABLED",
        // #306 (SEC-PG-13): stable column-scoped code for a missing required filter column.
        "REQUIRED_FILTER_COLUMN_MISSING",
        "AGGREGATION_ON_MASKED_COLUMN",
        // #294/#314 (SEC-PG-12): fixed-message codes for engine validation, coercion and request-body errors.
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
        "UNKNOWN_METRIC",
        "METRIC_FILTER_INVALID",
        "PRIVACY_BUDGET_EXHAUSTED",
        // WP-D: ConsentRequestException codes the removed Development passthrough used to expose; fixed messages only.
        "ITSM_UNAVAILABLE",
        "INVALID_STATE",
        "CONSENT_ON_ATTRIBUTE_SOURCE",
        "INDIVIDUAL_GRANT_REQUIRES_EXPIRY",
        "INDIVIDUAL_GRANT_EXPIRY_IN_PAST",
        "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM",
        "INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES",
        "INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE",
        "INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN",
        "INDIVIDUAL_GRANT_DISABLED_FOR_REALM",
        "INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION",
        "INDIVIDUAL_GRANT_INDETERMINATE"
    };

    /// <summary>
    /// SEC-PG-12: the client text of every typed code is fixed here, never taken from the thrower, so a changed or
    /// verbose exception message (policy names, column names, privacy wording) cannot reach a client.
    /// </summary>
    private static readonly Dictionary<string, string> FixedClientMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GRAPHQL_VALIDATION_FAILED"] = ValidationMessage,
        ["GRAPHQL_VARIABLE_INVALID"] = VariableMessage,
        ["GRAPHQL_PARSE_FAILED"] = ParseMessage,
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
        ["AGG_NEGATED_FILTER_REJECTED"] = "The aggregation filter is rejected by the privacy policy.",
        ["UNKNOWN_METRIC"] = "The metric is not available.",
        ["METRIC_FILTER_INVALID"] = "The metric filter does not fit the dimension type.",
        ["PRIVACY_BUDGET_EXHAUSTED"] = "Privacy budget exhausted.",
        ["ITSM_UNAVAILABLE"] = "The external ticketing system is unavailable; the request was not created.",
        ["INVALID_STATE"] = "The consent request is not in a state that allows this operation.",
        ["CONSENT_ON_ATTRIBUTE_SOURCE"] = "A consent on this table is not possible.",
        ["INDIVIDUAL_GRANT_REQUIRES_EXPIRY"] = "An expiry date is required for this grant.",
        ["INDIVIDUAL_GRANT_EXPIRY_IN_PAST"] = "The expiry date must be in the future.",
        ["INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM"] = "The expiry date exceeds the maximum allowed for this grant.",
        ["INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES"] = "This grant requires four-eyes approval.",
        ["INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE"] = "A grantee cannot renew their own grant.",
        ["INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN"] = "This grant can only be renewed through a recertification campaign.",
        ["INDIVIDUAL_GRANT_DISABLED_FOR_REALM"] = "Individual grants are disabled for this realm.",
        ["INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION"] = "A justification is required for this grant.",
        ["INDIVIDUAL_GRANT_INDETERMINATE"] = "The grant policy could not be evaluated."
    };

    internal const string ValidationCode = "GRAPHQL_VALIDATION_FAILED";
    internal const string ValidationMessage = "The GraphQL request is invalid.";
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
        "READ_ONLY_TOKEN"
    };

    private static readonly string[] SensitivePatterns =
    [
        "password=", "pwd=", "server=", "uid=", "user id=", "connectionstring", "initial catalog=",
        "bearer ", "token=", "secret=", "client_secret",
        "stack trace:", "at system.", "at microsoft.", "at autheris."
    ];

    private static bool ContainsSensitivePatterns(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var lower = message.ToLowerInvariant();
        return SensitivePatterns.Any(pattern => lower.Contains(pattern));
    }

    public IError OnError(IError error)
    {
        var isDevelopment = _environment.IsDevelopment();

        if (error.Exception is Autheris.Domain.Exceptions.GatewaySecurityException secEx)
        {
            var code = secEx.ErrorCode;
            var message = secEx.Message;

            // Anti-enumeration oracle defense (SEC-PG-12): a missing table is indistinguishable from a forbidden one in every environment.
            if (secEx is Autheris.Domain.Exceptions.TableNotFoundException)
            {
                code = "FORBIDDEN";
                message = "Access denied.";
            }

            error = error
                .WithMessage(message)
                .WithCode(code);
        }

        if (error.Code != null && FixedClientMessages.TryGetValue(error.Code, out var fixedMessage))
        {
            error = error.WithMessage(fixedMessage);
        }

        // Server-side logging keeps the full exception (never sent to the client).
        if (error.Exception != null && error.Code != null && ExpectedOutcomeCodes.Contains(error.Code))
        {
            // A refused or lost decision is a normal outcome: no Error entry (the denial itself is audited by the resolver).
            _logger.LogInformation("GraphQL request refused [{Code}].", error.Code);
        }
        else if (error.Exception != null)
        {
            _logger.LogError(error.Exception, "GraphQL Execution Error [{Code}] trace {TraceId}", error.Code, Activity.Current?.TraceId.ToString());
        }

        var isWhitelisted = error.Code != null && WhitelistedSafeCodes.Contains(error.Code);
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
    /// SEC-PG-12: classifies a code-less or engine-coded error by exception/error type into a stable code with a fixed message.
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

            // Provider layer: Microsoft.Data.Sqlite raises SqliteException (SQLITE_ERROR) for "LIKE or GLOB pattern too complex".
            if (ex is System.Data.Common.DbException && ex.Message.Contains("LIKE or GLOB", StringComparison.OrdinalIgnoreCase))
            {
                return ("INVALID_QUERY", InvalidQueryMessage);
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
