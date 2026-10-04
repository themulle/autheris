namespace Autheris.Extensions.Lineage;

using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;

public sealed partial class OpenJevClient : IOpenJevClient
{
    [GeneratedRegex(@"[\x00-\x1F\x7F]")]
    private static partial Regex ControlCharsRegex();

    private readonly HttpClient? _httpClient;
    private readonly ILogger<OpenJevClient> _logger;
    private readonly ConcurrentDictionary<string, UserRateLimitState> _rateLimits = new();

    public OpenJevClient(ILogger<OpenJevClient> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<JustificationTriageResult> ClassifyJustificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string justificationText,
        CancellationToken ct = default)
    {
        // 1. Input Sanitization & Bounds
        ArgumentNullException.ThrowIfNull(justificationText);

        if (justificationText.Length > 500)
        {
            justificationText = justificationText[..500];
        }

        // Bereinigung von Steuerzeichen
        justificationText = ControlCharsRegex().Replace(justificationText, " ").Trim();

        // 2. Per-User Rate Limiting (Token Bucket: max 20 per minute)
        if (!CheckRateLimit(userSid.Value))
        {
            _logger.LogWarning("OpenJev Rate Limit überschritten für User {UserSid}", userSid.Value);
            return new JustificationTriageResult(
                JustificationCategory.Unclassified,
                0.0,
                "Rate limit exceeded - routed to manual review",
                AutoGrantEligible: false,
                GrantedDuration: null);
        }

        // 3. 120 ms Timeout Guard (Defense against DoS)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(120));

        try
        {
            return await ExecuteClassificationAsync(tenant, userSid, table, justificationText, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OpenJev Klassifikator Timeout (> 120 ms) für User {UserSid}. Fallback auf UNCLASSIFIED.", userSid.Value);
            return new JustificationTriageResult(
                JustificationCategory.Unclassified,
                0.0,
                "Classifier timeout (>120ms) - fallback to human 4-eyes approval",
                AutoGrantEligible: false,
                GrantedDuration: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenJev Klassifikator Fehler für User {UserSid}. Fallback auf UNCLASSIFIED.", userSid.Value);
            return new JustificationTriageResult(
                JustificationCategory.Unclassified,
                0.0,
                $"Classifier error ({ex.Message}) - fallback to human 4-eyes approval",
                AutoGrantEligible: false,
                GrantedDuration: null);
        }
    }

    private Task<JustificationTriageResult> ExecuteClassificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string text,
        CancellationToken ct)
    {
        // Deterministic Security Analysis against Prompt Injection & Jailbreaks
        var lower = text.ToLowerInvariant();

        // Adversarial Injection Patterns
        if (lower.Contains("ignore previous instructions") ||
            lower.Contains("disregard all previous") ||
            lower.Contains("system override") ||
            lower.Contains("override all") ||
            lower.Contains("system:") ||
            lower.Contains("<system>") ||
            lower.Contains("maintenance mode") ||
            lower.Contains("grant full access") ||
            lower.Contains("bypass") ||
            lower.Contains("jailbreak") ||
            lower.Contains("you are now") ||
            lower.Contains("as an administrator") ||
            lower.Contains("sudo grant") ||
            lower.Contains("prompt leakage"))
        {
            var hashPrefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
            _logger.LogWarning("Prompt-Injection erkannt in Justification für User {UserSid}. PatternCategory: SuspiciousExfiltration, TextLength: {Length}, HashPrefix: {Hash}",
                userSid.Value, text.Length, hashPrefix);
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.SuspiciousExfiltration,
                0.99,
                "Adversarial prompt injection pattern detected",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        if (lower.Contains("exfiltration") || lower.Contains("dump database") || lower.Contains("leak") || lower.Contains("export all credit cards"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.SuspiciousExfiltration,
                0.95,
                "Suspicious data exfiltration intent detected",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        if (lower.Contains("audit") || lower.Contains("compliance") || lower.Contains("regulatory") || lower.Contains("sox") || lower.Contains("finma"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.LegitimateAudit,
                0.98,
                "Classified as legitimate compliance audit",
                AutoGrantEligible: false, // Entschieden durch JustificationTriageService je nach Tabellen-Opt-In
                GrantedDuration: null));
        }

        if (lower.Contains("incident") || lower.Contains("outage") || lower.Contains("p1") || lower.Contains("sev-1") || lower.Contains("emergency triage"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.IncidentTriage,
                0.98,
                "Classified as urgent incident triage",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        if (text.Length < 10 || lower.Contains("test") || lower.Contains("asdf") || lower.Contains("please give access"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.Unjustified,
                0.90,
                "Insufficient justification provided",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        return Task.FromResult(new JustificationTriageResult(
            JustificationCategory.Unclassified,
            0.50,
            "Standard request - human review required",
            AutoGrantEligible: false,
            GrantedDuration: null));
    }

    private long _requestCounter;

    private bool CheckRateLimit(string userSid)
    {
        var now = DateTimeOffset.UtcNow;

        if (Interlocked.Increment(ref _requestCounter) % 100 == 0 && _rateLimits.Count > 500)
        {
            EvictStaleRateLimitEntries(now);
        }

        var state = _rateLimits.GetOrAdd(userSid, _ => new UserRateLimitState());
        return state.TryConsume(now);
    }

    private void EvictStaleRateLimitEntries(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-5);
        foreach (var (key, state) in _rateLimits)
        {
            if (state.IsStale(cutoff))
            {
                _rateLimits.TryRemove(key, out _);
            }
        }
    }

    private sealed class UserRateLimitState
    {
        private readonly object _lock = new();
        private int _tokens = 20;
        private DateTimeOffset _lastRefill = DateTimeOffset.UtcNow;

        public bool TryConsume(DateTimeOffset now)
        {
            lock (_lock)
            {
                var elapsed = now - _lastRefill;
                int replenished = (int)(elapsed.TotalSeconds * (20.0 / 60.0));
                if (replenished > 0)
                {
                    _tokens = Math.Min(20, _tokens + replenished);
                    _lastRefill = now;
                }

                if (_tokens >= 1)
                {
                    _tokens--;
                    return true;
                }

                return false;
            }
        }

        public bool IsStale(DateTimeOffset cutoff)
        {
            lock (_lock)
            {
                return _lastRefill < cutoff;
            }
        }
    }
}
