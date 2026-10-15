namespace Autheris.Application.Analytics;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Executes governed analytical aggregations (<c>COUNT</c>, <c>SUM</c>, <c>AVG</c>, <c>MIN</c>, <c>MAX</c>, <c>COUNT(DISTINCT)</c>)
/// with full RLS row filtering, masked-column rejection, and k-anonymity minimum group size enforcement (#28, #37).
/// </summary>
public interface IGovernedAggregationService
{
    Task<AggregationResult> ExecuteAggregationAsync(
        ClaimsPrincipal principal,
        AggregationQuery query,
        CancellationToken ct = default);
}
