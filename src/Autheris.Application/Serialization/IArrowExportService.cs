namespace Autheris.Application.Serialization;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;

/// <summary>
/// F-DATA-04: High-performance columnar export service converting governed tabular rows into Apache Arrow IPC streaming format.
/// </summary>
public interface IArrowExportService
{
    public const string ArrowStreamContentType = "application/vnd.apache.arrow.stream";
    public const string ArrowFileContentType = "application/vnd.apache.arrow.file";

    ValueTask ExportToStreamAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        Stream outputStream,
        CancellationToken ct = default);

    ValueTask<byte[]> ExportToBytesAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default);

    RecordBatch BuildRecordBatch(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows);
}
