namespace Autheris.Tests.Unit.Sql;

using System.Collections.Immutable;
using System.Text;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>The hit path keeps the cache key in the thread-local builder; hash and equality must match the string forms exactly.</summary>
public sealed class CompileCacheKeyBuilderTests
{
    private static StringBuilder MultiChunk(string text)
    {
        var sb = new StringBuilder(16);
        for (int i = 0; i < text.Length; i += 7)
        {
            sb.Append(text, i, Math.Min(7, text.Length - i));   // forces a chain of chunks
        }

        return sb;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1000)]
    [InlineData(40000)]
    public void Builder_hash_equals_string_hash(int length)
    {
        var text = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            text.Append(i % 5 == 0 ? 'ä' : (char)('a' + i % 26));
        }

        string key = text.ToString();

        CompiledSqlTemplateCache.HashOf(MultiChunk(key)).ShouldBe(CompiledSqlTemplateCache.HashOf(key));
    }

    [Fact]
    public void Builder_hash_equals_string_hash_for_a_surrogate_pair_split_across_chunks()
    {
        string key = new string('x', 6) + "\U0001F600" + new string('y', 20);   // the pair straddles the 7 char chunk boundary

        CompiledSqlTemplateCache.HashOf(MultiChunk(key)).ShouldBe(CompiledSqlTemplateCache.HashOf(key));
    }

    [Fact]
    public void Find_by_builder_hits_only_on_exact_key_text()
    {
        var cache = new CompiledSqlTemplateCache();
        string key = "v=1;sql=8:SELECT 1";
        cache.Add(new CompiledSqlTemplate(key, "SELECT 1", ImmutableArray<SqlSlot>.Empty, TargetSqlDialect.PostgreSql, SqlStatementClass.Select,
            ImmutableArray<SecurityPredicateId>.Empty, "test", ImmutableArray<TableDependency>.Empty));

        var same = MultiChunk(key);
        cache.Find(same, CompiledSqlTemplateCache.HashOf(same)).ShouldNotBeNull();

        // Same hash supplied, different text (collision protection): the full text is still compared.
        var other = MultiChunk("v=1;sql=8:SELECT 2");
        cache.Find(other, CompiledSqlTemplateCache.HashOf(key)).ShouldBeNull();
        var shorter = MultiChunk("v=1;sql=8:SELECT ");
        cache.Find(shorter, CompiledSqlTemplateCache.HashOf(key)).ShouldBeNull();
    }
}
