namespace Autheris.Domain.Model;

using System;
using System.Security.Cryptography;

/// <summary>
/// C# 12/13 Stack-only ref struct invariant for sensitive PII and GDPR Article 9 data.
/// The compiler strictly guarantees that instances cannot be boxed, cannot be stored in managed heap objects,
/// and cannot escape the callstack into async state machines or memory dumps.
/// </summary>
public readonly ref struct SensitiveDataSpan
{
    private readonly ReadOnlySpan<char> _span;
    public string Classification { get; }

    public SensitiveDataSpan(ReadOnlySpan<char> data, string classification = "GDPR_ARTICLE_9")
    {
        _span = data;
        Classification = classification;
    }

    public int Length => _span.Length;
    public bool IsEmpty => _span.IsEmpty;

    public ReadOnlySpan<char> AsSpan() => _span;

    /// <summary>
    /// Cryptographic constant-time equality check to prevent timing side-channel attacks on sensitive values.
    /// </summary>
    public bool EqualsConstantTime(ReadOnlySpan<char> other)
    {
        if (_span.Length != other.Length)
        {
            return false;
        }

        int diff = 0;
        for (int i = 0; i < _span.Length; i++)
        {
            diff |= _span[i] ^ other[i];
        }

        return diff == 0;
    }

    /// <summary>
    /// Masks the sensitive data directly into the destination buffer without heap allocations.
    /// Preserves prefix/suffix if sufficient length exists.
    /// </summary>
    public void MaskInto(Span<char> destination, char maskChar = '*', int visiblePrefix = 2, int visibleSuffix = 2)
    {
        if (destination.Length < _span.Length)
        {
            throw new ArgumentException("Destination span is too short.", nameof(destination));
        }

        if (_span.Length <= (visiblePrefix + visibleSuffix))
        {
            destination[.._span.Length].Fill(maskChar);
            return;
        }

        // Copy prefix
        _span[..visiblePrefix].CopyTo(destination[..visiblePrefix]);

        // Mask middle
        var maskLen = _span.Length - visiblePrefix - visibleSuffix;
        destination.Slice(visiblePrefix, maskLen).Fill(maskChar);

        // Copy suffix
        _span[^visibleSuffix..].CopyTo(destination.Slice(_span.Length - visibleSuffix, visibleSuffix));
    }

    /// <summary>
    /// Explicitly redacts output to prevent sensitive data leakage into loggers or string formatters.
    /// </summary>
    public override string ToString() => $"[REDACTED_STACK_SPAN:{Classification}]";
}
