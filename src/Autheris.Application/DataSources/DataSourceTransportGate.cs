namespace Autheris.Application.DataSources;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options.DataSources;

/// <summary>Why the transport gate allowed or denied a request (the sub-reason appears only in the audit event, SEC-ATR-08).</summary>
public enum TransportDecisionReason
{
    Allowed = 0,
    TransportUnset,
    InvalidTransport,
    UnknownSource,
    FlagNotSet,
    EvaluationFailed
}

/// <summary>Outcome of <see cref="DataSourceTransportGate.Decide"/>.</summary>
public readonly record struct TransportDecision(TransportDecisionReason Reason)
{
    public bool IsAllowed => Reason == TransportDecisionReason.Allowed;
}

/// <summary>
/// Issue #447 (REQ-ATR-01/02): the single place that enforces <c>AllowedTransports</c> per data source. Fail-closed: no switch, no
/// optional dependency, no swallowed error; an unset or invalid ingress, an unknown source and every evaluation failure deny.
/// </summary>
public interface IDataSourceTransportGate
{
    /// <summary>Throws <see cref="TransportNotAllowedException"/> (and audits once) when the ingress transport may not read the source.</summary>
    Task EnsureAsync(string? dataSourceName, CancellationToken ct = default);

    /// <summary>All-or-nothing form of <see cref="EnsureAsync"/>: one denied source denies the whole set before any data access.</summary>
    Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default);

    /// <summary>Non-throwing, non-auditing form for discovery filtering; the same decision as <see cref="EnsureAsync"/>.</summary>
    bool IsAllowed(string? dataSourceName);
}

/// <inheritdoc />
public sealed class DataSourceTransportGate : IDataSourceTransportGate
{
    private readonly IIngressTransportContext _ingress;
    private readonly IDataSourceOptionsResolver _resolver;
    private readonly ITransportDenialRecorder _recorder;

    public DataSourceTransportGate(
        IIngressTransportContext ingress,
        IDataSourceOptionsResolver resolver,
        ITransportDenialRecorder recorder)
    {
        _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default) =>
        EnsureAllAsync([dataSourceName], ct);

    /// <inheritdoc />
    public async Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSourceNames);
        var transport = _ingress.Current;

        foreach (var logicalName in dataSourceNames)
        {
            var canonical = Canonicalise(logicalName);
            var decision = Decide(_resolver, canonical, transport);
            if (decision.IsAllowed)
            {
                continue;
            }

            // One audit event for the first denial; nothing was read, and allowed sources are never audited (no partial audit).
            await _recorder.RecordDenialAsync(new TransportDenial(logicalName, canonical, transport, decision.Reason), ct).ConfigureAwait(false);
            throw new TransportNotAllowedException(logicalName, transport);
        }
    }

    /// <inheritdoc />
    public bool IsAllowed(string? dataSourceName) =>
        Decide(_resolver, Canonicalise(dataSourceName), _ingress.Current).IsAllowed;

    /// <summary>
    /// SEC-ATR-01: the only decision point. Pure and total: any exception of the resolver is an <see cref="TransportDecisionReason.EvaluationFailed"/>
    /// denial. Uses <c>TryResolve</c> only, so an unknown source never falls back to the default profile.
    /// </summary>
    public static TransportDecision Decide(IDataSourceOptionsResolver resolver, string? canonicalName, DataSourceTransportFlags transport)
    {
        if (transport == DataSourceTransportFlags.None)
        {
            return new TransportDecision(TransportDecisionReason.TransportUnset);
        }

        if (!IngressTransportContext.IsSingleKnownFlag(transport))
        {
            return new TransportDecision(TransportDecisionReason.InvalidTransport);
        }

        if (resolver is null || string.IsNullOrWhiteSpace(canonicalName))
        {
            return new TransportDecision(TransportDecisionReason.UnknownSource);
        }

        try
        {
            if (!resolver.TryResolve(canonicalName, out var profile) || profile is null)
            {
                return new TransportDecision(TransportDecisionReason.UnknownSource);
            }

            return profile.IsTransportAllowed(transport)
                ? new TransportDecision(TransportDecisionReason.Allowed)
                : new TransportDecision(TransportDecisionReason.FlagNotSet);
        }
        catch (Exception)
        {
            return new TransportDecision(TransportDecisionReason.EvaluationFailed);
        }
    }

    private string Canonicalise(string? name)
    {
        try
        {
            return _resolver.CanonicalName(name);
        }
        catch (Exception)
        {
            // an unreadable mapping must not let the raw name through as a different source: deny via the blank-name path
            return string.Empty;
        }
    }
}
