namespace Autheris.Api.Extensions.DependencyInjection.StartupChecks;

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

/// <summary>Validates <c>Gateway:GraphQL:TableRootsOnlyScopes</c> and reports the active restriction at startup.</summary>
internal static class GraphQlTableRootsOnlyScopesStartup
{
    internal static void ValidateAndReport(GatewayOptions options, ILogger? logger)
    {
        var scopes = options.GraphQL.TableRootsOnlyScopes;
        if (scopes is null || scopes.Count == 0)
        {
            return;
        }

        // An entry that can never match a single token scope would silently disable the restriction: fail closed at startup.
        foreach (var entry in scopes)
        {
            if (string.IsNullOrWhiteSpace(entry) || entry.Any(char.IsWhiteSpace) || entry.EndsWith('/', StringComparison.Ordinal) || ScopeNames.ToBareName(entry).Length == 0)
            {
                throw new ValidationException(
                    "Configuration error: Gateway:GraphQL:TableRootsOnlyScopes entries must be single non-empty scope names without whitespace.");
            }
        }

        var names = string.Join(", ", scopes);
        Report(logger, LogLevel.Information, $"GraphQL table-roots-only scopes active: {names}");
        if (options.GraphQL.EnableIntrospection || options.IsIntrospectionForced)
        {
            Report(logger, LogLevel.Warning,
                "GraphQL table-roots-only scopes are configured while introspection is enabled: the schema (introspection and SDL download) stays reachable for every caller outside GraphQL root fields.");
        }
    }

    private static void Report(ILogger? logger, LogLevel level, string message)
    {
        if (logger is not null)
        {
            logger.Log(level, "{Message}", message);
        }
        else
        {
            Console.WriteLine($"[Autheris] {(level == LogLevel.Warning ? "WARNING" : "INFO")}: {message}");
        }
    }
}
