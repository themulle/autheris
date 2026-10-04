namespace Autheris.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IGoldenQueryService
{
    void RegisterGoldenQuery(GoldenQuery query);
    ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(string? domain = null, string? tableName = null, CancellationToken ct = default);
    ValueTask<GoldenQuery?> GetByIdAsync(string id, CancellationToken ct = default);
}
