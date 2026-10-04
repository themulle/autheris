namespace Autheris.Application.Connectors;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;

public interface IVectorRecordSource
{
    Task<IReadOnlyList<VectorDocumentChunk>> SearchVectorsAsync(
        VectorSearchRequest request,
        ConnectorSessionContext session,
        CancellationToken ct = default);
}

public interface IVectorAutherisConnector : IAutherisConnector
{
    IVectorRecordSource VectorRecordSource { get; }
}
