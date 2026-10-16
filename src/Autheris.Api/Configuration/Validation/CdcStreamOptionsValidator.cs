using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// REQ-CDC-99 (PLAN-CDC-99 section 3.4): validates the change tracking producer and the governed CDC stream options. Violations fail
/// start-up. Failure messages name option paths only, never values (a SecretRef is a secret name, but it is still not echoed).
/// </summary>
public sealed class CdcStreamOptionsValidator : IValidateOptions<GatewayOptions>
{
    /// <summary>Secret name of the webhook KEK; stream KEKs must be distinct secrets (SEC-CDC99-14).</summary>
    public const string WebhookKekSecretName = "cdc-webhook-kek";

    private readonly IHostEnvironment _environment;

    public CdcStreamOptionsValidator(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();
        ValidateProducer(opts.MssqlChangeTracking, failures);
        ValidateStream(opts.CdcStream, failures);
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateProducer(MssqlChangeTrackingOptions o, List<string> failures)
    {
        const string p = "Gateway:MssqlChangeTracking";
        CheckRange(o.PollingIntervalMilliseconds, 250, int.MaxValue, $"{p}:PollingIntervalMilliseconds", failures);
        CheckRange(o.BatchSize, 1, 5000, $"{p}:BatchSize", failures);
        CheckRange(o.QueryTimeoutSeconds, 1, 300, $"{p}:QueryTimeoutSeconds", failures);
        if (!Enum.IsDefined(o.OnWatermarkExpired))
        {
            failures.Add($"{p}:OnWatermarkExpired must be Restart or Halt.");
        }

        var seen = new HashSet<TableIdentifier>();
        foreach (var shorthand in o.TrackedTables)
        {
            if (!TryTable(shorthand, out var id))
            {
                failures.Add($"{p}:TrackedTables contains an entry that is not a qualified 'domain.schema.table' name.");
            }
            else if (!seen.Add(id))
            {
                failures.Add($"{p}:TrackedTables contains a duplicate table.");
            }
        }

        var overridden = new HashSet<TableIdentifier>();
        for (var i = 0; i < o.Tables.Count; i++)
        {
            var t = o.Tables[i];
            var tp = $"{p}:Tables:{i}";
            if (!TryTable(t.Table, out var id))
            {
                failures.Add($"{tp}:Table must be a qualified 'domain.schema.table' name.");
            }
            else if (!overridden.Add(id))
            {
                failures.Add($"{tp}:Table duplicates another entry of {p}:Tables.");
            }

            CheckOptionalRange(t.PollingIntervalMilliseconds, 250, int.MaxValue, $"{tp}:PollingIntervalMilliseconds", failures);
            CheckOptionalRange(t.BatchSize, 1, 5000, $"{tp}:BatchSize", failures);
            CheckOptionalRange(t.QueryTimeoutSeconds, 1, 300, $"{tp}:QueryTimeoutSeconds", failures);
            CheckOptionalRange(t.RetentionHours, 1, int.MaxValue, $"{tp}:RetentionHours", failures);
            CheckOptionalRange(t.RetentionMaxEvents, 1, int.MaxValue, $"{tp}:RetentionMaxEvents", failures);
            if (t.OnWatermarkExpired is { } w && !Enum.IsDefined(w))
            {
                failures.Add($"{tp}:OnWatermarkExpired must be Restart or Halt.");
            }
        }
    }

    private void ValidateStream(CdcStreamOptions o, List<string> failures)
    {
        const string p = "Gateway:CdcStream";
        CheckRange(o.RetentionHours, 1, int.MaxValue, $"{p}:RetentionHours", failures);
        CheckRange(o.RetentionMaxEvents, 1, long.MaxValue, $"{p}:RetentionMaxEvents", failures);
        CheckRange(o.RetentionSweepSeconds, 1, int.MaxValue, $"{p}:RetentionSweepSeconds", failures);
        CheckRange(o.RetentionDeleteBatch, 1, 100_000, $"{p}:RetentionDeleteBatch", failures);
        CheckRange(o.MaxEventPayloadBytes, 1_024, 16 * 1_024 * 1_024, $"{p}:MaxEventPayloadBytes", failures);
        CheckRange(o.TailPollIntervalMilliseconds, 50, int.MaxValue, $"{p}:TailPollIntervalMilliseconds", failures);
        CheckRange(o.SubscriberBufferSize, 1, 100_000, $"{p}:SubscriberBufferSize", failures);
        CheckRange(o.GraphQlResumeWindowSeconds, 1, int.MaxValue, $"{p}:GraphQlResumeWindowSeconds", failures);
        CheckRange(o.MaxConcurrentCatchUps, 1, 1_000, $"{p}:MaxConcurrentCatchUps", failures);

        var e = o.Encryption;
        CheckRange(e.DekRotationHours, 1, int.MaxValue, $"{p}:Encryption:DekRotationHours", failures);
        CheckRange(e.MaxEventsPerDek, 1, long.MaxValue, $"{p}:Encryption:MaxEventsPerDek", failures);
        CheckRange(e.MaxCachedDeks, 1, 100_000, $"{p}:Encryption:MaxCachedDeks", failures);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kek in e.Keks)
        {
            if (string.IsNullOrWhiteSpace(kek.Id) || string.IsNullOrWhiteSpace(kek.SecretRef))
            {
                failures.Add($"{p}:Encryption:Keks entries require Id and SecretRef.");
                continue;
            }

            if (!ids.Add(kek.Id))
            {
                failures.Add($"{p}:Encryption:Keks contains a duplicate Id.");
            }

            if (string.Equals(kek.SecretRef, WebhookKekSecretName, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{p}:Encryption:Keks must not reuse the webhook KEK secret (SEC-CDC99-14).");
            }
        }

        if (!string.IsNullOrWhiteSpace(e.ActiveKekId) && !ids.Contains(e.ActiveKekId))
        {
            failures.Add($"{p}:Encryption:ActiveKekId must name an entry of {p}:Encryption:Keks.");
        }

        var c = o.Consumers;
        CheckRange(c.MaxConsumersPerStream, 1, 100_000, $"{p}:Consumers:MaxConsumersPerStream", failures);
        CheckRange(c.MaxInFlight, 1, 10_000, $"{p}:Consumers:MaxInFlight", failures);
        CheckRange(c.MaxAttempts, 1, 1_000, $"{p}:Consumers:MaxAttempts", failures);
        CheckRange(c.MaxConsecutiveFailures, 1, 100_000, $"{p}:Consumers:MaxConsecutiveFailures", failures);
        CheckRange(c.HandlerTimeoutSeconds, 1, 3_600, $"{p}:Consumers:HandlerTimeoutSeconds", failures);
        CheckRange(c.MaxAbandonedHandlers, 0, 1_000, $"{p}:Consumers:MaxAbandonedHandlers", failures);

        // SEC-CDC99-07: 0 (no expiry) is a Development convenience only.
        var minAge = _environment.IsDevelopment() ? 0 : 1;
        CheckRange(c.IdentityMaxAgeDays, minAge, 365, $"{p}:Consumers:IdentityMaxAgeDays", failures);
    }

    private static bool TryTable(string? value, out TableIdentifier id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value) || value.Split('.').Length != 3)
        {
            return false;
        }

        return TableIdentifier.TryParse(value, out id);
    }

    private static void CheckRange(long value, long min, long max, string path, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{path} is out of range ({min}..{max}).");
        }
    }

    private static void CheckOptionalRange(long? value, long min, long max, string path, List<string> failures)
    {
        if (value is { } v)
        {
            CheckRange(v, min, max, path, failures);
        }
    }
}
