namespace Autheris.Extensions.Lakehouse.Interfaces;

using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Storage abstraction for reading Apache Iceberg metadata and data files (Local, S3, Azure ADLS Gen2).
/// </summary>
public interface ILakehouseStorageProvider
{
    /// <summary>
    /// Reads UTF-8 text content from the specified storage location.
    /// </summary>
    ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream for the specified storage location.
    /// </summary>
    ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a file exists at the specified storage location.
    /// </summary>
    ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default);
}
