namespace Autheris.Application.Events.Services;

using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class CdcWebhooksOptionsValidator : IValidateOptions<CdcWebhooksOptions>
{
    public ValidateOptionsResult Validate(string? name, CdcWebhooksOptions options)
    {
        var failures = new List<string>();
        Range(failures, nameof(options.MaxSubscriptionsPerTenant), options.MaxSubscriptionsPerTenant, 1, 10_000);
        Range(failures, nameof(options.SubscriptionCacheSeconds), options.SubscriptionCacheSeconds, 0, 3600);
        Range(failures, nameof(options.CreatorSnapshotMaxAgeDays), options.CreatorSnapshotMaxAgeDays, 0, 3650);
        Range(failures, nameof(options.MaxSnapshotGroups), options.MaxSnapshotGroups, 1, 4096);
        Range(failures, nameof(options.AuditBatchSeconds), options.AuditBatchSeconds, 1, 3600);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Range(List<string> failures, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            failures.Add($"{CdcWebhooksOptions.SectionName}:{name} must be between {min} and {max}.");
        }
    }
}
