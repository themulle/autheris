using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Lightweight <see cref="IColumnMaskingProvider"/> test double returning either a fixed value or computing via delegate.
/// </summary>
public sealed class StubColumnMaskingProvider(
    Func<string, object?, MaskingRule, object?> mask,
    string? defaultHmacKeyId = null) : IColumnMaskingProvider
{
    public static readonly StubColumnMaskingProvider Null = new((Func<string, object?, MaskingRule, object?>)((_, _, _) => null));
    public static readonly StubColumnMaskingProvider Asterisks = new("***");

    public StubColumnMaskingProvider(object? fixedValue, string? defaultHmacKeyId = null)
        : this((_, _, _) => fixedValue, defaultHmacKeyId)
    {
    }

    public string? DefaultHmacKeyId => defaultHmacKeyId;

    public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => mask(columnName, rawValue, rule);
}
