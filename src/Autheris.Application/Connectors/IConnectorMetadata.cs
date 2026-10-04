using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors;

public interface IConnectorMetadata
{
    Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default);
    Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default);
}
