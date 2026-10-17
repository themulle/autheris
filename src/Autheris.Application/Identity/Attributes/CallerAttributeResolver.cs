namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching;
using Autheris.Application.Sql.ListParameters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// The only caller of <see cref="ICallerAttributeProvider"/> (PLAN-GOV-129 A-2, A-6, A-11). Per attribute: registry lookup, routing by
/// subject kind (an application subject reads service sets only, a user never does), request memo, value cache (single flight, key
/// material hashed and length-prefixed with subject kind, tenant, issuer and definition hash), bulkhead and circuit breaker per provider
/// kind (only infrastructure failures count), provider timeout, normalization through <see cref="ListParameterNormalizer"/> and the
/// cardinality cap (never truncated). Every non-resolved state is a result, never an exception; no value reaches a log or a message.
/// </summary>
public sealed class CallerAttributeResolver : ICallerAttributeResolver, ICallerAttributeCacheInvalidator, IDisposable
{
    private const string ServiceSetKind = CallerAttributeProviderBinding.ServiceSet;
    private const string CacheDomain = "cav/v1";

    private readonly ICallerAttributeRegistry _registry;
    private readonly Dictionary<string, ICallerAttributeProvider> _providers;
    private readonly CallerAttributeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CallerAttributeResolver>? _logger;
    private readonly SingleFlightKeySetCache<ResolvedAttributeEntry> _cache;
    private readonly ConcurrentDictionary<string, long> _attributeEpochs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _subjectEpochs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Breaker> _breakers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _bulkheads = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<ClaimsPrincipal, CallerAttributeRequestState> _states = new();

    public CallerAttributeResolver(
        ICallerAttributeRegistry registry,
        IEnumerable<ICallerAttributeProvider> providers,
        IOptions<GatewayOptions> options,
        TimeProvider? time = null,
        ILogger<CallerAttributeResolver>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _providers = (providers ?? []).GroupBy(p => p.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        _options = options?.Value?.CallerAttributes ?? new CallerAttributeOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _cache = new SingleFlightKeySetCache<ResolvedAttributeEntry>(_options.Cache.SizeLimitBytes, _time);
    }

    public bool IsEnabled => _options.Enabled;

    public CallerAttributePadding Padding => new(_options.PadListsToBuckets, NextPowerOfTwo(_options.MaxValuesHardCap));

    public int BindReserve => _options.BindReserve;

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value && result < (1 << 20))
        {
            result <<= 1;
        }

        return result;
    }

    public void Dispose() => _cache.Dispose();

    public CallerSubject CreateSubject(ClaimsPrincipal? principal, TenantId tenant, bool longLived = false)
    {
        var subject = CallerSubjectFactory.Create(principal, tenant, _options);
        if (principal is null || longLived || subject.Kind == CallerSubjectKind.None)
        {
            return subject;
        }

        return subject with { RequestState = _states.GetValue(principal, static _ => new CallerAttributeRequestState()) };
    }

    public void InvalidateAttribute(TenantId tenant, string attributeName) =>
        _attributeEpochs.AddOrUpdate(EpochKey(tenant.Value, attributeName), 1, static (_, v) => v + 1);

    public void InvalidateSubject(TenantId tenant, string subjectKey) =>
        _subjectEpochs.AddOrUpdate(EpochKey(tenant.Value, subjectKey.ToLowerInvariant()), 1, static (_, v) => v + 1);

    public async ValueTask<CallerAttributeSet> ResolveAsync(CallerSubject subject, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return CallerAttributeSet.Empty;
        }

        var results = new Dictionary<string, CallerAttributeResult>(StringComparer.Ordinal);
        if (!_options.Enabled)
        {
            foreach (var name in names)
            {
                results[name] = CallerAttributeResult.Unknown();
            }

            return new CallerAttributeSet(results);
        }

        CallerAttributeSnapshot snapshot;
        try
        {
            snapshot = await _registry.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning("Caller attribute registry unavailable ({ExceptionType}).", ex.GetType().Name);
            foreach (var name in names)
            {
                results[name] = CallerAttributeResult.Failed("RegistryUnavailable");
            }

            return new CallerAttributeSet(results);
        }

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            results[name] = await ResolveOneAsync(subject, name, snapshot, ct).ConfigureAwait(false);
        }

        return new CallerAttributeSet(results);
    }

    private async ValueTask<CallerAttributeResult> ResolveOneAsync(CallerSubject subject, string name, CallerAttributeSnapshot snapshot, CancellationToken ct)
    {
        if (subject.Kind == CallerSubjectKind.None)
        {
            return CallerAttributeResult.NoSubject();
        }

        if (!snapshot.TryGetDefinition(subject.Tenant, name, out var definition))
        {
            return CallerAttributeResult.Unknown();
        }

        var state = subject.RequestState;
        if (state is null)
        {
            return await ResolveCoreAsync(subject, definition, ct).ConfigureAwait(false);
        }

        string memoKey = EpochKey(subject.Tenant.Value, name);
        string hash = definition.ComputeDefinitionHash();
        var now = _time.GetUtcNow();
        var bound = TimeSpan.FromSeconds(Math.Min(definition.TtlSeconds, _options.RequestMemoSeconds));
        if (state.TryGet(memoKey, out var entry) && entry.DefinitionHash == hash && entry.Generation == snapshot.Generation &&
            now - entry.CreatedAt < bound && !entry.Result.IsCanceled && !entry.Result.IsFaulted)
        {
            return await entry.Result.WaitAsync(ct).ConfigureAwait(false);
        }

        var task = ResolveCoreAsync(subject, definition, ct).AsTask();
        state.Set(memoKey, new CallerAttributeRequestState.Entry(now, hash, snapshot.Generation, task));
        return await task.ConfigureAwait(false);
    }

    private async ValueTask<CallerAttributeResult> ResolveCoreAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
    {
        string kind = subject.Kind == CallerSubjectKind.Application ? ServiceSetKind : definition.Provider.Kind;
        if (subject.Kind == CallerSubjectKind.User && string.Equals(kind, ServiceSetKind, StringComparison.Ordinal))
        {
            return CallerAttributeResult.Missing("ServiceSetForUser");
        }

        if (!_providers.TryGetValue(kind, out var provider))
        {
            return CallerAttributeResult.Failed("ProviderUnavailable");
        }

        int max = Math.Max(1, Math.Min(definition.MaxValues, _options.MaxValuesHardCap));
        try
        {
            if (provider is INonCachingCallerAttributeProvider)
            {
                var raw = await provider.ResolveAsync(subject, definition, ct).ConfigureAwait(false);
                return Normalize(raw, definition, max);
            }

            string key = CacheKey(subject, definition, kind);
            var entry = await _cache.GetOrLoadAsync(
                key,
                loadCt => LoadAsync(subject, definition, provider, kind, key, max, loadCt),
                ct,
                TimeSpan.FromMilliseconds(_options.ProviderTimeoutMilliseconds),
                accept: e => string.Equals(e.KeyHash, key, StringComparison.Ordinal)).ConfigureAwait(false);
            return entry.Result;
        }
        catch (CallerAttributeLoadException ex)
        {
            return ex.Result;
        }
        catch (TimeoutException)
        {
            return CallerAttributeResult.Failed("Timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Caller attribute provider '{Kind}' failed ({ExceptionType}).", kind, ex.GetType().Name);
            return CallerAttributeResult.Failed("SourceUnavailable");
        }
    }

    private async Task<SingleFlightLoadResult<ResolvedAttributeEntry>> LoadAsync(
        CallerSubject subject, CallerAttributeDefinition definition, ICallerAttributeProvider provider, string kind, string key, int max, CancellationToken ct)
    {
        var breaker = _breakers.GetOrAdd(kind, _ => new Breaker());
        if (!breaker.TryEnter(_time))
        {
            throw new CallerAttributeLoadException(CallerAttributeResult.Failed("CircuitOpen"));
        }

        var bulkhead = _bulkheads.GetOrAdd(kind, _ => new SemaphoreSlim(_options.ProviderMaxConcurrency, _options.ProviderMaxConcurrency));
        if (!await bulkhead.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            breaker.Release();
            throw new CallerAttributeLoadException(CallerAttributeResult.Failed("Bulkhead"));
        }

        CallerAttributeResult raw;
        try
        {
            raw = await provider.ResolveAsync(subject, definition, ct).ConfigureAwait(false);
            breaker.Success();
        }
        catch (Exception)
        {
            breaker.Failure(_time, _options.CircuitBreaker.FailureThreshold, _options.CircuitBreaker.OpenSeconds);   // exceptions and timeouts only
            throw;
        }
        finally
        {
            bulkhead.Release();
        }

        var result = Normalize(raw, definition, max);
        if (result.Outcome is not (CallerAttributeOutcome.Resolved or CallerAttributeOutcome.Missing))
        {
            throw new CallerAttributeLoadException(result);   // failures are never cached
        }

        int ttl = Math.Min(definition.TtlSeconds, Math.Min(_options.MaxTtlSeconds, CallerAttributeOptions.TtlCeilingSeconds));
        if (result.Outcome == CallerAttributeOutcome.Missing)
        {
            ttl = Math.Min(ttl, _options.NegativeTtlSeconds);
        }

        long size = (result.Value?.EstimatedBytes ?? 64) + key.Length;
        return new SingleFlightLoadResult<ResolvedAttributeEntry>(new ResolvedAttributeEntry(key, result), _time.GetUtcNow().AddSeconds(ttl), size);
    }

    private static CallerAttributeResult Normalize(CallerAttributeResult raw, CallerAttributeDefinition definition, int max)
    {
        if (raw.Outcome != CallerAttributeOutcome.Resolved || raw.Value is null)
        {
            return raw.Outcome == CallerAttributeOutcome.Resolved ? CallerAttributeResult.Missing() : raw;
        }

        if (raw.Value.Count == 0)
        {
            return CallerAttributeResult.Missing();
        }

        var elements = new List<object?>(raw.Value.Count);
        for (int i = 0; i < raw.Value.Count; i++)
        {
            elements.Add(raw.Value[i]);
        }

        SqlParameterType declared = definition.ValueType switch
        {
            CallerAttributeType.Whole => SqlParameterType.Int64,
            CallerAttributeType.Number => SqlParameterType.Decimal,
            CallerAttributeType.Flag => SqlParameterType.Boolean,
            _ => SqlParameterType.String
        };

        ClientListValue typed;
        try
        {
            typed = ListParameterNormalizer.Normalize(elements, new ListParameterLimits(max, 1), declared, guidStrings: definition.ValueType == CallerAttributeType.Uuid);
        }
        catch (ClientListException ex)
        {
            if (ex.Code == GovernedSqlErrorCodes.ListParamTooLarge)
            {
                return definition.Cardinality == CallerAttributeCardinality.Scalar ? CallerAttributeResult.Failed("Ambiguous") : CallerAttributeResult.OverCardinality();
            }

            return CallerAttributeResult.Failed("NotNormalizable");
        }

        if (typed.Count == 0)
        {
            return CallerAttributeResult.Missing();
        }

        if (typed.Count > max)
        {
            return CallerAttributeResult.OverCardinality();
        }

        if (definition.Cardinality == CallerAttributeCardinality.Scalar && typed.Count > 1)
        {
            return CallerAttributeResult.Failed("Ambiguous");
        }

        var values = new object[typed.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = typed[i];
        }

        return CallerAttributeResult.Resolved(new CallerAttributeValue(values, typed.ElementType));
    }

    /// <summary>
    /// SHA-256 over length-prefixed material (BC-129-08): tenant, issuer, subject kind, subject key kind and key (or application id), attribute,
    /// definition hash and the epochs of the attribute and the subject. The hex form is 64 characters, never truncated.
    /// </summary>
    private string CacheKey(CallerSubject subject, CallerAttributeDefinition definition, string providerKind)
    {
        string keyKind;
        string keyValue;
        if (subject.Kind == CallerSubjectKind.Application)
        {
            keyKind = "app";
            keyValue = subject.ApplicationId ?? string.Empty;
        }
        else
        {
            var kind = new[] { SubjectKeyKind.EntraObjectId, SubjectKeyKind.WindowsSid, SubjectKeyKind.Subject }.FirstOrDefault(k => subject.KeyFor(k) is not null);
            keyKind = kind.ToString();
            keyValue = subject.KeyFor(kind) ?? string.Empty;
        }

        _attributeEpochs.TryGetValue(EpochKey(subject.Tenant.Value, definition.Name), out long attributeEpoch);
        _subjectEpochs.TryGetValue(EpochKey(subject.Tenant.Value, keyValue.ToLowerInvariant()), out long subjectEpoch);

        var sb = new StringBuilder(CacheDomain);
        foreach (var part in new[]
        {
            subject.Tenant.Value, subject.Issuer ?? string.Empty, subject.Kind.ToString(), providerKind, keyKind, keyValue, definition.Name,
            definition.ComputeDefinitionHash(), attributeEpoch.ToString(CultureInfo.InvariantCulture), subjectEpoch.ToString(CultureInfo.InvariantCulture)
        })
        {
            sb.Append('|').Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string EpochKey(string tenant, string name) => $"{tenant.Length}:{tenant}|{name}";

    /// <summary>A cached result with the key hash it was stored under (defensive equality check on read).</summary>
    internal sealed record ResolvedAttributeEntry(string KeyHash, CallerAttributeResult Result);

    /// <summary>A non-cacheable result: thrown out of the shared load so that nothing is stored and every waiter sees it.</summary>
    private sealed class CallerAttributeLoadException(CallerAttributeResult result) : Exception("Caller attribute not cacheable")
    {
        public CallerAttributeResult Result { get; } = result;
    }

    /// <summary>Opens after N consecutive infrastructure failures for <c>OpenSeconds</c>; then lets one trial through (half-open).</summary>
    private sealed class Breaker
    {
        private readonly object _gate = new();
        private int _failures;
        private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
        private bool _trial;

        public bool TryEnter(TimeProvider time)
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                if (_openUntil == DateTimeOffset.MinValue)
                {
                    return true;
                }

                if (now < _openUntil)
                {
                    return false;
                }

                if (_trial)
                {
                    return false;
                }

                _trial = true;   // half-open: exactly one call tests the source
                return true;
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _trial = false;
            }
        }

        public void Success()
        {
            lock (_gate)
            {
                _failures = 0;
                _openUntil = DateTimeOffset.MinValue;
                _trial = false;
            }
        }

        public void Failure(TimeProvider time, int threshold, int openSeconds)
        {
            lock (_gate)
            {
                _trial = false;
                _failures++;
                if (_failures >= threshold || _openUntil != DateTimeOffset.MinValue)
                {
                    _openUntil = time.GetUtcNow().AddSeconds(openSeconds);
                }
            }
        }
    }
}
