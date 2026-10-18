namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>What an administrator asks the simulation: a subject of the administrator's tenant and the attributes to resolve (BC-129-13).</summary>
public sealed record CallerAttributeSimulationRequest(
    TenantId Tenant,
    SubjectKeyKind? KeyKind,
    string? KeyValue,
    string? Issuer,
    string? ApplicationId,
    IReadOnlyList<string> Attributes,
    bool Reveal,
    string? Reason);

/// <summary>One attribute of a simulation: outcome and reason always; the count and the peppered value hashes by default; clear values only on an audited reveal.</summary>
public sealed record CallerAttributeSimulationItem(
    string Attribute, CallerAttributeOutcome Outcome, string? ReasonCode, int? Count, IReadOnlyList<string>? ValueHashes, IReadOnlyList<string>? Values);

/// <summary>A simulation was refused; <see cref="Code"/> is stable and the message names no value.</summary>
public sealed class CallerAttributeSimulationException : Exception
{
    public CallerAttributeSimulationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// The admin simulation of the attribute resolution for a chosen subject (PLAN-GOV-129 A-11, BC-129-13). It resolves through the same resolver
/// as the data path. Default output: names, outcome, counts and the first 8 hex characters of an HMAC with a per-tenant pepper; clear values need
/// the <c>CallerAttributeInspector</c> role, a reason and are audited; every simulation is audited and rate limited per administrator; the subject
/// must belong to the administrator's tenant. Claims attributes cannot be simulated (there is no token): they resolve as missing.
/// </summary>
public sealed class CallerAttributeSimulationService
{
    private static readonly byte[] ProcessPepper = RandomNumberGenerator.GetBytes(32);

    private readonly ICallerAttributeResolver _resolver;
    private readonly IAuditLogRepository _audit;
    private readonly CallerAttributeOptions _options;
    private readonly TimeProvider _time;
    private readonly byte[] _pepper;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);

    public CallerAttributeSimulationService(ICallerAttributeResolver resolver, IAuditLogRepository audit, IOptions<GatewayOptions> options, TimeProvider? time = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options?.Value?.CallerAttributes ?? new CallerAttributeOptions();
        _time = time ?? TimeProvider.System;
        _pepper = string.IsNullOrEmpty(_options.SimulationHashPepper) ? ProcessPepper : Encoding.UTF8.GetBytes(_options.SimulationHashPepper);
    }

    /// <param name="adminTenant">The administrator's tenant; <paramref name="request"/>.Tenant must equal it unless <paramref name="crossTenantAllowed"/>.</param>
    public async Task<IReadOnlyList<CallerAttributeSimulationItem>> SimulateAsync(
        CallerAttributeSimulationRequest request, string adminSubjectKey, TenantId adminTenant, bool holdsInspectorRole, bool crossTenantAllowed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSubjectKey);
        if (!_options.Enabled)
        {
            throw new CallerAttributeSimulationException("Disabled", "Caller attributes are disabled.");
        }

        if (request.Tenant != adminTenant && !crossTenantAllowed)
        {
            throw new CallerAttributeSimulationException("TenantMismatch", "The subject must belong to the administrator's tenant.");
        }

        if (request.Reveal)
        {
            if (!holdsInspectorRole)
            {
                throw new CallerAttributeSimulationException("RevealRoleRequired", "Revealing values needs the inspector role.");
            }

            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
            {
                throw new CallerAttributeSimulationException("ReasonRequired", "Revealing values needs a reason of at most 256 characters.");
            }
        }

        if (request.Attributes.Count == 0 || request.Attributes.Count > 16 || request.Attributes.Any(a => !CallerAttributeDefinition.IsValidName(a)))
        {
            throw new CallerAttributeSimulationException("InvalidAttributes", "The attribute list is not acceptable.");
        }

        var subject = BuildSubject(request);
        EnforceRateLimit(adminSubjectKey);

        var set = await _resolver.ResolveAsync(subject, request.Attributes, ct).ConfigureAwait(false);
        var items = new List<CallerAttributeSimulationItem>();
        foreach (var name in request.Attributes.Distinct(StringComparer.Ordinal))
        {
            set.TryGet(name, out var result);
            if (result.Outcome != CallerAttributeOutcome.Resolved || result.Value is null)
            {
                items.Add(new CallerAttributeSimulationItem(name, result.Outcome, result.ReasonCode, null, null, null));
                continue;
            }

            var values = Enumerable.Range(0, result.Value.Count).Select(i => Convert.ToString(result.Value[i], CultureInfo.InvariantCulture) ?? string.Empty).ToList();
            items.Add(new CallerAttributeSimulationItem(
                name, result.Outcome, null, values.Count,
                values.Select(v => Hash(request.Tenant, v)).ToList(),
                request.Reveal ? values : null));
        }

        await _audit.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = adminTenant,
            EventType = AuditEventTypes.CallerAttributeSimulationRead,
            ActorSid = new Sid(adminSubjectKey),
            TargetTable = "caller_attribute_simulation",
            Decision = "ALLOW",
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(new
            {
                attributes = items.Select(i => new { name = i.Attribute, outcome = i.Outcome.ToString(), reason = i.ReasonCode, count = i.Count }),
                reveal = request.Reveal,
                reason = request.Reveal ? request.Reason : null,
                subject_tenant = request.Tenant.Value
            })
        }, ct).ConfigureAwait(false);
        return items;
    }

    private static CallerSubject BuildSubject(CallerAttributeSimulationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return new CallerSubject(request.Tenant, CallerSubjectKind.Application, CallerAttributeNameRules.CanonicalApplicationId(request.ApplicationId), [], request.Issuer, null);
        }

        if (request.KeyKind is not { } kind || string.IsNullOrWhiteSpace(request.KeyValue))
        {
            throw new CallerAttributeSimulationException("InvalidSubject", "A subject key or an application id is required.");
        }

        return new CallerSubject(
            request.Tenant, CallerSubjectKind.User, null, [], request.Issuer, null,
            new Dictionary<SubjectKeyKind, string> { [kind] = request.KeyValue.Trim() }.ToFrozenDictionary());
    }

    private string Hash(TenantId tenant, string value)
    {
        using var tenantKey = new HMACSHA256(_pepper);
        var perTenant = tenantKey.ComputeHash(Encoding.UTF8.GetBytes(tenant.Value));
        using var mac = new HMACSHA256(perTenant);
        return Convert.ToHexStringLower(mac.ComputeHash(Encoding.UTF8.GetBytes(value)))[..8];
    }

    private void EnforceRateLimit(string adminSubjectKey)
    {
        var now = _time.GetUtcNow();
        var queue = _recent.GetOrAdd(adminSubjectKey, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= _options.SimulationRequestsPerMinute)
            {
                throw new CallerAttributeSimulationException("RateLimited", "Too many simulations; try again later.");
            }

            queue.Enqueue(now);
        }
    }
}
