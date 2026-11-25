namespace Autheris.Application.Policy.Protection;

/// <summary>
/// The unmask coverage of one access decision, resolved at most once and only when a masked column needs it. Every masking stage
/// of the decision (classification masks, row-scoped per-cell masks, protection masks) reads the same coverage, so an unmask grant
/// lifts a mask consistently or nowhere. A resolution failure yields <see cref="UnmaskCoverage.None"/> (columns stay masked).
/// </summary>
public sealed class UnmaskCoverageSource(Func<CancellationToken, ValueTask<UnmaskCoverage>> resolve)
{
    private readonly Func<CancellationToken, ValueTask<UnmaskCoverage>> _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    private UnmaskCoverage? _coverage;

    public async ValueTask<UnmaskCoverage> GetAsync(CancellationToken ct)
    {
        if (_coverage is { } cached)
        {
            return cached;
        }

        UnmaskCoverage resolved;
        try
        {
            resolved = await _resolve(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            resolved = UnmaskCoverage.None;
        }

        _coverage = resolved;
        return resolved;
    }
}
