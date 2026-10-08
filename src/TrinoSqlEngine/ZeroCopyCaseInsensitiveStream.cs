namespace TrinoSqlEngine;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;

public sealed class ZeroCopyCaseInsensitiveStream : ICharStream
{
    private readonly ReadOnlyMemory<char> _memory;
    private readonly string? _strSource;
    private readonly int _strOffset;
    private readonly int _length;
    private int _index;

    public ZeroCopyCaseInsensitiveStream(ReadOnlyMemory<char> source)
    {
        _memory = source;
        _length = source.Length;
        _index = 0;

        if (MemoryMarshal.TryGetString(source, out string? text, out int start, out _))
        {
            _strSource = text;
            _strOffset = start;
        }
    }

    public int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _index;
    }

    public int Size
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length;
    }

    public string SourceName => IntStreamConstants.UnknownSourceName;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Consume()
    {
        if ((uint)_index < (uint)_length)
        {
            _index++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int LA(int i)
    {
        // Hot-path: LA(1) is invoked >95% of the time in ANTLR lexer loops
        if (i == 1)
        {
            if ((uint)_index >= (uint)_length) return IntStreamConstants.EOF;
            char ch = _strSource != null ? _strSource[_strOffset + _index] : _memory.Span[_index];
            return (uint)(ch - 'a') <= ('z' - 'a') ? (char)(ch - 32) : ch;
        }

        if (i == 0) return 0;

        // General path for lookahead / lookbehind
        long targetLong = i > 0 ? (long)_index + i - 1 : (long)_index + i;
        if (targetLong < 0 || targetLong >= _length) return IntStreamConstants.EOF;

        int target = (int)targetLong;
        char c = _strSource != null ? _strSource[_strOffset + target] : _memory.Span[target];
        return (uint)(c - 'a') <= ('z' - 'a') ? (char)(c - 32) : c;
    }

    public int Mark() => -1;
    public void Release(int marker) { }
    public void Seek(int index) => _index = Math.Clamp(index, 0, _length);

    public string GetText(Interval interval)
    {
        int start = interval.a;
        int stop = interval.b;
        if (start < 0 || stop < start || stop >= _length) return string.Empty;
        int count = stop - start + 1;

        if (_strSource != null)
        {
            return _strSource.Substring(_strOffset + start, count);
        }

        return _memory.Slice(start, count).ToString();
    }
}
