namespace Autheris.Application.Identity;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>Group-resolution counters on the shared <c>Autheris.Core</c> meter. Labels use a fixed vocabulary; values never appear.</summary>
internal static class IdentityGroupMetrics
{
    internal const string SourceEntra = "entra";
    internal const string SourceAdfs = "adfs";
    internal const string SourceNegotiate = "negotiate";
    internal const string SourceBasic = "basic";
    internal const string SourceResolver = "resolver";
    internal const string SourceForwardAuth = "forwardauth";

    private static readonly Meter Meter = new("Autheris.Core");

    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
        "autheris_identity_group_rejected_total",
        description: "Group values dropped because they were not canonical SIDs or were duplicates, by fixed source.");

    internal static void RecordRejected(string source, int count)
    {
        if (count > 0)
        {
            Rejected.Add(count, new KeyValuePair<string, object?>("source", source));
        }
    }
}
