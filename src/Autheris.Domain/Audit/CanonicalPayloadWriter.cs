using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Autheris.Domain.Audit;

/// <summary>
/// Pooled, allocation-free writer for the canonical audit payload and its digest. The produced characters are identical to
/// <see cref="AuditCanonicalizer.BuildCanonicalPayload"/>; the digest is computed over their UTF-8 bytes without materializing a payload
/// string or a byte array per entry.
/// </summary>
internal ref struct CanonicalPayloadWriter
{
    private const int StackBytes = 1024;
    private static readonly SearchValues<char> EscapedChars = SearchValues.Create("\\\n\r\t|");

    private char[] _buffer;
    private int _length;

    public CanonicalPayloadWriter(int capacity)
    {
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(capacity, 256));
        _length = 0;
    }

    public readonly ReadOnlySpan<char> Written => _buffer.AsSpan(0, _length);

    public void Append(char c)
    {
        Ensure(1);
        _buffer[_length++] = c;
    }

    public void Append(ReadOnlySpan<char> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    public void AppendLong(long value)
    {
        Ensure(24);
        value.TryFormat(_buffer.AsSpan(_length), out int written, default, CultureInfo.InvariantCulture);
        _length += written;
    }

    public void AppendRoundTrip(DateTimeOffset value)
    {
        Ensure(40);
        value.TryFormat(_buffer.AsSpan(_length), out int written, "O", CultureInfo.InvariantCulture);
        _length += written;
    }

    /// <summary>Appends <paramref name="value"/> escaped exactly like <see cref="AuditCanonicalizer.CanonicalizeField"/>.</summary>
    public void AppendCanonical(string? value)
    {
        ReadOnlySpan<char> rest = value;
        while (!rest.IsEmpty)
        {
            int index = rest.IndexOfAny(EscapedChars);
            if (index < 0)
            {
                Append(rest);
                return;
            }

            Append(rest[..index]);
            Ensure(2);
            _buffer[_length++] = '\\';
            _buffer[_length++] = rest[index] switch
            {
                '\\' => '\\',
                '\n' => 'n',
                '\r' => 'r',
                '\t' => 't',
                _ => 'p'
            };
            rest = rest[(index + 1)..];
        }
    }

    /// <summary>SHA-256 (or HMAC-SHA-256 when a key is given, even an empty one) of the UTF-8 bytes of the written characters, as upper-case hex.</summary>
    public readonly string HashToHex(byte[]? hmacKey)
    {
        ReadOnlySpan<char> chars = Written;
        int max = Encoding.UTF8.GetMaxByteCount(chars.Length);
        Span<byte> digest = stackalloc byte[32];
        if (max <= StackBytes)
        {
            Span<byte> bytes = stackalloc byte[max];
            int n = Encoding.UTF8.GetBytes(chars, bytes);
            Hash(hmacKey, bytes[..n], digest);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int n = Encoding.UTF8.GetBytes(chars, rented);
                Hash(hmacKey, rented.AsSpan(0, n), digest);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        return Convert.ToHexString(digest);
    }

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null!;
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void Hash(byte[]? hmacKey, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (hmacKey is not null)
        {
            HMACSHA256.HashData(hmacKey, source, destination);
        }
        else
        {
            SHA256.HashData(source, destination);
        }
    }

    private void Ensure(int additional)
    {
        if (_length + additional <= _buffer.Length)
        {
            return;
        }

        char[] grown = ArrayPool<char>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + additional));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<char>.Shared.Return(_buffer, clearArray: true);
        _buffer = grown;
    }
}
