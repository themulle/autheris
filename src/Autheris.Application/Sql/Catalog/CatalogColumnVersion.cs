using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Autheris.Application.Sql.Catalog;

/// <summary>
/// The version of a catalog entry: a hash of name, native type (including length and precision), length semantics and
/// collation of every column in order. A change of any of them changes the version, so no cached plan survives it (SEC-X1-09).
/// </summary>
public static class CatalogColumnVersion
{
    public static long Compute(IReadOnlyList<DbColumnMetadata> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, columns.Count);
        sha.AppendData(count);
        foreach (var column in columns)
        {
            Append(sha, column.Name);
            Append(sha, column.NativeType);
            Append(sha, column.LengthSemantics);
            Append(sha, column.Collation);
        }

        Span<byte> digest = stackalloc byte[32];
        sha.GetHashAndReset(digest);
        return BinaryPrimitives.ReadInt64LittleEndian(digest) & long.MaxValue;
    }

    // Length-prefixed, so ("ab", "c") and ("a", "bc") cannot collide; null differs from the empty string.
    private static void Append(IncrementalHash sha, string? value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, value is null ? -1 : Encoding.UTF8.GetByteCount(value));
        sha.AppendData(length);
        if (value is not null)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(value));
        }
    }
}
