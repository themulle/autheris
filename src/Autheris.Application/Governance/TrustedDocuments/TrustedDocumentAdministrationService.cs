namespace Autheris.Application.Governance.TrustedDocuments;

using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Who changes the trusted documents: the immutable subject key and the scope that authorised the call (SEC-281-9).</summary>
public sealed record TrustedDocumentActor(string SubjectKey, string Scope);

/// <summary>What a PUT changed (counts only; the hashes are in the audit row).</summary>
public sealed record TrustedDocumentPutResult(int Added, int Removed, int Unchanged, bool Created);

/// <summary>A refused change; <see cref="Code"/> is stable and the message never contains a submitted value.</summary>
public sealed class TrustedDocumentAdminException : Exception
{
    public const string InvalidAppId = "InvalidAppId";
    public const string InvalidVersion = "InvalidVersion";
    public const string InvalidHash = "InvalidHash";
    public const string InvalidHashCount = "InvalidHashCount";
    public const string InvalidOrigin = "InvalidOrigin";
    public const string InvalidOriginCount = "InvalidOriginCount";
    public const string VersionRetired = "VersionRetired";
    public const string VersionLimit = "VersionLimit";

    public TrustedDocumentAdminException(string code, string message, bool isConflict = false) : base(message)
    {
        Code = code;
        IsConflict = isConflict;
    }

    public string Code { get; }

    /// <summary>The request is valid but conflicts with the stored state (HTTP 409); otherwise the input is invalid (HTTP 400).</summary>
    public bool IsConflict { get; }
}

/// <summary>
/// The rules for changing the trusted documents (Issue #281): validated server-side (SEC-281-8), stored with a generation bump in one
/// transaction, the local registry invalidated before the audit write, and a required audit row for every change including no-ops (SEC-281-9).
/// </summary>
public sealed class TrustedDocumentAdministrationService
{
    /// <summary>Longest hash/origin list written into an audit row; longer lists are recorded as counts only.</summary>
    public const int MaxAuditedListLength = 500;

    private readonly ITrustedDocumentRepository _repository;
    private readonly ITrustedDocumentAppRegistry _registry;
    private readonly IAuditWriter _audit;
    private readonly AppScopedTrustedDocumentsOptions _options;
    private readonly TimeProvider _time;

    public TrustedDocumentAdministrationService(
        ITrustedDocumentRepository repository,
        ITrustedDocumentAppRegistry registry,
        IAuditWriter audit,
        IOptions<GatewayOptions> options,
        TimeProvider? time = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = (options?.Value ?? new GatewayOptions()).GraphQL.AppScopedTrustedDocuments;
        _time = time ?? TimeProvider.System;
    }

    public async Task<TrustedDocumentAppView?> GetAppAsync(string appId, CancellationToken ct = default)
    {
        if (!AppIdentity.IsValidAppId(appId))
        {
            throw new TrustedDocumentAdminException(TrustedDocumentAdminException.InvalidAppId, "The app id is not acceptable.");
        }

        return await _repository.GetTrustedDocumentAppAsync(appId, ct).ConfigureAwait(false);
    }

    public async Task<TrustedDocumentPutResult> PutAsync(
        string appId, string version, IReadOnlyList<string>? hashes, IReadOnlyList<string>? allowedOrigins, TrustedDocumentActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateIdentity(appId, version);
        var normalizedHashes = NormalizeHashes(hashes);
        var normalizedOrigins = NormalizeOrigins(allowedOrigins);

        TrustedDocumentSetDelta delta;
        try
        {
            delta = await _repository.ReplaceTrustedDocumentVersionAsync(
                appId, version, normalizedOrigins, normalizedHashes, _options.MaxActiveVersionsPerApp, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (TrustedDocumentVersionRetiredException)
        {
            throw new TrustedDocumentAdminException(TrustedDocumentAdminException.VersionRetired, "The version is retired.", isConflict: true);
        }
        catch (TrustedDocumentVersionLimitException)
        {
            throw new TrustedDocumentAdminException(TrustedDocumentAdminException.VersionLimit, "The app has the maximum number of active versions.", isConflict: true);
        }

        _registry.Invalidate();   // effective on this instance before the call returns, even when the audit write fails below
        await AuditAsync(AuditEventTypes.TrustedDocumentsReplaced, appId, version, actor, new Dictionary<string, object?>
        {
            ["outcome"] = delta.Created ? "created" : "replaced",
            ["addedCount"] = delta.Added.Count,
            ["removedCount"] = delta.Removed.Count,
            ["unchangedCount"] = delta.Unchanged,
            ["originsChanged"] = delta.OriginsChanged,
            ["added"] = Capped(delta.Added),
            ["removed"] = Capped(delta.Removed)
        }, ct).ConfigureAwait(false);
        return new TrustedDocumentPutResult(delta.Added.Count, delta.Removed.Count, delta.Unchanged, delta.Created);
    }

    public async Task<TrustedDocumentRetireOutcome> RetireAsync(string appId, string version, TrustedDocumentActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateIdentity(appId, version);
        var outcome = await _repository.RetireTrustedDocumentVersionAsync(appId, version, _time.GetUtcNow(), ct).ConfigureAwait(false);
        _registry.Invalidate();
        await AuditAsync(AuditEventTypes.TrustedDocumentsRetired, appId, version, actor, new Dictionary<string, object?>
        {
            ["outcome"] = outcome.ToString()
        }, ct).ConfigureAwait(false);
        return outcome;
    }

    private static void ValidateIdentity(string appId, string version)
    {
        if (!AppIdentity.IsValidAppId(appId))
        {
            throw new TrustedDocumentAdminException(TrustedDocumentAdminException.InvalidAppId, "The app id is not acceptable.");
        }

        if (!AppIdentity.IsValidVersion(version))
        {
            throw new TrustedDocumentAdminException(TrustedDocumentAdminException.InvalidVersion, "The version is not acceptable.");
        }
    }

    private List<string> NormalizeHashes(IReadOnlyList<string>? hashes)
    {
        if (hashes is null || hashes.Count == 0 || hashes.Count > _options.MaxHashesPerVersion)
        {
            throw new TrustedDocumentAdminException(TrustedDocumentAdminException.InvalidHashCount, "The number of hashes is out of range.");
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hash in hashes)
        {
            if (!TrustedDocumentHash.TryNormalize(hash, out var normalized))
            {
                throw new TrustedDocumentAdminException(TrustedDocumentAdminException.InvalidHash, "A hash is not a 64 character hexadecimal SHA-256.");
            }

            set.Add(normalized);
        }

        return [.. set.Order(StringComparer.Ordinal)];
    }

    private List<string> NormalizeOrigins(IReadOnlyList<string>? origins)
    {
        if (origins is null || origins.Count == 0 || origins.Count > _options.MaxAllowedOriginsPerApp)
        {
            throw new TrustedDocumentAdminException(TrustedDocumentAdminException.InvalidOriginCount, "The number of allowed origins is out of range.");
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in origins)
        {
            if (!AllowedOrigin.TryNormalize(origin, out var normalized))
            {
                throw new TrustedDocumentAdminException(TrustedDocumentAdminException.InvalidOrigin, "An allowed origin is not acceptable.");
            }

            set.Add(normalized);
        }

        return [.. set.Order(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string>? Capped(IReadOnlyList<string> list) => list.Count <= MaxAuditedListLength ? list : null;

    private Task AuditAsync(string eventType, string appId, string version, TrustedDocumentActor actor, Dictionary<string, object?> details, CancellationToken ct)
    {
        details["appId"] = appId;
        details["version"] = version;
        details["actor"] = actor.SubjectKey;
        details["scope"] = actor.Scope;
        return _audit.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = TenantId.LegacySingleTenant,
            EventType = eventType,
            ActorSid = new Sid(actor.SubjectKey),
            TargetTable = $"trusted-documents:{appId}/{version}",
            Decision = "ALLOW",
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(details),
            OccurredAt = _time.GetUtcNow()
        }, ct);
    }
}
