namespace FluentAssertions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

internal static class FluentAssertionsCompatibilityExtensions
{
    public static StringAssertions Should(this string? actual) => new(actual);

    public static CollectionAssertions<TItem> Should<TItem>(this IEnumerable<TItem>? actual) => new(actual);

    public static CollectionAssertions<TItem> Should<TItem>(this IReadOnlyList<TItem>? actual) => new(actual);

    public static CollectionAssertions<TItem> Should<TItem>(this IReadOnlyCollection<TItem>? actual) => new(actual);

    public static CollectionAssertions<TItem> Should<TItem>(this IList<TItem>? actual) => new(actual);

    public static CollectionAssertions<TItem> Should<TItem>(this List<TItem>? actual) => new(actual);

    public static CollectionAssertions<TItem> Should<TItem>(this TItem[]? actual) => new(actual);

    public static DictionaryAssertions<TKey, TValue> Should<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue>? actual)
        where TKey : notnull => new(actual);

    public static DictionaryAssertions<TKey, TValue> Should<TKey, TValue>(this IDictionary<TKey, TValue>? actual)
        where TKey : notnull => new(actual);

    public static DictionaryAssertions<TKey, TValue> Should<TKey, TValue>(this Dictionary<TKey, TValue>? actual)
        where TKey : notnull => new(actual);

    public static ObjectAssertions<T> Should<T>(this T actual) => new(actual);

    public static AsyncFunctionAssertions Should(this Func<Task> action) => new(action);

    public static AsyncFunctionAssertions<TResult> Should<TResult>(this Func<Task<TResult>> action) => new(action);
}

internal sealed class StringAssertions(string? actual)
{
    public AndConstraint<string?> Be(string? expected, string? because = null)
    {
        Assert.Equal(expected, actual);
        return new AndConstraint<string?>(actual);
    }

    public AndConstraint<string?> NotBe(string? unexpected, string? because = null)
    {
        Assert.NotEqual(unexpected, actual);
        return new AndConstraint<string?>(actual);
    }

    public AndConstraint<string?> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<string?>(actual);
    }

    public AndConstraint<string?> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<string?>(actual);
    }

    public AndConstraint<string?> NotBeNullOrWhiteSpace(string? because = null)
    {
        Assert.False(string.IsNullOrWhiteSpace(actual), because ?? "Expected string not to be null or whitespace.");
        return new AndConstraint<string?>(actual);
    }

    public AndConstraint<string?> Contain(string expectedSubstring, string? because = null)
    {
        Assert.Contains(expectedSubstring, actual ?? string.Empty, StringComparison.Ordinal);
        return new AndConstraint<string?>(actual);
    }

    public AndConstraint<string?> NotContain(string unexpectedSubstring, string? because = null)
    {
        Assert.DoesNotContain(unexpectedSubstring, actual ?? string.Empty, StringComparison.Ordinal);
        return new AndConstraint<string?>(actual);
    }
}

internal sealed class CollectionAssertions<TItem>(IEnumerable<TItem>? actual)
{
    public AndConstraint<IEnumerable<TItem>?> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> BeEmpty(string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Empty(actual!);
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> HaveCount(int expectedCount, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Equal(expectedCount, actual!.Count());
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> BeEquivalentTo<TExpected>(IEnumerable<TExpected> expected, string? because = null)
    {
        Assert.NotNull(actual);
        var actualList = actual!.Select(x => x?.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expectedList = expected.Select(x => x?.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedList, actualList);
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndWhichConstraint<TItem> ContainSingle(Func<TItem, bool> predicate, string? because = null)
    {
        Assert.NotNull(actual);
        var matches = actual!.Where(predicate).ToList();
        var single = Assert.Single(matches);
        return new AndWhichConstraint<TItem>(single);
    }

    public AndWhichConstraint<TItem> ContainSingle(string? because = null)
    {
        Assert.NotNull(actual);
        var single = Assert.Single(actual!);
        return new AndWhichConstraint<TItem>(single);
    }

    public AndConstraint<IEnumerable<TItem>?> Contain(Func<TItem, bool> predicate, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Contains(actual!, x => predicate(x));
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> Contain(TItem? expectedItem, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Contains(expectedItem!, actual!);
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> NotContain(Func<TItem, bool> predicate, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.DoesNotContain(actual!, x => predicate(x));
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> NotContain(TItem? unexpectedItem, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.DoesNotContain(unexpectedItem!, actual!);
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> OnlyContain(Func<TItem, bool> predicate, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.All(actual!, item => Assert.True(predicate(item), because));
        return new AndConstraint<IEnumerable<TItem>?>(actual);
    }

    public AndConstraint<IEnumerable<TItem>?> ContainKey(string key, string? because = null)
    {
        Assert.NotNull(actual);
        if (actual is IReadOnlyDictionary<string, object?> roDict)
        {
            Assert.True(roDict.ContainsKey(key), because ?? $"Expected dictionary to contain key '{key}'.");
            return new AndConstraint<IEnumerable<TItem>?>(actual);
        }

        if (actual is IDictionary<string, object?> dict)
        {
            Assert.True(dict.ContainsKey(key), because ?? $"Expected dictionary to contain key '{key}'.");
            return new AndConstraint<IEnumerable<TItem>?>(actual);
        }

        throw new InvalidOperationException("Subject is not a string-keyed dictionary.");
    }
}

internal sealed class DictionaryAssertions<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>>? actual)
{
    public AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?>(actual);
    }

    public AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?>(actual);
    }

    public AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?> BeEmpty(string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Empty(actual!);
        return new AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?>(actual);
    }

    public AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?> HaveCount(int expectedCount, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Equal(expectedCount, actual!.Count());
        return new AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?>(actual);
    }

    public AndWhichConstraint<TValue> ContainKey(TKey expectedKey, string? because = null)
    {
        Assert.NotNull(actual);
        if (actual is IReadOnlyDictionary<TKey, TValue> roDict)
        {
            Assert.True(roDict.TryGetValue(expectedKey, out var val), because ?? $"Expected dictionary to contain key '{expectedKey}'.");
            return new AndWhichConstraint<TValue>(val!);
        }

        if (actual is IDictionary<TKey, TValue> dict)
        {
            Assert.True(dict.TryGetValue(expectedKey, out var val), because ?? $"Expected dictionary to contain key '{expectedKey}'.");
            return new AndWhichConstraint<TValue>(val!);
        }

        var match = actual!.FirstOrDefault(kvp => EqualityComparer<TKey>.Default.Equals(kvp.Key, expectedKey));
        Assert.True(actual!.Any(kvp => EqualityComparer<TKey>.Default.Equals(kvp.Key, expectedKey)), because ?? $"Expected dictionary to contain key '{expectedKey}'.");
        return new AndWhichConstraint<TValue>(match.Value);
    }

    public AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?> NotContainKey(TKey unexpectedKey, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.False(actual!.Any(kvp => EqualityComparer<TKey>.Default.Equals(kvp.Key, unexpectedKey)), because ?? $"Expected dictionary not to contain key '{unexpectedKey}'.");
        return new AndConstraint<IEnumerable<KeyValuePair<TKey, TValue>>?>(actual);
    }
}

internal sealed class ObjectAssertions<T>(T actual)
{
    public T Subject => actual;

    public AndConstraint<T> Be(T expected, string? because = null)
    {
        if (actual is IComparable comp && expected is not null && comp.CompareTo(expected) == 0)
        {
            return new AndConstraint<T>(actual);
        }

        Assert.Equal(expected, actual);
        return new AndConstraint<T>(actual);
    }

    public AndConstraint<T> NotBe(T unexpected, string? because = null)
    {
        Assert.NotEqual(unexpected, actual);
        return new AndConstraint<T>(actual);
    }

    public AndConstraint<T> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<T>(actual);
    }

    public AndConstraint<T> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<T>(actual);
    }

    public AndConstraint<T> BeTrue(string? because = null)
    {
        Assert.True(Convert.ToBoolean(actual), because);
        return new AndConstraint<T>(actual);
    }

    public AndConstraint<T> BeFalse(string? because = null)
    {
        Assert.False(Convert.ToBoolean(actual), because);
        return new AndConstraint<T>(actual);
    }

    public AndConstraint<T> BeGreaterThan<TComparable>(TComparable expected, string? because = null)
        where TComparable : IComparable
    {
        var value = Convert.ToDouble(actual);
        var exp = Convert.ToDouble(expected);
        Assert.True(value > exp, because ?? $"Expected {value} to be greater than {exp}.");
        return new AndConstraint<T>(actual);
    }

    public AndConstraint<T> BeLessThanOrEqualTo<TComparable>(TComparable expected, string? because = null)
        where TComparable : IComparable
    {
        var value = Convert.ToDouble(actual);
        var exp = Convert.ToDouble(expected);
        Assert.True(value <= exp, because ?? $"Expected {value} to be <= {exp}.");
        return new AndConstraint<T>(actual);
    }
}

internal sealed class AndConstraint<T>(T value)
{
    public T And => value;
}

internal sealed class AndWhichConstraint<T>(T value)
{
    public T Which => value;
}

internal sealed class AsyncFunctionAssertions(Func<Task> action)
{
    public async Task<AndWhichConstraint<TException>> ThrowAsync<TException>(string? because = null)
        where TException : Exception
    {
        var ex = await Assert.ThrowsAnyAsync<TException>(action).ConfigureAwait(false);
        return new AndWhichConstraint<TException>(ex);
    }
}

internal sealed class AsyncFunctionAssertions<TResult>(Func<Task<TResult>> action)
{
    public async Task<AndWhichConstraint<TException>> ThrowAsync<TException>(string? because = null)
        where TException : Exception
    {
        var ex = await Assert.ThrowsAnyAsync<TException>(action).ConfigureAwait(false);
        return new AndWhichConstraint<TException>(ex);
    }
}
