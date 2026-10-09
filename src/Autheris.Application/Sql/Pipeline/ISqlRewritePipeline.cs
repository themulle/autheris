namespace Autheris.Application.Sql.Pipeline;

using System.Collections.Generic;

/// <summary>
/// Sicherheits-Invariante 1: Strikte und unveränderliche Reihenfolge der 5 SQL-Rewrite-Stufen.
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
}
