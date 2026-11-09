namespace Autheris.Application.Usage;

using System;
using Autheris.Domain.Common;

public sealed class UsageRecorder : IUsageRecorder
{
    private readonly IUsageAggregator _aggregator;

    public UsageRecorder(IUsageAggregator aggregator)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        UsageRequestScope.DefaultAggregator ??= aggregator;
    }

    public void Record(in UsageFact fact, TenantId tenant, Sid actor)
    {
        try
        {
            var currentScope = UsageRequestScope.Current;
            if (currentScope != null &&
                currentScope.Identity.Tenant == tenant &&
                currentScope.Identity.UserSid == actor)
            {
                currentScope.AddFact(fact);
                return;
            }

            UsageDiagnostics.UnscopedCounter.Add(1);
            _aggregator.RecordUnscoped(tenant, actor, fact);
        }
        catch
        {
            UsageDiagnostics.RecordErrorsCounter.Add(1);
        }
    }
}
