using System.Collections.Generic;
using Autheris.Domain.Common;

namespace Autheris.Application.DataSources.Connectors;

public interface IAutherisConnectorRegistry
{
    void RegisterConnector(string catalogName, IAutherisConnector connector);
    void RegisterFactory(IAutherisConnectorFactory factory);
    IAutherisConnector? GetConnector(string catalogName);
    IReadOnlyCollection<IAutherisConnector> GetAllConnectors();
    bool TryGetConnectorForTable(TableIdentifier table, out IAutherisConnector? connector);
}
