namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Who changes the registry: the immutable subject key (never a UPN), whether the actor holds the approver role (BC-129-03), and the
/// actor's own identifiers and groups (a change never grants or widens access for them).
/// </summary>
public sealed record CallerAttributeActor(
    string SubjectKey,
    bool HoldsApproverRole,
    IReadOnlyCollection<string>? OwnIdentifiers = null,
    IReadOnlyCollection<string>? OwnGroups = null)
{
    internal SingleApprovalWriter ToWriter() => new(SubjectKey, OwnIdentifiers ?? [], OwnGroups ?? []);
}

/// <summary>A refused registry change; <see cref="Code"/> is stable, the message never contains a value.</summary>
public sealed class CallerAttributeAdminException : Exception
{
    public CallerAttributeAdminException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Provider-specific checks of a definition at submit time (for example identifiers of an attribute table).</summary>
public interface ICallerAttributeDefinitionValidator
{
    string Kind { get; }

    /// <exception cref="CallerAttributeAdminException">The definition is not acceptable.</exception>
    void Validate(CallerAttributeDefinition definition);
}

/// <summary>
/// The rules for changing the registry and the service sets (129 A-7): validated, single approval (a save takes effect at once when the
/// writer holds the capacity and does not widen their own access), audited fail closed with before/after hashes only, visible to
/// enforcement on this instance at once.
/// </summary>
public sealed class CallerAttributeAdministrationService
{
    private readonly ICallerAttributeRepository _repository;
    private readonly ICallerAttributeRegistry _registry;
    private readonly IAuditLogRepository _audit;
    private readonly CallerAttributeOptions _options;
    private readonly TimeProvider _time;
    private readonly ICallerAttributeCacheInvalidator? _invalidator;
    private readonly Dictionary<string, ICallerAttributeDefinitionValidator> _validators;
    private readonly SelfWideningGuard _guard;

    public CallerAttributeAdministrationService(
        ICallerAttributeRepository repository,
        ICallerAttributeRegistry registry,
        IAuditLogRepository audit,
        IOptions<GatewayOptions> options,
        TimeProvider time,
        IEnumerable<ICallerAttributeDefinitionValidator> validators,
        ICallerAttributeCacheInvalidator? invalidator = null)
    {
        _invalidator = invalidator;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options?.Value?.CallerAttributes ?? new CallerAttributeOptions();
        _time = time ?? TimeProvider.System;
        _validators = (validators ?? []).ToDictionary(v => v.Kind, StringComparer.Ordinal);
        _guard = new SelfWideningGuard(_audit);
    }

    public Task<CallerAttributeStoreSnapshot> GetSnapshotAsync(CancellationToken ct = default) => _repository.LoadCallerAttributeSnapshotAsync(ct);

    /// <summary>Saves the definition; it is active at once.</summary>
    public async Task SaveDefinitionAsync(CallerAttributeDefinition definition, CallerAttributeActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureEnabled();
        ValidateDefinition(definition);
        if (_validators.TryGetValue(definition.Provider.Kind, out var validator))
        {
            validator.Validate(definition);
        }

        RequireApproverRole(definition.Provider.Kind, actor);
        var before = (await _repository.LoadCallerAttributeSnapshotAsync(ct).ConfigureAwait(false)).Attributes
            .FirstOrDefault(a => a.Definition.Tenant == definition.Tenant && string.Equals(a.Definition.Name, definition.Name, StringComparison.Ordinal));
        await _guard.RecordAsync(actor.ToWriter(), new SingleApprovalChange(definition.Tenant, AuditEventTypes.CallerAttributeDefinitionApplied,
            $"caller_attribute:{definition.Name}", before?.DefinitionHash, definition.ComputeDefinitionHash()), ct).ConfigureAwait(false);

        EnsureApplied(await _repository.SaveDefinitionAsync(definition, actor.SubjectKey, _time.GetUtcNow(), ct).ConfigureAwait(false));
        _registry.Invalidate();
        _invalidator?.InvalidateAttribute(definition.Tenant, definition.Name);
    }

    public async Task DeleteDefinitionAsync(TenantId tenant, string name, CallerAttributeActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureEnabled();
        var before = (await _repository.LoadCallerAttributeSnapshotAsync(ct).ConfigureAwait(false)).Attributes
            .FirstOrDefault(a => a.Definition.Tenant == tenant && string.Equals(a.Definition.Name, name, StringComparison.Ordinal))
            ?? throw new CallerAttributeAdminException(CallerAttributeChangeResult.NotFound.ToString(), "The caller attribute does not exist.");
        RequireApproverRole(before.Definition.Provider.Kind, actor);
        await _guard.RecordAsync(actor.ToWriter(), new SingleApprovalChange(tenant, AuditEventTypes.CallerAttributeDefinitionDeleted,
            $"caller_attribute:{name}", before.DefinitionHash, null), ct).ConfigureAwait(false);

        EnsureApplied(await _repository.DeleteDefinitionAsync(tenant, name, ct).ConfigureAwait(false));
        _registry.Invalidate();
        _invalidator?.InvalidateAttribute(tenant, name);
    }

    /// <summary>Saves the static values of an application-only caller; they are active at once.</summary>
    public async Task SaveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, CallerAttributeActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId.Any(char.IsControl) || applicationId != applicationId.Trim())
        {
            throw new CallerAttributeAdminException("InvalidApplicationId", "The application id is not acceptable.");
        }

        var snapshot = await _registry.GetAsync(ct).ConfigureAwait(false);
        if (!snapshot.TryGetDefinition(tenant, attributeName, out var definition))
        {
            throw new CallerAttributeAdminException("UnknownAttribute", "The attribute is not registered and active.");
        }

        if (values.Count > definition.MaxValues || values.Any(v => string.IsNullOrEmpty(v) || v.Length > 256) ||
            (definition.Cardinality == CallerAttributeCardinality.Scalar && values.Count > 1))
        {
            throw new CallerAttributeAdminException("InvalidValues", "The values do not fit the attribute definition.");
        }

        RequireApproverRole(CallerAttributeProviderBinding.ServiceSet, actor);
        try
        {
            SelfWideningGuard.EnsureDoesNotWidenOwnAccess(actor.ToWriter(), [applicationId]);
        }
        catch (SingleApprovalRefusedException ex)
        {
            throw new CallerAttributeAdminException(ex.Code, ex.Message);
        }

        var app = CallerAttributeNameRules.CanonicalApplicationId(applicationId);
        var before = (await _repository.LoadCallerAttributeSnapshotAsync(ct).ConfigureAwait(false)).ServiceSets
            .FirstOrDefault(x => x.Set.Tenant == tenant && string.Equals(x.Set.ApplicationId, app, StringComparison.Ordinal) && string.Equals(x.Set.AttributeName, attributeName, StringComparison.Ordinal));
        var after = new ServiceAttributeSet(Guid.Empty, tenant, app, attributeName, values, FilterApprovalStatus.Active);
        await _guard.RecordAsync(actor.ToWriter(), new SingleApprovalChange(tenant, AuditEventTypes.CallerAttributeServiceSetChanged,
            $"caller_attribute:{attributeName}", before?.Set.ComputeValuesHash(), after.ComputeValuesHash()), ct).ConfigureAwait(false);

        EnsureApplied(await _repository.SaveServiceSetAsync(tenant, applicationId, attributeName, values, actor.SubjectKey, _time.GetUtcNow(), ct).ConfigureAwait(false));
        _registry.Invalidate();
        _invalidator?.InvalidateAttribute(tenant, attributeName);
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new CallerAttributeAdminException("Disabled", "Caller attributes are disabled.");
        }
    }

    private void ValidateDefinition(CallerAttributeDefinition d)
    {
        if (!CallerAttributeDefinition.IsValidName(d.Name))
        {
            throw new CallerAttributeAdminException("InvalidName", "The attribute name is not acceptable.");
        }

        if (d.TtlSeconds < 1 || d.TtlSeconds > Math.Min(_options.MaxTtlSeconds, CallerAttributeOptions.TtlCeilingSeconds))
        {
            throw new CallerAttributeAdminException("InvalidTtl", "The TTL is out of range.");
        }

        if (d.MaxValues < 1 || d.MaxValues > _options.MaxValuesHardCap)
        {
            throw new CallerAttributeAdminException("InvalidMaxValues", "The maximum number of values is out of range.");
        }

        if (d.Cardinality == CallerAttributeCardinality.Scalar && d.MaxValues != 1 && d.MaxValues != CallerAttributeDefinition.DefaultMaxValues)
        {
            // A scalar attribute is one value; the stored maximum is irrelevant but must not be misleading.
            throw new CallerAttributeAdminException("InvalidMaxValues", "A scalar attribute has no value count.");
        }

        if (string.IsNullOrWhiteSpace(d.Provider.Kind))
        {
            throw new CallerAttributeAdminException("InvalidProvider", "The provider kind is missing.");
        }
    }

    private void RequireApproverRole(string providerKind, CallerAttributeActor actor)
    {
        bool grantsData = providerKind is CallerAttributeProviderBinding.AttributeTable or CallerAttributeProviderBinding.ServiceSet;
        if (_options.RequireSeparateApproverRole && grantsData && !actor.HoldsApproverRole)
        {
            throw new CallerAttributeAdminException("ApproverRoleRequired", "Saving this change needs the approver role.");
        }
    }

    private static void EnsureApplied(CallerAttributeChangeResult result)
    {
        if (result != CallerAttributeChangeResult.Applied)
        {
            throw new CallerAttributeAdminException(result.ToString(), $"The change was refused ({result}).");
        }
    }
}
