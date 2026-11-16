namespace Autheris.Application.Usage;

/// <summary>
/// Exposes inline source execution statistics captured by a data-source command or executor.
/// </summary>
public interface ISourceExecutionStatsProvider
{
    public const string ItemKey = "__SourceExecutionStats";

    SourceExecutionStats? LastExecutionStats { get; }
}
