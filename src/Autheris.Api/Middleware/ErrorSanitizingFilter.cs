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
            if (ContainsSensitivePatterns(cleanError.Message))
            {
                return cleanError.WithMessage("Die Anfrage enthält ungültige Parameter oder kann nicht verarbeitet werden.");
            }
            return cleanError;
        }

        // Non-whitelisted or unhandled technical exceptions: mask as generic INTERNAL_SERVER_ERROR
        return error
            .WithMessage("Ein interner Serverfehler ist aufgetreten.")
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
                "Header 'X-Test-User-Sid: S-1-5-21-ALICE-FINANCE' & 'X-Test-Roles: FinanceManager' setzen",
                "Oder 'Gateway:Dev:Preset: Quickstart' in appsettings.Development.json aktivieren",
                "Besuche das Developer Dashboard auf http://localhost:5000/ zum Kopieren vorgefertigter Test-Personas"
            });
        }

        if (code == "FORBIDDEN" || code == "CONSENT_DENIED" || code == "ACCESS_DENIED")
        {
            return error.SetExtension("dev_fix_hints", new[]
            {
                "Consent anfragen via GraphQL Mutation 'requestConsent(domain: ..., tableName: ...)'",
                "Oder 'Insecure:warn_auto_approve_access_requests: true' in appsettings.Development.json aktivieren",
                "Oder 'Insecure:danger_bypass_consent_checks: true' für unbeschränkten Dev-Zugriff aktivieren"
            });
        }

        return error;
    }
}
