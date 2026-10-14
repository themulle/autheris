namespace Autheris.Application.Security.Totp.Services;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Default <see cref="ITotpEnrollmentService"/>. Pending seeds live in the cluster state (or in-process without a
/// cluster provider), keyed by user SID, with a short TTL.
/// </summary>
public sealed class TotpEnrollmentService : ITotpEnrollmentService
{
    private readonly ITotpVerificationService _totpService;
    private readonly ITotpSecretStore _secretStore;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly TimeSpan _pendingTtl;
    private readonly ConcurrentDictionary<string, (string Secret, DateTimeOffset Expiry)> _localPending = new(StringComparer.OrdinalIgnoreCase);

    public TotpEnrollmentService(
        ITotpVerificationService totpService,
        ITotpSecretStore secretStore,
        IDistributedClusterStateProvider? clusterState = null,
        TotpOptions? options = null)
    {
        _totpService = totpService ?? throw new ArgumentNullException(nameof(totpService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _clusterState = clusterState;
        _pendingTtl = TimeSpan.FromMinutes(Math.Max(1, (options ?? new TotpOptions()).PendingEnrollmentMinutes));
    }

    public async Task<TotpEnrollmentResult?> BeginEnrollmentAsync(string userSid, string email, string issuer, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var enrollment = _totpService.GenerateEnrollment(userSid, email, issuer);

        var key = PendingKey(userSid);
        if (_clusterState != null)
        {
            try
            {
                await _clusterState.SetAsync(key, enrollment.SecretBase32, _pendingTtl, ct).ConfigureAwait(false);
            }
            catch
            {
                return null; // Fail closed: an enrollment that cannot be verified server-side must not be offered.
            }
        }
        else
        {
            _localPending[key] = (enrollment.SecretBase32, DateTimeOffset.UtcNow.Add(_pendingTtl));
        }

        return enrollment;
    }

    public async Task<bool> HasActiveSecretAsync(string userSid, CancellationToken ct = default) =>
        await _secretStore.HasSecretAsync(userSid, ct).ConfigureAwait(false);

    public async Task<TotpEnrollmentOutcome> CompleteEnrollmentAsync(
        string userSid,
        string totpCode,
        string? currentTotpCode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (string.IsNullOrWhiteSpace(totpCode))
        {
            return TotpEnrollmentOutcome.InvalidCode;
        }

        string? existing;
        string? pending;
        try
        {
            existing = await _secretStore.GetSecretAsync(userSid, ct).ConfigureAwait(false);
            pending = await GetPendingAsync(userSid, ct).ConfigureAwait(false);
        }
        catch
        {
            return TotpEnrollmentOutcome.StoreUnavailable;
        }

        if (string.IsNullOrWhiteSpace(pending))
        {
            return TotpEnrollmentOutcome.NoPendingEnrollment;
        }

        var replacing = !string.IsNullOrWhiteSpace(existing);
        if (replacing)
        {
            // Re-enrollment must be authorised by the secret that is being replaced.
            if (string.IsNullOrWhiteSpace(currentTotpCode))
            {
                return TotpEnrollmentOutcome.CurrentCodeRequired;
            }

            if (!await _totpService.VerifyAndConsumeTotpAsync(userSid, existing!, currentTotpCode, ct).ConfigureAwait(false))
            {
                return TotpEnrollmentOutcome.InvalidCurrentCode;
            }

            // The current code already consumed this user's time step and counted against the throttle; the new seed
            // is single-use (pending value is removed below), so a plain check is sufficient here.
            if (!_totpService.VerifyTotp(pending, totpCode))
            {
                return TotpEnrollmentOutcome.InvalidCode;
            }
        }
        else if (!await _totpService.VerifyAndConsumeTotpAsync(userSid, pending, totpCode, ct).ConfigureAwait(false))
        {
            return TotpEnrollmentOutcome.InvalidCode;
        }

        try
        {
            await _secretStore.SetSecretAsync(userSid, pending, ct).ConfigureAwait(false);
            await RemovePendingAsync(userSid, ct).ConfigureAwait(false);
        }
        catch
        {
            return TotpEnrollmentOutcome.StoreUnavailable;
        }

        return TotpEnrollmentOutcome.Completed;
    }

    private async Task<string?> GetPendingAsync(string userSid, CancellationToken ct)
    {
        var key = PendingKey(userSid);
        if (_clusterState != null)
        {
            return await _clusterState.GetAsync<string>(key, ct).ConfigureAwait(false);
        }

        if (_localPending.TryGetValue(key, out var entry))
        {
            if (entry.Expiry > DateTimeOffset.UtcNow)
            {
                return entry.Secret;
            }

            _localPending.TryRemove(key, out _);
        }

        return null;
    }

    private async Task RemovePendingAsync(string userSid, CancellationToken ct)
    {
        var key = PendingKey(userSid);
        _localPending.TryRemove(key, out _);
        if (_clusterState != null)
        {
            await _clusterState.RemoveAsync(key, ct).ConfigureAwait(false);
        }
    }

    private static string PendingKey(string userSid) => $"totp:pending:{userSid.Trim().ToLowerInvariant()}";
}
