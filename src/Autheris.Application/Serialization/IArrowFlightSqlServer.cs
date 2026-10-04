namespace Autheris.Application.Serialization;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// F-DATA-04-B: Arrow Flight SQL service for continuous governed binary columnar streaming.
/// </summary>
public interface IArrowFlightSqlServer
{
    ValueTask<FlightSqlInfo> GetFlightInfoAsync(
        string query,
        ClaimsPrincipal principal,
        TenantId tenant,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<FlightSqlTableInfo>> GetTablesAsync(
        ClaimsPrincipal principal,
        TenantId tenant,
        string? schemaPattern = null,
        CancellationToken ct = default);

    IAsyncEnumerable<RecordBatch> DoGetStreamAsync(
        FlightSqlTicket ticket,
        ClaimsPrincipal principal,
        CancellationToken ct = default);
}
