using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Deterministic <see cref="IConsentResolutionService"/> test double for unit tests that fix or compute the resolved decision.
/// </summary>
public sealed class StubConsentResolutionService(Func<TableIdentifier, TableAccessDecision> resolve) : IConsentResolutionService
{
    public static readonly StubConsentResolutionService Unconfigured = new(_ => null!);

    public static StubConsentResolutionService Fixed(TableAccessDecision decision) => new(_ => decision);

    public static StubConsentResolutionService AllowUnconstrained() =>
        new(table => TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

    public TableAccessDecision ResolveAccess(
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles,
        TableIdentifier table,
        IReadOnlyList<Consent> activeConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer,
        IReadOnlySet<Sid>? allUserSids = null,
        IReadOnlySet<string>? callerScopes = null)
        => resolve(table);
}
