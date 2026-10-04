namespace Autheris.Application.Governance.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface ISchemaSunsettingService
{
    ValueTask RegisterRuleAsync(FieldSunsettingRule rule, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<FieldSunsettingRule>> GetRulesAsync(CancellationToken cancellationToken = default);
    ValueTask<FieldSunsettingRule?> GetRuleAsync(string targetTable, string fieldName, CancellationToken cancellationToken = default);
    ValueTask<SunsettingEvaluationResult?> EvaluateFieldAsync(string targetTable, string fieldName, DateTimeOffset? now = null, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveRuleAsync(Guid id, CancellationToken cancellationToken = default);
}
