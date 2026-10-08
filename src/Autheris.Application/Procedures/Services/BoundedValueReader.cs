namespace Autheris.Application.Procedures.Services;

using System;
using System.Data.Common;
using System.Text;

/// <summary>
/// R-SQL-8 / SQL2-8: reads one column of a <see cref="System.Data.CommandBehavior.SequentialAccess"/> reader. Text and
/// binary values (LOBs) are read in chunks against the remaining response budget, so a single large value is never
/// materialised beyond the limit. Binary values count with the Base64 factor 4/3 of their JSON representation.
/// </summary>
internal static class BoundedValueReader
{
    private const int ChunkSize = 8192;

    /// <summary>Estimated serialized size of a value already read (UTF-16 for text, Base64 for binary, 16 per scalar).</summary>
    public static long EstimateBytes(object? value) => value switch
    {
        null => 0,
        string s => s.Length * 2L,
        byte[] b => Base64Length(b.Length),
        _ => 16
    };

    public static object? Read(DbDataReader reader, int ordinal, long remainingBudget, out long consumedBytes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        consumedBytes = 0;
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var type = reader.GetFieldType(ordinal);
        if (type == typeof(byte[]))
        {
            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[ChunkSize];
            long offset = 0;
            long read;
            while ((read = reader.GetBytes(ordinal, offset, chunk, 0, chunk.Length)) > 0)
            {
                offset += read;
                consumedBytes = Base64Length(offset);
                if (consumedBytes > remainingBudget)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, (int)read);
            }

            return buffer.ToArray();
        }

        if (type == typeof(string))
        {
            var builder = new StringBuilder();
            var chunk = new char[ChunkSize];
            long offset = 0;
            long read;
            while ((read = reader.GetChars(ordinal, offset, chunk, 0, chunk.Length)) > 0)
            {
                offset += read;
                consumedBytes = offset * 2L;
                if (consumedBytes > remainingBudget)
                {
                    throw TooLarge();
                }

                builder.Append(chunk, 0, (int)read);
            }

            return builder.ToString();
        }

        var value = reader.GetValue(ordinal);
        consumedBytes = EstimateBytes(value);
        return value;
    }

    private static long Base64Length(long bytes) => (bytes + 2) / 3 * 4;

    private static Autheris.Domain.Exceptions.GatewaySecurityException TooLarge() =>
        new("The response size exceeds the configured limit.", "RESPONSE_TOO_LARGE");
}
