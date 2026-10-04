using System;
using Autheris.Domain.Connectors;

namespace Autheris.Application.Connectors;

public interface IAutherisConnector : IAsyncDisposable
{
    string ConnectorId { get; }
    string ConnectorType { get; }
    ConnectorCapabilities Capabilities { get; }
    IConnectorMetadata Metadata { get; }
    IConnectorSplitManager SplitManager { get; }
    IConnectorRecordSource RecordSource { get; }
}
