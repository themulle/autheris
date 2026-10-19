namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Who changes the registry: the immutable subject key (never a UPN) and whether the actor holds the separate approver role (BC-129-03).</summary>
public sealed record CallerAttributeActor(string SubjectKey, bool HoldsApproverRole);

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
/// The rules for changing the registry and the service sets (PLAN-GOV-129 A-7, BC-129-10): validated, four-eyes (the store enforces
/// approver != creator and the reviewed hash), audited with hashes only, visible to enforcement on this instance at once.
/// </summary>
public sealed class CallerAttributeAdministrationService
{
    private readonly ICallerAttributeRepository _repository;
    private readonly ICallerAttributeRegistry _registry;
    private readonly IAuditLogRepository _audit;
    private readonly CallerAttributeOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ICallerAttributeDefinitionValidator> _validators;

    public CallerAttributeAdministrationService(
        ICallerAttributeRepository repository,
        ICallerAttributeRegistry registry,
        IAuditLogRepository audit,
        IOptions<GatewayOptions> options,
        TimeProvider time,
        IEnumerable<ICallerAttributeDefinitionValidator> validators)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options?.Value?.CallerAttributes ?? new CallerAttributeOptions();
        _time = time ?? TimeProvider.System;
        _validators = (validators ?? []).ToDictionary(v => v.Kind, StringComparer.Ordinal);
    }

    public Task<CallerAttributeStoreSnapshot> GetSnapshotAsync(CancellationToken ct = default) => _repository.LoadCallerAttributeSnapshotAsync(ct);

    public async Task SubmitDefinitionAsync(CallerAttributeDefinition definition, CallerAttributeActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureEnabled();
        ValidateDefinition(definition);
        if (_validators.TryGetValue(definition.Provider.Kind, out var validator))
        {
            validator.Validate(definition);
        }

        var result = await _repository.SubmitDefinitionAsync(definition, actor.SubjectKey, _time.GetUtcNow(), ct).ConfigureAwait(false);
        EnsureApplied(result);
        _registry.Invalidate();
        await AuditAsync(definition.Tenant, actor, AuditEventTypes.CallerAttributeDefinitionPending, definition.Name,
            new { name = definition.Name, status = "PendingApproval", hash = definition.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }

    public async Task RequestDeletionAsync(TenantId tenant, string name, CallerAttributeActor actor, CancellationToken ct = default)
    {
        EnsureEnabled();
        EnsureApplied(await _repository.RequestDeletionAsync(tenant, name, actor.SubjectKey, _time.GetUtcNow(), ct).ConfigureAwait(false));
        _registry.Invalidate();
        await AuditAsync(tenant, actor, AuditEventTypes.CallerAttributeDefinitionPending, name, new { name, status = "DeletionPending" }, ct).ConfigureAwait(false);
    }

    public async Task ApproveDefinitionAsync(TenantId tenant, string name, string expectedHash, CallerAttributeActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureEnabled();
        var before = (await _repository.LoadCallerAttributeSnapshotAsync(ct).ConfigureAwait(false)).Attributes
            .FirstOrDefault(a => a.Definition.Tenant == tenant && string.Equals(a.Definition.Name, name, StringComparison.Ordinal));
        if (before is null)
        {
            throw new CallerAttributeAdminException(CallerAttributeChangeResult.NotFound.ToString(), "The caller attribute does not exist.");
        }

        var pending = before.PendingDeletion ? before.Definition : before.Draft ?? before.Definition;
        RequireApproverRole(pending.Provider.Kind, actor);
        var result = await _repository.ApproveDefinitionAsync(tenant, name, actor.SubjectKey, expectedHash, _time.GetUtcNow(), ct).ConfigureAwait(false);
        EnsureApplied(result);
        _registry.Invalidate();
        await AuditAsync(tenant, actor, before.PendingDeletion ? AuditEventTypes.CallerAttributeDefinitionDeleted : AuditEventTypes.CallerAttributeDefinitionApplied, name,
            new { name, before = before.DefinitionHash, after = before.PendingDeletion ? null : expectedHash }, ct).ConfigureAwait(false);
    }

    public async Task RejectDefinitionAsync(TenantId tenant, string name, CallerAttributeActor actor, CancellationToken ct = default)
    {
        EnsureEnabled();
        EnsureApplied(await _repository.RejectDefinitionAsync(tenant, name, actor.SubjectKey, _time.GetUtcNow(), ct).ConfigureAwait(false));
        _registry.Invalidate();
        await AuditAsync(tenant, actor, AuditEventTypes.CallerAttributeDefinitionRejected, name, new { name }, ct).ConfigureAwait(false);
    }

    public async Task SubmitServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, CallerAttributeActor actor, CancellationToken ct = default)
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

        EnsureApplied(await _repository.SubmitServiceSetAsync(tenant, applicationId, attributeName, values, actor.SubjectKey, _time.GetUtcNow(), ct).ConfigureAwait(false));
        _registry.Invalidate();
        await AuditAsync(tenant, actor, AuditEventTypes.CallerAttributeServiceSetChanged, attributeName,
            new { attribute = attributeName, step = "submitted", count = values.Count }, ct).ConfigureAwait(false);
    }

    public async Task ApproveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, string expectedHash, CallerAttributeActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureEnabled();
        RequireApproverRole(CallerAttributeProviderBinding.ServiceSet, actor);
        EnsureApplied(await _repository.ApproveServiceSetAsync(tenant, applicationId, attributeName, actor.SubjectKey, expectedHash, _time.GetUtcNow(), ct).ConfigureAwait(false));
        _registry.Invalidate();
        await AuditAsync(tenant, actor, AuditEventTypes.CallerAttributeServiceSetChanged, attributeName,
            new { attribute = attributeName, step = "approved", hash = expectedHash }, ct).ConfigureAwait(false);
    }

    public async Task RejectServiceSetAsync(TenantId tenant, string applicationId, string attributeName, CallerAttributeActor actor, CancellationToken ct = default)
    {
        EnsureEnabled();
        EnsureApplied(await _repository.RejectServiceSetAsync(tenant, applicationId, attributeName, actor.SubjectKey, _time.GetUtcNow(), ct).ConfigureAwait(false));
        _registry.Invalidate();
        await AuditAsync(tenant, actor, AuditEventTypes.CallerAttributeServiceSetChanged, attributeName, new { attribute = attributeName, step = "rejected" }, ct).ConfigureAwait(false);
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
            throw new CallerAttributeAdminException("ApproverRoleRequired", "Approving this change needs the separate approver role.");
        }
    }

    private static void EnsureApplied(CallerAttributeChangeResult result)
    {
        if (result != CallerAttributeChangeResult.Applied)
        {
            throw new CallerAttributeAdminException(result.ToString(), $"The change was refused ({result}).");
        }
    }

    private Task AuditAsync(TenantId tenant, CallerAttributeActor actor, string eventType, string target, object details, CancellationToken ct) =>
        _audit.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = eventType,
            ActorSid = new Sid(actor.SubjectKey),
            TargetTable = $"caller_attribute:{target}",
            Decision = "ALLOW",
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(details)
        }, ct);
}
