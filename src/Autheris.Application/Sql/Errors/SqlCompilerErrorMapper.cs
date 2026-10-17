namespace Autheris.Application.Sql.Errors;

using System.Diagnostics.Metrics;
using System.Security;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Autheris.Application.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

/// <summary>
/// The result of <see cref="SqlCompilerErrorMapper.TryMap"/>: HTTP status, stable code and the fixed client message. <c>Limit</c> is
/// the <see cref="SqlLimitKind"/> name of a limit error and never a count.
/// </summary>
public readonly record struct SqlErrorMapping(int Status, string Code, string Title, string? Limit = null);

/// <summary>
/// WP-X0a (plan section 28.4.3, SEC-X1-01, SEC-X1-05): the one mapping of compiler and execution errors to a status, a stable code
/// and a fixed message, shared by every surface that returns or persists an error of a governed entry point (WebSQL, the global
/// handler, Arrow Flight, MCP, async statements, async jobs). The message is always <see cref="GovernedSqlErrorCodes.FixedMessage"/>:
/// never <c>Exception.Message</c>, driver text, values, object names or counts. Precedence is the row order of the plan table; a
/// security exception is never downgraded to a 4xx data code.
/// </summary>
public static class SqlCompilerErrorMapper
{
    /// <summary>Depth of the inner-exception walk (<c>GovernedSqlRewriter</c> wraps engine exceptions).</summary>
    private const int MaxDepth = 8;

    private static readonly Counter<long> InvariantFailures = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_sql_compiler_invariant_failures_total",
        description: "Gateway defects of the governed SQL compiler that failed closed (SQL_COMPILER_INVARIANT, DML_CHECKED_EXECUTION_REQUIRED).");

    /// <summary>Maps an exception (or an exception in its inner chain) to the stable error; false when it is not a governed SQL error.</summary>
    public static bool TryMap(Exception? exception, out SqlErrorMapping mapping)
    {
        bool mapped = TryMapCore(exception, out mapping);
        if (mapped && TrinoSqlEngine.CompilerHooks.IsDisabled(TrinoSqlEngine.SecurityControl.ErrorMapperFixedMessage))
        {
            mapping = mapping with { Title = exception!.Message };
        }
        return mapped;
    }

    private static bool TryMapCore(Exception? exception, out SqlErrorMapping mapping)
    {
        if (exception is null)
        {
            mapping = default;
            return false;
        }

        // Row order of the plan table: the first rule that matches any exception of the chain wins.
        foreach (var rule in Rules)
        {
            for (var (current, depth) = (exception, 0); current is not null && depth < MaxDepth; current = current.InnerException, depth++)
            {
                if (rule(current) is { } found)
                {
                    mapping = found;
                    return true;
                }
            }
        }

        // Timeout and unavailable are classified before the sanitizer can turn them into a generic error.
        switch (DataAccessErrorClassifier.Classify(exception))
        {
            case DataAccessErrorKind.Timeout:
                mapping = Of(504, GovernedSqlErrorCodes.Timeout);
                return true;
            case DataAccessErrorKind.Unavailable:
                mapping = Of(503, GovernedSqlErrorCodes.SourceUnavailable);
                return true;
            default:
                mapping = default;
                return false;
        }
    }

    /// <summary>The fixed message of the mapped error, or <paramref name="fallback"/> when the exception is not a governed SQL error.</summary>
    public static string FixedMessageOrDefault(Exception? exception, string fallback) =>
        TryMap(exception, out var mapping) ? mapping.Title : fallback;

    /// <summary>Counts a gateway defect (<c>SQL_COMPILER_INVARIANT</c>, <c>DML_CHECKED_EXECUTION_REQUIRED</c>) once per answered request.</summary>
    public static void RecordOutcome(in SqlErrorMapping mapping)
    {
        if (mapping.Code is GovernedSqlErrorCodes.CompilerInvariant or GovernedSqlErrorCodes.CheckedExecutionRequired)
        {
            InvariantFailures.Add(1);
        }
    }

    private static SqlErrorMapping Of(int status, string code, string? limit = null) =>
        new(status, code, GovernedSqlErrorCodes.FixedMessage(code), limit);

    private static readonly Func<Exception, SqlErrorMapping?>[] Rules =
    [
        // A parser time budget overrun and parser-slot exhaustion are ParseCanceledExceptions with a TimeoutException cause (FastSqlEngine):
        // a limit, not a syntax error. This row precedes the syntax row, which would otherwise match first.
        e => e is ParseCanceledException { InnerException: TimeoutException } ? Of(422, GovernedSqlErrorCodes.LimitExceeded, nameof(SqlLimitKind.CompileTime)) : null,
        e => e is ParseCanceledException or RecognitionException ? Of(400, GovernedSqlErrorCodes.SyntaxError) : null,
        e => e is MissingClientParameterException ? Of(400, GovernedSqlErrorCodes.ParameterMissing) : null,
        // REQ-SQL-128: a caller mistake with a list parameter. The code is stable, the message fixed, no count or name. The row precedes
        // the generic SecurityException row (403), which a wrapped list error must never reach.
        e => e is ClientListException list ? Of(400, list.Code) : null,
        // Phase 5 L3: a user error (a reserved identifier), not a gateway defect: no invariant metric.
        e => e is ReservedIdentifierException ? Of(400, GovernedSqlErrorCodes.ReservedIdentifier) : null,
        e => e switch
        {
            SqlCompileNotSupportedException { Reason: SqlCompileNotSupportedReason.Construct } or AstBuildException
                => Of(422, GovernedSqlErrorCodes.ConstructNotSupported),
            _ => null
        },
        e => e is SqlCompileNotSupportedException { Reason: SqlCompileNotSupportedReason.StatementClass } ? Of(403, GovernedSqlErrorCodes.StatementNotPermitted) : null,
        e => e is SqlCompileNotSupportedException { Reason: SqlCompileNotSupportedReason.Dialect } ? Of(501, GovernedSqlErrorCodes.DialectNotSupported) : null,
        e => e switch
        {
            SqlLimitExceededException limit => Of(422, GovernedSqlErrorCodes.LimitExceeded, limit.Kind.ToString()),
            OperationCanceledException { InnerException: TimeoutException } => Of(422, GovernedSqlErrorCodes.LimitExceeded, nameof(SqlLimitKind.CompileTime)),
            _ => null
        },
        e => e is UnfilteredDmlException ? Of(403, GovernedSqlErrorCodes.UnfilteredDml) : null,
        // SEC-X1-05: one code for every "table not usable" cause, so a response never tells whether a table exists.
        e => e is PolicyParseException or PolicyConflictException or WebSqlTableDeniedException ? Of(403, GovernedSqlErrorCodes.PolicyDenied) : null,
        e => e is GatewaySecurityException { ErrorCode: GovernedSqlErrorCodes.TenantIdCollision } ? Of(403, GovernedSqlErrorCodes.TenantIdCollision) : null,
        // Gateway defects fail closed. They precede the generic SecurityException row because they derive from it.
        e => e is SecurityCoverageException or EmittedSqlInvariantViolationException or SqlCompileConfigurationException ? Of(500, GovernedSqlErrorCodes.CompilerInvariant) : null,
        e => e is MaskedAggregationException ? Of(403, GovernedSqlErrorCodes.AggregationOnMaskedColumn) : null,
        e => e is SecurityException ? Of(403, GovernedSqlErrorCodes.PolicyViolation) : null,
        e => e is GovernedSqlException governed ? MapGoverned(governed.Code) : null,
        // Phase 5 M4: a data source that cannot hand out a correctly pinned session (missing or invalid default schema, a pin that did
        // not verify). The exception derives from InvalidOperationException, so the classifier would not know it.
        e => e is SqlSourceUnavailableException ? Of(503, GovernedSqlErrorCodes.SourceUnavailable) : null
    ];

    private static SqlErrorMapping? MapGoverned(string code) => code switch
    {
        GovernedSqlErrorCodes.CheckOptionViolation => Of(403, code),
        GovernedSqlErrorCodes.ConstraintViolation => Of(409, code),
        GovernedSqlErrorCodes.AffectedRowsLimit => Of(422, code),
        GovernedSqlErrorCodes.DataError => Of(422, code),
        GovernedSqlErrorCodes.ProviderError => Of(502, code),
        GovernedSqlErrorCodes.CheckedExecutionRequired => Of(500, code),
        // An unknown governed code is a gateway defect: fail closed.
        _ => Of(500, GovernedSqlErrorCodes.CompilerInvariant)
    };
}
