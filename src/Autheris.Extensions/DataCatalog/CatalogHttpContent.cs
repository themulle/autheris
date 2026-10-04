namespace Autheris.Extensions.DataCatalog;

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Bounded response reading for all data catalog clients: rejects responses above <see cref="MaxResponseBytes"/>,
/// both via the declared Content-Length and while streaming (chunked responses without Content-Length).
/// </summary>
internal static class CatalogHttpContent
{
    internal const long MaxResponseBytes = 10 * 1024 * 1024; // 10 MB maximum payload cap

    internal static Task<string> ReadBoundedStringAsync(HttpResponseMessage response, string providerName, CancellationToken ct) =>
        BoundedHttpContent.ReadBoundedStringAsync(response, providerName, MaxResponseBytes, ct);
}
