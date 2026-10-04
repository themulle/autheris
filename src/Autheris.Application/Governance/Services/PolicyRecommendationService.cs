namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

public sealed class PolicyRecommendationService
{
    private readonly PolicyRecommendationOptions _options;
    private readonly ILogger<PolicyRecommendationService>? _logger;
    private readonly ConcurrentDictionary<string, LeastPrivilegeProposal> _pendingProposals = new();

    public PolicyRecommendationService(
        IOptions<PolicyRecommendationOptions>? options = null,
        ILogger<PolicyRecommendationService>? logger = null)
    {
        _options = options?.Value ?? new PolicyRecommendationOptions();
        _logger = logger;
    }

    public LeastPrivilegeProposal? GenerateProposal(
        AccessDenialEvent denialEvent,
        IReadOnlyList<string>? catalogColumns = null)
    {
        ArgumentNullException.ThrowIfNull(denialEvent);

        // Security Guardrail: Reject wildcard recommendations
        var containsWildcard = denialEvent.RequestedColumns.Any(c => c == "*" || c.Contains('%'));
        if (containsWildcard && !_options.AllowWildcardRecommendations)
        {
            _logger?.LogWarning("Policy recommendation rejected: Wildcard column access cannot be automatically proposed.");
            return null;
        }

        // Minimal Columns: Only the requested columns that are actually valid and unconsented
        var minimalCols = catalogColumns != null && catalogColumns.Count > 0
            ? denialEvent.RequestedColumns.Where(c => catalogColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : denialEvent.RequestedColumns.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (minimalCols.Count == 0)
        {
            return null;
        }

        // Bounded lifetime enforcement (Least Privilege)
        var duration = _options.MaxValidityDuration;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(30))
        {
            duration = TimeSpan.FromDays(14);
        }

        // Row filter: Bind strictly to requester's tenant
        var rowFilters = new List<ConsentRowFilter>
        {
            new()
            {
                ColumnName = "tenant_id",
                Operator = "EQ",
                ValueType = "string",
                ValueJson = JsonSerializer.Serialize(new[] { denialEvent.TenantId.Value }),
                ValueSource = "LITERAL"
            }
        };

        var proposalKey = $"{denialEvent.TenantId.Value}:{denialEvent.RequesterSid.Value}:{denialEvent.TargetTable.ToQualifiedName()}:{string.Join(",", minimalCols.OrderBy(c => c))}";

        if (_pendingProposals.Count >= _options.MaxQueueCapacity && !_pendingProposals.ContainsKey(proposalKey))
        {
            _logger?.LogWarning("Policy recommendation queue capacity ({Capacity}) reached. Dropping proposal for table '{Table}'.", _options.MaxQueueCapacity, denialEvent.TargetTable.ToQualifiedName());
            return null;
        }

        var proposal = new LeastPrivilegeProposal(
            TargetTable: denialEvent.TargetTable,
            TenantId: denialEvent.TenantId,
            RequesterSid: denialEvent.RequesterSid,
            MinimalColumns: minimalCols,
            MinimalRowFilters: rowFilters,
            SuggestedValidityDuration: duration,
            ConfidenceScore: 0.95f,
            BusinessJustification: $"Automated least-privilege proposal following 403 Forbidden access denial for query intent '{denialEvent.IntendedPurpose ?? "General Retrieval"}'. Minimal requested columns: [{string.Join(", ", minimalCols)}]."
        );

        // Deduplication
        return _pendingProposals.GetOrAdd(proposalKey, proposal);
    }

    public int PendingProposalCount => _pendingProposals.Count;
}
