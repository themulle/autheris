namespace Autheris.Domain.Options;

/// <summary>
/// Bounded options for the transactional consent event outbox and CloudEvents webhook dispatcher,
/// bound to <c>Gateway:Governance:ConsentEvents</c>.
/// </summary>
public sealed class ConsentEventsOptions
{
    public const string SectionName = "Gateway:Governance:ConsentEvents";

    /// <summary>Maximum delivery attempts before a consent webhook delivery is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>Retention window in days for stored <c>CONSENT_EVENTS</c> rows and cursor feeds.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Polling interval in seconds for claiming pending consent webhook deliveries.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum number of pending deliveries claimed in one batch.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Lease duration in seconds held by a dispatcher worker while delivering a batch.</summary>
    public int LeaseSeconds { get; set; } = 60;

    public int EffectiveMaxAttempts => Math.Clamp(MaxAttempts, 1, 100);
    public int EffectiveRetentionDays => Math.Clamp(RetentionDays, 1, 3650);
    public int EffectivePollIntervalSeconds => Math.Clamp(PollIntervalSeconds, 1, 300);
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 500);
    public TimeSpan EffectiveLease => TimeSpan.FromSeconds(Math.Clamp(LeaseSeconds, 10, 600));
}
