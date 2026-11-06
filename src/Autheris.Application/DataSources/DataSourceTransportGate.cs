namespace Autheris.Application.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options.DataSources;

/// <summary>Why the transport gate allowed or denied a request (the sub-reason appears only in the audit event).</summary>
public enum TransportDecisionReason
{
    Allowed = 0,
    TransportUnset,
    InvalidTransport,
    UnknownSource,
    FlagNotSet,
    EvaluationFailed,

    /// <summary>A persisted statement, job or ticket was presented over a different ingress than the one that created it.</summary>
    IngressMismatch
}

/// <summary>Outcome of <see cref="DataSourceTransportGate.Decide"/>.</summary>
public readonly record struct TransportDecision(TransportDecisionReason Reason)
{
    public bool IsAllowed => Reason == TransportDecisionReason.Allowed;
}

/// <summary>
/// Issue #447 (02): the single place that enforces<c>AllowedTransports</c> per data source. Fail-closed: no switch, no
/// optional dependency, no swallowed error; an unset or invalid ingress, an unknown source and every evaluation failure deny.
/// </summary>
public interface IDataSourceTransportGate
{
    /// <summary>Throws <see cref="TransportNotAllowedException"/> (and audits once) when the ingress transport may not read the source.</summary>
    Task EnsureAsync(string? dataSourceName, CancellationToken ct = default);

    /// <summary>All-or-nothing form of <see cref="EnsureAsync"/>: one denied source denies the whole set before any data access.</summary>
    Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default);

    /// <summary>
    /// The form for sources that come from the data catalog (a table's <c>SourceName</c> or domain), never from client input: such a source is
    /// known by definition, so one without an explicit profile is judged by <c>Defaults</c> (owner decision 1, 2026-11-03) instead of reading as
    /// an unknown source. A blank name, an unset or invalid ingress and every evaluation failure still deny.
    /// </summary>
    Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default);

    /// <summary>Single-source form of <see cref="EnsureCatalogSourcesAsync"/>.</summary>
    Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default);

    /// <summary>Non-throwing, non-auditing form for discovery filtering; the same decision as <see cref="EnsureAsync"/>.</summary>
    bool IsAllowed(string? dataSourceName);

    /// <summary>
    /// Logical names of the sources this scope's gate has allowed so far (complete by construction: every choke point calls the gate).
    /// A statement keeps them to re-check a later page against the live configuration.
    /// </summary>
    IReadOnlyList<string> CheckedSources { get; }

    /// <summary>The ingress transport of the current scope (for copying it into a derived scope or a persisted record).</summary>
    DataSourceTransportFlags Ingress { get; }
}

/// <summary>
/// Fail-closed stand-in for a consumer constructed without a gate: every call denies as an unset ingress (there is no
/// null or pass-through gate). Production wiring always supplies <see cref="DataSourceTransportGate"/>.
/// </summary>
public sealed class DenyAllTransportGate : IDataSourceTransportGate
{
    public static DenyAllTransportGate Instance { get; } = new();

    /// <inheritdoc />
    public DataSourceTransportFlags Ingress => DataSourceTransportFlags.None;

    /// <inheritdoc />
    public IReadOnlyList<string> CheckedSources => [];

    /// <inheritdoc />
    public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default) =>
        throw new TransportNotAllowedException(dataSourceName, DataSourceTransportFlags.None);

    /// <inheritdoc />
    public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default) =>
        throw new TransportNotAllowedException(dataSourceNames?.FirstOrDefault(), DataSourceTransportFlags.None);

    /// <inheritdoc />
    public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) =>
        throw new TransportNotAllowedException(catalogSourceNames?.FirstOrDefault(), DataSourceTransportFlags.None);

    /// <inheritdoc />
    public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) =>
        throw new TransportNotAllowedException(catalogSourceName, DataSourceTransportFlags.None);

    /// <inheritdoc />
    public bool IsAllowed(string? dataSourceName) => false;
}

/// <inheritdoc />
public sealed class DataSourceTransportGate : IDataSourceTransportGate
{
    private readonly IIngressTransportContext _ingress;
    private readonly IDataSourceOptionsResolver _resolver;
    private readonly ITransportDenialRecorder _recorder;
    private readonly object _checkedGate = new();
    private readonly List<string> _checked = [];

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
        EnsureOneAsync(dataSourceName, catalogKnown: false, ct);

    /// <inheritdoc />
    public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default) =>
        EnsureCoreAsync(dataSourceNames, catalogKnown: false, ct);

    /// <inheritdoc />
    public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) =>
        EnsureCoreAsync(catalogSourceNames, catalogKnown: true, ct);

    /// <inheritdoc />
    public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) =>
        EnsureOneAsync(catalogSourceName, catalogKnown: true, ct);

    private async Task EnsureCoreAsync(IEnumerable<string?> dataSourceNames, bool catalogKnown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSourceNames);
        foreach (var logicalName in dataSourceNames)
        {
            await EnsureOneAsync(logicalName, catalogKnown, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Single-name core (no per-call array); the all-or-nothing forms loop over it and stop at the first denial.</summary>
    private async Task EnsureOneAsync(string? logicalName, bool catalogKnown, CancellationToken ct)
    {
        var transport = _ingress.Current;
        var canonical = Canonicalise(logicalName);
        var decision = Decide(_resolver, canonical, transport, catalogKnown);
        if (decision.IsAllowed)
        {
            RememberChecked(logicalName!);
            return;
        }

        // One audit event for the first denial; nothing was read, and allowed sources are never audited (no partial audit).
        await _recorder.RecordDenialAsync(new TransportDenial(logicalName, canonical, transport, decision.Reason), ct).ConfigureAwait(false);
        throw new TransportNotAllowedException(logicalName, transport);
    }

    /// <inheritdoc />
    public DataSourceTransportFlags Ingress => _ingress.Current;

    /// <inheritdoc />
    public IReadOnlyList<string> CheckedSources
    {
        get
        {
            lock (_checkedGate)
            {
                return _checked.ToArray();
            }
        }
    }

    private void RememberChecked(string logicalName)
    {
        lock (_checkedGate)
        {
            if (!_checked.Contains(logicalName, StringComparer.OrdinalIgnoreCase))
            {
                _checked.Add(logicalName);
            }
        }
    }

    /// <inheritdoc />
    public bool IsAllowed(string? dataSourceName) =>
        Decide(_resolver, Canonicalise(dataSourceName), _ingress.Current).IsAllowed;

    /// <summary>
    /// the only decision point. Pure and total: any exception of the resolver is an<see cref="TransportDecisionReason.EvaluationFailed"/>
    /// denial. Uses <c>TryResolve</c> only, so an unknown source never falls back to the default profile.
    /// </summary>
    public static TransportDecision Decide(IDataSourceOptionsResolver resolver, string? canonicalName, DataSourceTransportFlags transport) =>
        Decide(resolver, canonicalName, transport, catalogKnown: false);

    /// <summary>
    /// <paramref name="catalogKnown"/>: the name comes from the data catalog, so a source without an explicit profile is judged by the
    /// <c>Defaults</c> profile instead of being unknown. Everything else (blank name, unset or invalid ingress, resolver failure) is unchanged.
    /// </summary>
    public static TransportDecision Decide(IDataSourceOptionsResolver resolver, string? canonicalName, DataSourceTransportFlags transport, bool catalogKnown)
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
                if (!catalogKnown)
                {
                    return new TransportDecision(TransportDecisionReason.UnknownSource);
                }

                // a catalog source without a profile inherits Defaults (the resolver returns the Defaults profile for it)
                profile = resolver.Resolve(canonicalName);
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

/// <summary>
/// The gate a data-path service uses when it was constructed without one (hand-built instances). Always <see cref="DenyAllTransportGate"/>
/// in production: the setter is <c>internal</c>, so only the test assemblies that see Application internals can install a different gate
/// (there is no configuration, option or public API that turns the check off).
/// </summary>
public static class TransportGateFallback
{
    public static IDataSourceTransportGate Current { get; internal set; } = DenyAllTransportGate.Instance;
}
