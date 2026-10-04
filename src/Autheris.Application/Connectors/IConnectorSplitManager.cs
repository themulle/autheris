using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors;

public interface IConnectorSplitManager
{
    Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(
        TableMetadata table,
        ConnectorSessionContext session,
        CancellationToken ct = default);
}
