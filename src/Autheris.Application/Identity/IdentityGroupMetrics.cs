namespace Autheris.Application.Identity;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>Group-resolution counters on the shared <c>Autheris.Core</c> meter. Labels use a fixed vocabulary; values never appear.</summary>
public static class IdentityGroupMetrics
{
    public const string SourceEntra = "entra";
    public const string SourceAdfs = "adfs";
    public const string SourceNegotiate = "negotiate";
    public const string SourceBasic = "basic";
    public const string SourceResolver = "resolver";
    public const string SourceForwardAuth = "forwardauth";

    private static readonly Meter Meter = new("Autheris.Core");

    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
        "autheris_identity_group_rejected_total",
        description: "Group values dropped because they were not canonical SIDs or were duplicates, by fixed source.");

    private static readonly Counter<long> PublishFailed = Meter.CreateCounter<long>(
        "autheris_identity_group_invalidation_publish_failed_total",
        description: "Group cache invalidation events that could not be published; the entry expires with its TTL.");

    public static void RecordPublishFailed() => PublishFailed.Add(1);

    public static void RecordRejected(string source, int count)
    {
        if (count > 0)
        {
            Rejected.Add(count, new KeyValuePair<string, object?>("source", source));
        }
    }
}
