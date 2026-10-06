namespace TrinoSqlEngine.Ast.Buffer;

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

/// <summary>
/// High-performance, zero-allocation string buffer for SQL dialect code generation.
/// Operates on stack memory (Span<char>) and rents from ArrayPool<char>.Shared when growing.
/// </summary>
public ref struct ValueStringBuilder
{
    private char[]? _arrayToReturnToPool;
    private Span<char> _chars;
    private int _pos;

    public ValueStringBuilder(Span<char> initialBuffer)
    {
        _arrayToReturnToPool = null;
        _chars = initialBuffer;
        _pos = 0;
    }

    public int Length => _pos;
    public int Capacity => _chars.Length;
    public ReadOnlySpan<char> AsSpan() => _chars.Slice(0, _pos);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char c)
    {
        int pos = _pos;
        if ((uint)pos < (uint)_chars.Length)
        {
            _chars[pos] = c;
            _pos = pos + 1;
        }
        else
        {
            GrowAndAppend(c);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        Append(s.AsSpan());
    }

    public void Append(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return;
        int pos = _pos;
        if (span.Length <= _chars.Length - pos)
        {
            span.CopyTo(_chars.Slice(pos));
            _pos = pos + span.Length;
        }
        else
        {
            GrowAndAppend(span);
        }
    }

    public void Append(long value)
    {
        Span<char> chars = _chars.Slice(_pos);
        if (value.TryFormat(chars, out int written, provider: System.Globalization.CultureInfo.InvariantCulture))
        {
            _pos += written;
        }
        else
        {
            Grow(32);
            chars = _chars.Slice(_pos);
            if (value.TryFormat(chars, out written, provider: System.Globalization.CultureInfo.InvariantCulture))
            {
                _pos += written;
            }
            else
            {
                Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    public void Append(int value) => Append((long)value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowAndAppend(char c)
    {
        Grow(1);
        Append(c);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowAndAppend(ReadOnlySpan<char> span)
    {
        Grow(span.Length);
        Append(span);
    }

    private void Grow(int requiredAdditionalCapacity)
    {
        int minimumCapacity = _pos + requiredAdditionalCapacity;
        int newCapacity = Math.Max(Math.Max(_chars.Length * 2, 256), minimumCapacity);
        char[] poolArray = ArrayPool<char>.Shared.Rent(newCapacity);
        _chars.Slice(0, _pos).CopyTo(poolArray);

        char[]? toReturn = _arrayToReturnToPool;
        _chars = _arrayToReturnToPool = poolArray;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }

    public override string ToString()
    {
        string result = new string(_chars.Slice(0, _pos));
        Dispose();
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        char[]? toReturn = _arrayToReturnToPool;
        this = default;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }
}
