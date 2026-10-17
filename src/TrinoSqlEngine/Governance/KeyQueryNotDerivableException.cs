namespace TrinoSqlEngine.Governance;

using System;

/// <summary>Reason codes of <see cref="KeyQueryNotDerivableException.ReasonCode"/>; the row filter stage maps them one to one to its fallback reasons.</summary>
public static class KeyQueryNotDerivableReasons
{
    /// <summary>The <c>EXISTS</c> has no equivalent key query (PLAN-GOV-107 A-3).</summary>
    public const string NotDerivable = "NotDerivable";

    /// <summary>The key and foreign key column types are not an allowlisted pair, or their collations differ (A-9, SEC-RF107-07).</summary>
    public const string TypeNotBindable = "TypeNotBindable";

    /// <summary>The dialect has no production capability entry, so no key query can be emitted.</summary>
    public const string DialectUnsupported = "DialectUnsupported";
}

/// <summary>
/// The correlated <c>EXISTS</c> has no equivalent key query; the caller falls back to <c>EXISTS</c> (reason <see cref="ReasonCode"/>).
/// It is the only exception of the key query compiler that maps to a fallback: a <see cref="System.Security.SecurityException"/> or a
/// <see cref="Ast.Capabilities.SqlLimitExceededException"/> propagates unchanged (SEC-RF107-08). <see cref="Rule"/> names the violated
/// derivation rule for diagnostics and tests; it never carries request text or values.
/// </summary>
public sealed class KeyQueryNotDerivableException(string reasonCode, string? rule = null) : Exception(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;

    public string? Rule { get; } = rule;
}
