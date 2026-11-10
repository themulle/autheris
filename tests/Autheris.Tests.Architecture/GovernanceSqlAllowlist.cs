namespace Autheris.Tests.Architecture;

/// <summary>SEC-SIMPL-26: per file + member exceptions for the governance SQL construction scan; each needs a reason.</summary>
internal static class GovernanceSqlAllowlist
{
    public static readonly IReadOnlyDictionary<(string File, string Member), string> Entries = new Dictionary<(string, string), string>
    {
        [("AuditSqlCore.cs", "QueryAuditLogsAsync")] = "where clauses are constant predicates over bound parameters (reviewed, shared audit core)",
        [("GovernanceRepository.Consent.cs", "ActiveForSubjectsCoreAsync")] = "appends the constant tenant predicate and the generated @sN placeholder list",
        [("GovernanceRepository.Consent.cs", "AllActiveCoreAsync")] = "appends the constant tenant predicate and the generated @sN placeholder list",
        [("GovernanceRepository.Consent.cs", "BuildLockRowsQuery")] = "joins constant fragments with the dialect lock clause constant",
        [("GovernanceRepository.ConsentPage.cs", "GetConsentPageAsync")] = "constant prefix/suffix around a where built from constant predicates and parameters",
        [("GovernanceRepository.ConsentGrants.cs", "ActiveRoleConsents")] = "appends optional tenant predicate to constant SELECT query",
        [("GovernanceRepository.ConsentSql.cs", "GetRequestsByRequesterSidAsync")] = "constant sql plus constant ORDER BY",
        [("GovernanceRepository.ConsentSql.cs", "HydrateConsentsBatchAsync")] = "constant prefix plus BindIdInClause placeholders (bounded batch)",
        [("GovernanceRepository.ConsentSql.cs", "LoadDescriptionsBatchAsync")] = "constant prefix plus BindIdInClause placeholders (bounded batch)",
        [("GovernanceRepository.WebhookOutbox.cs", "Enqueue")] = "interpolates constant column lists into INSERT statement",
        [("GovernanceRepository.WebhookOutbox.cs", "ClaimBatch")] = "interpolates constant returning/output column projection into claim statement",
        [("GovernanceRepository.WebhookOutbox.cs", "FencedUpdate")] = "interpolates validated set clause into constant UPDATE statement",
        [("GovernanceRepository.WebhookOutbox.cs", "ListDeadLetters")] = "interpolates validated owner scope predicate into constant SELECT statement",
        [("PostgreSqlGovernanceDialect.cs", "Upsert")] = "identifiers pass GovernanceSqlInput.Identifier (SEC-SIMPL-21) before being appended",
        [("SqlServerGovernanceDialect.cs", "Upsert")] = "identifiers pass GovernanceSqlInput.Identifier (SEC-SIMPL-21) before being appended",
        [("SqlServerGovernanceDialect.cs", "LimitWithoutOrder")] = "appends a validated positive int",
    };
}
