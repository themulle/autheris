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

    public static ActionAssertions Should(this Action action) => new(action);

    public static ActionAssertions Should<TResult>(this Func<TResult> action) => new(() => _ = action());

    public static AsyncFunctionAssertions Should(this Func<Task> action) => new(action);

    public static AsyncFunctionAssertions<TResult> Should<TResult>(this Func<Task<TResult>> action) => new(action);
}

internal sealed class StringAssertions(string? actual)
{
    public AndConstraint<StringAssertions> Be(string? expected, string? because = null)
    {
        Assert.Equal(expected, actual);
        return new AndConstraint<StringAssertions>(this);
    }

    public AndConstraint<StringAssertions> NotBe(string? unexpected, string? because = null)
    {
        Assert.NotEqual(unexpected, actual);
        return new AndConstraint<StringAssertions>(this);
    }

    public AndConstraint<StringAssertions> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<StringAssertions>(this);
    }

    public AndConstraint<StringAssertions> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<StringAssertions>(this);
    }

    public AndConstraint<StringAssertions> NotBeNullOrWhiteSpace(string? because = null)
    {
        Assert.False(string.IsNullOrWhiteSpace(actual), because ?? "Expected string not to be null or whitespace.");
        return new AndConstraint<StringAssertions>(this);
    }

    public AndConstraint<StringAssertions> Contain(string expectedSubstring, string? because = null)
    {
        Assert.Contains(expectedSubstring, actual ?? string.Empty, StringComparison.Ordinal);
        return new AndConstraint<StringAssertions>(this);
    }

    public AndConstraint<StringAssertions> NotContain(string unexpectedSubstring, string? because = null)
    {
        Assert.DoesNotContain(unexpectedSubstring, actual ?? string.Empty, StringComparison.Ordinal);
        return new AndConstraint<StringAssertions>(this);
    }
}

internal sealed class CollectionAssertions<TItem>(IEnumerable<TItem>? actual)
{
    public AndConstraint<CollectionAssertions<TItem>> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> BeEmpty(string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Empty(actual!);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> NotBeEmpty(string? because = null)
    {
        Assert.NotNull(actual);
        Assert.NotEmpty(actual!);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> HaveCount(int expectedCount, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Equal(expectedCount, actual!.Count());
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> HaveCountGreaterOrEqualTo(int expectedCount, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.True(actual!.Count() >= expectedCount, because ?? $"Expected count >= {expectedCount}.");
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> Equal(params TItem[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.ToArray());
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> BeEquivalentTo<TExpected>(IEnumerable<TExpected> expected, string? because = null)
    {
        Assert.NotNull(actual);
        var actualList = actual!.Select(x => x?.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expectedList = expected.Select(x => x?.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedList, actualList);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> BeInAscendingOrder(string? because = null)
    {
        Assert.NotNull(actual);
        var list = actual!.ToList();
        var sorted = list.OrderBy(x => x).ToList();
        Assert.Equal(sorted, list);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> BeInDescendingOrder(string? because = null)
    {
        Assert.NotNull(actual);
        var list = actual!.ToList();
        var sorted = list.OrderByDescending(x => x).ToList();
        Assert.Equal(sorted, list);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
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

    public AndConstraint<CollectionAssertions<TItem>> Contain(Func<TItem, bool> predicate, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Contains(actual!, x => predicate(x));
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> Contain(TItem? expectedItem, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Contains(expectedItem!, actual!);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> NotContain(Func<TItem, bool> predicate, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.DoesNotContain(actual!, x => predicate(x));
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> NotContain(TItem? unexpectedItem, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.DoesNotContain(unexpectedItem!, actual!);
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> NotContain(IEnumerable<TItem> unexpectedItems, string? because = null)
    {
        Assert.NotNull(actual);
        foreach (var item in unexpectedItems)
        {
            Assert.DoesNotContain(item, actual!);
        }
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }

    public AndConstraint<CollectionAssertions<TItem>> OnlyContain(Func<TItem, bool> predicate, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.All(actual!, item => Assert.True(predicate(item), because));
        return new AndConstraint<CollectionAssertions<TItem>>(this);
    }
}

internal sealed class DictionaryAssertions<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>>? actual)
{
    public AndConstraint<DictionaryAssertions<TKey, TValue>> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<DictionaryAssertions<TKey, TValue>>(this);
    }

    public AndConstraint<DictionaryAssertions<TKey, TValue>> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<DictionaryAssertions<TKey, TValue>>(this);
    }

    public AndConstraint<DictionaryAssertions<TKey, TValue>> BeEmpty(string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Empty(actual!);
        return new AndConstraint<DictionaryAssertions<TKey, TValue>>(this);
    }

    public AndConstraint<DictionaryAssertions<TKey, TValue>> HaveCount(int expectedCount, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.Equal(expectedCount, actual!.Count());
        return new AndConstraint<DictionaryAssertions<TKey, TValue>>(this);
    }

    public AndWhichConstraint<TValue> ContainKey(TKey expectedKey, string? because = null)
    {
        Assert.NotNull(actual);
        var match = actual!.FirstOrDefault(kvp => EqualityComparer<TKey>.Default.Equals(kvp.Key, expectedKey));
        Assert.True(actual!.Any(kvp => EqualityComparer<TKey>.Default.Equals(kvp.Key, expectedKey)), because ?? $"Expected dictionary to contain key '{expectedKey}'.");
        return new AndWhichConstraint<TValue>(match.Value);
    }

    public AndConstraint<DictionaryAssertions<TKey, TValue>> NotContainKey(TKey unexpectedKey, string? because = null)
    {
        Assert.NotNull(actual);
        Assert.False(actual!.Any(kvp => EqualityComparer<TKey>.Default.Equals(kvp.Key, unexpectedKey)), because ?? $"Expected dictionary not to contain key '{unexpectedKey}'.");
        return new AndConstraint<DictionaryAssertions<TKey, TValue>>(this);
    }
}

internal sealed class ObjectAssertions<T>(T actual)
{
    public T Subject => actual;

    public AndConstraint<ObjectAssertions<T>> Be(T expected, string? because = null)
    {
        if (actual is IComparable comp && expected is not null && comp.CompareTo(expected) == 0)
        {
            return new AndConstraint<ObjectAssertions<T>>(this);
        }

        Assert.Equal(expected, actual);
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> NotBe(T unexpected, string? because = null)
    {
        Assert.NotEqual(unexpected, actual);
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> BeNull(string? because = null)
    {
        Assert.Null(actual);
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> NotBeNull(string? because = null)
    {
        Assert.NotNull(actual);
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> BeTrue(string? because = null)
    {
        Assert.True(Convert.ToBoolean(actual), because);
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> BeFalse(string? because = null)
    {
        Assert.False(Convert.ToBoolean(actual), because);
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> BeGreaterThan<TComparable>(TComparable expected, string? because = null)
        where TComparable : IComparable
    {
        var value = Convert.ToDouble(actual);
        var exp = Convert.ToDouble(expected);
        Assert.True(value > exp, because ?? $"Expected {value} to be greater than {exp}.");
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> BeGreaterOrEqualTo<TComparable>(TComparable expected, string? because = null)
        where TComparable : IComparable
    {
        var value = Convert.ToDouble(actual);
        var exp = Convert.ToDouble(expected);
        Assert.True(value >= exp, because ?? $"Expected {value} to be >= {exp}.");
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> BeLessThanOrEqualTo<TComparable>(TComparable expected, string? because = null)
        where TComparable : IComparable
    {
        var value = Convert.ToDouble(actual);
        var exp = Convert.ToDouble(expected);
        Assert.True(value <= exp, because ?? $"Expected {value} to be <= {exp}.");
        return new AndConstraint<ObjectAssertions<T>>(this);
    }

    public AndConstraint<ObjectAssertions<T>> BeLessOrEqualTo<TComparable>(TComparable expected, string? because = null)
        where TComparable : IComparable => BeLessThanOrEqualTo(expected, because);

    public AndConstraint<ObjectAssertions<T>> BeCloseTo(DateTimeOffset expected, TimeSpan precision, string? because = null)
    {
        if (actual is DateTimeOffset dto)
        {
            var diff = (dto - expected).Duration();
            Assert.True(diff <= precision, because ?? $"Expected {dto:O} to be within {precision} of {expected:O}.");
            return new AndConstraint<ObjectAssertions<T>>(this);
        }

        throw new InvalidOperationException("BeCloseTo requires a DateTimeOffset subject.");
    }
}

internal sealed class AndConstraint<T>(T value)
{
    public T And => value;
}

internal sealed class AndWhichConstraint<T>(T value)
{
    public T Which => value;

    public ObjectAssertions<T> Should() => new(value);
}

internal sealed class ActionAssertions(Action action)
{
    public AndWhichConstraint<TException> Throw<TException>(string? because = null)
        where TException : Exception
    {
        var ex = Assert.ThrowsAny<TException>(action);
        return new AndWhichConstraint<TException>(ex);
    }
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
