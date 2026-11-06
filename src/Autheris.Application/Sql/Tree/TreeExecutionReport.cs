namespace Autheris.Application.Sql.Tree;

/// <summary>
/// Execution metrics and compilation metadata of a single tree statement and its relations.
/// Internal only; not serialized to clients or external logs.
/// </summary>
public sealed record TreeExecutionReport(
    TimeSpan StatementElapsed,
    int ParentPageSize,
    IReadOnlyList<TreeListPlan> Lists,
    IReadOnlyList<CrossSourceListExecution> CrossSourceExecutions);

/// <summary>
/// Measured execution time and row limit for a cross-source relation resolved per parent batch.
/// </summary>
public sealed record CrossSourceListExecution(
    IReadOnlyList<string> Path,
    TimeSpan Elapsed,
    int Limit);

/// <summary>
/// Observer receiving tree execution metrics from <see cref="IGovernedTreeQueryService"/>.
/// </summary>
public interface ITreeExecutionObserver
{
    void OnReport(TreeExecutionReport report);
}
