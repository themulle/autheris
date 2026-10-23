using System.Collections.Immutable;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

namespace Autheris.Api.Middleware;

/// <summary>
/// R-GQL-6 / R-ERR-1: Outside Development an unknown field, a denied table and a denied column must not be
/// distinguishable. HotChocolate 16 reports unknown fields as document validation errors, which never reach an
/// <see cref="IErrorFilter"/>, and without data; denied tables and columns are resolver errors with partial data.
/// This request middleware wraps validation and execution and replaces every such result with one identical,
/// generic error result (same body, same HTTP status 400).
/// </summary>
public sealed class GraphQlEnumerationShieldMiddleware
{
    internal const string Code = "INVALID_QUERY";
    internal const string Message = "The query is invalid or references fields that are not available.";

    /// <summary>Error codes raised by the catalog resolvers for denied tables and unknown or denied columns.</summary>
    private static readonly HashSet<string> CatalogAccessCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESS_DENIED",
        "INVALID_QUERY"
    };

    /// <summary>Validation errors with these codes do not depend on the catalog and keep their own message.</summary>
    private static readonly HashSet<string> NeutralValidationCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // QueryCostAnalyzerRule: depends on the document shape and configured budgets only, never on field existence.
        "QUERY_TOO_COMPLEX"
    };

    /// <summary>Stable code of the execution depth rule; its message is fixed and carries no schema detail.</summary>
    internal const string DepthCode = "QUERY_TOO_DEEP";

    /// <summary>
    /// HotChocolate's max-execution-depth rule reports a code-less error with these extension keys; the depth depends on the
    /// document shape only, so it is told apart by the keys and not by message text.
    /// </summary>
    private static bool IsDepthError(IError error) =>
        error.Code is null && error.Extensions is { } extensions && extensions.ContainsKey("detectedExecutionDepth");

    private static bool IsNeutral(IError error) =>
        IsDepthError(error) || (error.Code is not null && NeutralValidationCodes.Contains(error.Code));

    private readonly RequestDelegate _next;

    public GraphQlEnumerationShieldMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        await _next(context).ConfigureAwait(false);

        if (context.Result is not OperationResult result || result.Errors is not { Count: > 0 } errors)
        {
            return;
        }

        // Unknown environment counts as production (fail-closed).
        var environment = context.RequestServices.GetService<IHostEnvironment>();
        bool isDevelopment = environment?.IsDevelopment() == true;
        bool validationFailed = result.ContextData.ContainsKey(ExecutionContextData.ValidationErrors);

        // SEC-PG-12 (#294): an invalid document answers the same single error in every environment (stable classified code,
        // fixed message, no locations, path or extensions), whether or not the named field or type exists. Denied tables and
        // columns (resolver errors) get that same error outside Development; Development keeps their detail for debugging.
        bool mustShield = validationFailed
            ? errors.Any(e => !IsNeutral(e))
            : !isDevelopment && errors.Any(e => e.Code is not null && CatalogAccessCodes.Contains(e.Code));

        if (!mustShield)
        {
            if (validationFailed && errors.Any(IsDepthError))
            {
                // Depth and cost violations keep a stable code; the engine text and its depth numbers are replaced by a fixed message.
                var rewritten = errors.Select(e => IsDepthError(e)
                    ? ErrorBuilder.New().SetMessage(ErrorSanitizingFilter.DepthMessage).SetCode(DepthCode).Build()
                    : e).ToImmutableList();
                var data = result.ContextData;
                await result.DisposeAsync().ConfigureAwait(false);
                context.Result = new OperationResult(rewritten) { ContextData = data };
            }

            return;
        }

        var code = ErrorSanitizingFilter.ValidationCode;
        var message = ErrorSanitizingFilter.ValidationMessage;
        if (validationFailed && context.RequestServices.GetService<ErrorSanitizingFilter>() is { } filter)
        {
            var classified = filter.OnError(errors[0]);
            if (classified.Code is ErrorSanitizingFilter.ValidationCode or "GRAPHQL_PARSE_FAILED" or "GRAPHQL_VARIABLE_INVALID" && classified.Message is { } text)
            {
                code = classified.Code;
                message = text;
            }
        }

        await result.DisposeAsync().ConfigureAwait(false);
        context.Result = new OperationResult(ImmutableList.Create(
            ErrorBuilder.New().SetMessage(message).SetCode(code).Build()))
        {
            ContextData = ImmutableDictionary<string, object?>.Empty.Add(ExecutionContextData.ValidationErrors, true)
        };
    }
}
