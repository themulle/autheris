namespace Autheris.Domain.Interfaces;

/// <summary>
/// Dialect text of a typed row policy for the legacy string consumers. <see cref="Parameters"/> is keyed by the marker that appears in
/// <see cref="Sql"/> (<c>@__gql_rls_N</c>); no value is ever part of the text.
/// </summary>
public sealed record RowPolicyText(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Opaque typed row policy carried by a <see cref="TableAccessDecision"/> (SEC-ADG-25). Domain must not reference the SQL engine, so the
/// typed predicate stays behind this abstraction and is implemented in Application. There is deliberately no factory from text: a
/// policy is built from a typed predicate or not at all, so a string and a typed policy cannot drift apart.
/// </summary>
public interface IRowPolicy
{
    /// <summary>Hash of the predicate shape (nodes, parameter names and types); values are not part of it.</summary>
    string Fingerprint { get; }

    /// <summary>The policy matches no row (<c>1 = 0</c>).</summary>
    bool IsDenyAll { get; }

    /// <summary>Typed AND-merge. Parameters bound to conflicting values throw a <see cref="System.Security.SecurityException"/> (fail closed).</summary>
    IRowPolicy And(IRowPolicy other);

    /// <summary>Read-only text projection for the string consumers; computed once per policy.</summary>
    RowPolicyText Project();
}
