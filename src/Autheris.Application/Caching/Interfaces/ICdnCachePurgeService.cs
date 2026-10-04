namespace Autheris.Application.Caching.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface ICdnCachePurgeService
{
    Task<PurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct = default);
}
