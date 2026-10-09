namespace Autheris.Application.Catalog.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IPrincipalResolverService
{
    Task<IReadOnlyList<PrincipalResolutionItem>> ResolvePrincipalAsync(string query, RequestContext context, CancellationToken ct = default);
}
