namespace Autheris.Domain.Interfaces;

public interface IConsentResolutionService
{
    /// <summary>
    /// Löst Zugriffsrechte deterministisch nach F-CONS-07 Wahrheitstabelle auf.
    /// </summary>
    TableAccessDecision ResolveAccess(
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles,
        TableIdentifier table,
        IReadOnlyList<Consent> activeConsents,
        Autheris.Domain.Common.DatabaseDialect dialect = Autheris.Domain.Common.DatabaseDialect.SqlServer,
        IReadOnlySet<Sid>? allUserSids = null,
        IReadOnlySet<string>? callerScopes = null
    );

    /// <summary>
    /// Resolves access rights deterministically with optional catalog table metadata for dynamic pattern coverage
    /// (excluding hidden objects/columns and masking newly added sensitive columns unless explicitly cleared).
    /// </summary>
    TableAccessDecision ResolveAccess(
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles,
        TableIdentifier table,
        IReadOnlyList<Consent> activeConsents,
        Autheris.Domain.Common.DatabaseDialect dialect,
        IReadOnlySet<Sid>? allUserSids,
        IReadOnlySet<string>? callerScopes,
        TableMetadata? tableMetadata) =>
        ResolveAccess(userSid, subjectGroupSids, userRoles, table, activeConsents, dialect, allUserSids, callerScopes);
}
