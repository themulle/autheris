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
        "TOO_MANY_REQUESTS",
        "QUERY_TOO_COMPLEX",
        "BAD_REQUEST",
        "INVALID_QUERY",
        "ALREADY_PROCESSED",
        "VALIDATION_ERROR",
        "RESPONSE_TOO_LARGE",
        "TIMEOUT",
        "UNAVAILABLE"
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
        if (error.Exception is Autheris.Domain.Exceptions.GatewaySecurityException secEx)
        {
            var code = secEx.ErrorCode;
            var message = secEx.Message;

            // Anti-enumeration oracle defense in non-development: Mask NOT_FOUND as unified FORBIDDEN
            if (!_environment.IsDevelopment() && secEx is Autheris.Domain.Exceptions.TableNotFoundException notFoundEx)
            {
                code = "FORBIDDEN";
                message = $"Access denied to table '{notFoundEx.Table}'.";
            }

            error = error
                .WithMessage(message)
                .WithCode(code);
        }

        if (_environment.IsDevelopment())
        {
            if (error.Exception != null)
            {
                error = error.WithMessage($"{error.Exception.GetType().Name}: {error.Exception.Message}");
            }

            return EnrichWithDevelopmentFixHints(error);
        }

        // In non-development (Staging, QA, Production):
        if (error.Exception != null)
        {
            _logger.LogError(error.Exception, "GraphQL Execution Error [{Code}]: {Message}", error.Code, error.Message);
        }

        // Whitelisted client codes: retain safe message/code, but strictly strip internal exception details and sanitize sensitive text
        if (error.Code != null && WhitelistedSafeCodes.Contains(error.Code))
        {
            var cleanError = error.WithException(null);

            // GQL-5: Outside Development, FORBIDDEN and ACCESS_DENIED messages must not disclose table names or denial reasons
            if (!_environment.IsDevelopment() &&
                (string.Equals(cleanError.Code, "FORBIDDEN", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(cleanError.Code, "ACCESS_DENIED", StringComparison.OrdinalIgnoreCase)))
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
            .WithMessage("An internal server error occurred.")
            .WithCode("INTERNAL_SERVER_ERROR")
            .WithException(null);
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
