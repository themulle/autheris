namespace Autheris.Extensions.OData;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public sealed record ODataQueryResult(
    bool Success,
    int StatusCode,
    object Payload,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? RetryAfterSeconds = null
);

public interface IODataHandler
{
    Task<string> GetMetadataCsdlAsync(ClaimsPrincipal? principal = null, CancellationToken ct = default);
    Task<object> GetServiceDocumentAsync(string serviceRootUrl, ClaimsPrincipal? principal = null, CancellationToken ct = default);
    Task<ODataQueryResult> ExecuteEntitySetQueryAsync(
        ClaimsPrincipal? principal,
        string serviceRootUrl,
        TableIdentifier table,
        int? top,
        int? skip,
        string? select,
        bool includeCount,
        IReadOnlyDictionary<string, string[]>? headers,
        CancellationToken ct = default);
}
