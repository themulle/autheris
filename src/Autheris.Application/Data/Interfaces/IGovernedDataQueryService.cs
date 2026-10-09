namespace Autheris.Application.Data.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IGovernedDataQueryService
{
    Task<DatasetQueryEnvelope> ExecuteQueryAsync(
        DatasetQueryRequest request,
        RequestContext context,
        CancellationToken ct = default);
}
