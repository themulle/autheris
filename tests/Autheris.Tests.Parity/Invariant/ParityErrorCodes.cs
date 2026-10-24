using System.Reflection;
using Autheris.Api.Errors;
using Autheris.Application.Analytics;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.GraphQL.Mcp;
using TrinoSqlEngine.Ast.Emit;

namespace Autheris.Tests.Parity.Invariant;

/// <summary>
/// Stable error codes a 4xx may carry (D-9): the public const strings of the public error-code classes (by reflection, so a new code
/// is accepted automatically), the WebSQL envelope tokens and the GraphQL codes emitted via <c>SetCode</c>.
/// </summary>
public static class ParityErrorCodes
{
    private static readonly Type[] CodeClasses =
    [
        typeof(ApiErrorCodes),
        typeof(AggregationErrorCodes),
        typeof(GovernedSqlErrorCodes),
        typeof(McpErrorCodes),
        typeof(VirtualFilterErrorCodes),
        typeof(Autheris.Domain.Model.InvalidCursorException), // WP-R2: bulk keyset cursor (INVALID_CURSOR)
    ];

    /// <summary>WebSQL <c>error</c> tokens, accepted as codes when a body has no fine-grained <c>code</c>.</summary>
    public static readonly IReadOnlyList<string> WebSqlTokens =
        ["BadRequest", "Forbidden", "Unauthorized", "NotImplemented", "TooManyRequests", "UnprocessableEntity", "NotFound", "Conflict", "PayloadTooLarge"];

    /// <summary>GraphQL codes (sanitizer and validation); the internal-fault code is deliberately absent.</summary>
    public static readonly IReadOnlyList<string> GraphQlCodes =
    [
        "INVALID_ARGUMENT", "UNAUTHORIZED", "AUTH_NOT_AUTHORIZED", "AUTH_TOKEN_EXPIRED", "FORBIDDEN", "NOT_FOUND", "BAD_REQUEST",
        "UPGRADE_REQUIRED", "FIELD_PERMANENTLY_DECOMMISSIONED", "TOKEN_REVOCATION_UNAVAILABLE",
        "GRAPHQL_VALIDATION_FAILED", "GRAPHQL_PARSE_FAILED", "BAD_USER_INPUT", "QUERY_TOO_COMPLEX", "QUERY_TOO_DEEP", "QUERY_COST_QUOTA_EXCEEDED", "AUTH_NOT_AUTHENTICATED",
    ];

    /// <summary>Codes that mean an unexpected server fault; they are hard failures on any status.</summary>
    public static readonly IReadOnlyList<string> InternalFaultCodes = ["INTERNAL_SERVER_ERROR", "InternalServerError", "INTERNAL_ERROR"];

    public static IReadOnlySet<string> All { get; } = Build();

    /// <summary>HotChocolate built-in error codes (<c>HC0011</c> ...) emitted by the GraphQL engine for syntax and coercion errors.</summary>
    public static bool IsGraphQlEngineCode(string? code) => code is { Length: 6 } && code.StartsWith("HC", StringComparison.Ordinal) && code.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>
    /// The safe-code whitelist of the GraphQL error sanitizer (the single place that decides which GraphQL codes reach a client),
    /// read by reflection so a new code is accepted automatically. Fails closed: a renamed type or member throws instead of
    /// silently shrinking the accepted set.
    /// </summary>
    private static IEnumerable<string> SanitizerWhitelist() =>
        ReadWhitelist(typeof(ApiErrorCodes).Assembly, "Autheris.Api.Middleware.ErrorSanitizingFilter", "WhitelistedSafeCodes");

    /// <summary>Reads a private static string collection by reflection; any missing piece throws (no silent empty set).</summary>
    internal static IEnumerable<string> ReadWhitelist(Assembly assembly, string typeName, string fieldName)
    {
        var type = assembly.GetType(typeName)
                   ?? throw new InvalidOperationException($"Type {typeName} not found; update ParityErrorCodes.");
        var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"{typeName}.{fieldName} not found; update ParityErrorCodes.");
        return field.GetValue(null) is IEnumerable<string> codes
            ? codes.ToList()
            : throw new InvalidOperationException($"{typeName}.{fieldName} has no string value; update ParityErrorCodes.");
    }

    private static HashSet<string> Build()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in CodeClasses)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string) && field.GetRawConstantValue() is string value)
                {
                    set.Add(value);
                }
            }
        }

        set.UnionWith(WebSqlTokens);
        set.UnionWith(GraphQlCodes);
        set.UnionWith(SanitizerWhitelist());
        set.Remove("INTERNAL_SERVER_ERROR");
        set.Remove("InternalServerError");
        return set;
    }
}
