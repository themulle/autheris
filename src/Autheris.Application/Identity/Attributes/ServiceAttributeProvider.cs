namespace Autheris.Application.Identity.Attributes;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// Static values per <c>(tenant, application id, attribute)</c> for application-only callers (PLAN-GOV-129 A-5.3, BC-129-02). Only an
/// <see cref="CallerSubjectKind.Application"/> subject reads it, and only an active (approved) set applies. The application id was
/// taken from one fixed-precedence claim and canonicalised by the subject factory; conflicting candidates left it empty.
/// </summary>
public sealed class ServiceAttributeProvider : ICallerAttributeProvider
{
    private readonly ICallerAttributeRegistry _registry;

    public ServiceAttributeProvider(ICallerAttributeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public string Kind => CallerAttributeProviderBinding.ServiceSet;

    public async ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
    {
        if (subject.Kind != CallerSubjectKind.Application || string.IsNullOrEmpty(subject.ApplicationId))
        {
            return CallerAttributeResult.Missing();
        }

        var snapshot = await _registry.GetAsync(ct).ConfigureAwait(false);
        if (!snapshot.TryGetServiceSet(subject.Tenant, subject.ApplicationId, definition.Name, out var values) || values.Count == 0)
        {
            return CallerAttributeResult.Missing();
        }

        return CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String));
    }
}
