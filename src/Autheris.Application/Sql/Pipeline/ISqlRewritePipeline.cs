namespace Autheris.Application.Sql.Pipeline;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Security invariant 1: the strict and immutable order of the 5 SQL rewrite stages.
/// </summary>
public enum SqlRewriteStageOrder
{
    AnalyseAndStatementPolicy = 1,
    IdentityResolution = 2,
    GovernanceResolution = 3,
    RewriteOptions = 4,
    SecureRewrite = 5
}

public interface ISqlRewritePipeline
{
    IReadOnlyList<SqlRewriteStageOrder> StageOrder { get; }

    /// <summary>
    /// G-6: runs the pipeline through the compile and discards the result. It answers "would this statement be accepted" and never
    /// returns SQL text (it replaces the string-returning <c>RewriteSqlAsync</c>).
    /// </summary>
    Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default);
}
