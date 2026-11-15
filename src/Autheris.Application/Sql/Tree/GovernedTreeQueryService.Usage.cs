namespace Autheris.Application.Sql.Tree;

using System;
using System.Data.Common;
using Autheris.Application.Usage;

public sealed partial class GovernedTreeQueryService
{
    private sealed class UsageStatsBox
    {
        public SourceExecutionStats Value;
    }

    private static void CaptureSourceStats(DbCommand command, UsageStatsBox? statsBox)
    {
        if (statsBox != null &&
            command is ISourceExecutionStatsProvider statsProvider &&
            statsProvider.LastExecutionStats is { } stats)
        {
            statsBox.Value = stats;
        }
    }

    private void RecordTreeUsage(
        ResolvedTableAccess access,
        UsageOutcome outcome,
        long rows,
        bool rowsMeasured,
        SourceExecutionStats source)
    {
        _usageRecorder?.Record(
            new UsageFact(
                access.Metadata.Identifier,
                access.Metadata.Table.SourceName ?? string.Empty,
                outcome,
                rows,
                rowsMeasured,
                AnalysisCost: 0,
                Grants: Array.Empty<GrantRef>(),
                Source: source),
            access.Tenant,
            access.UserSid);
    }
}
