namespace Autheris.Tests.Unit.OData;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class ODataPermissionSetCachingTests
{
    private static readonly Regex Sha256HexRegex = new("^[a-f0-9]{64}$", RegexOptions.Compiled);

    [Fact]
    public void ComputeSha256_Produces64CharLowercaseHexDigest()
    {
        var tables = new[]
        {
            new TableIdentifier("sales", "dbo", "Orders"),
            new TableIdentifier("sales", "dbo", "Customers")
        };

        var hash = CatalogVisibleBitSet.ComputeSha256(tables);

        hash.Length.ShouldBe(64);
        Sha256HexRegex.IsMatch(hash).ShouldBeTrue();
    }

    [Fact]
    public void ComputeSha256_IsOrderIndependent_CanonicalSorting()
    {
        var t1 = new TableIdentifier("sales", "dbo", "Orders");
        var t2 = new TableIdentifier("sales", "dbo", "Customers");

        var hash1 = CatalogVisibleBitSet.ComputeSha256([t1, t2]);
        var hash2 = CatalogVisibleBitSet.ComputeSha256([t2, t1]);

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void ComputeSha256_LengthPrefixedEncoding_PreventsDelimiterCollisions()
    {
        // SEC-PERF254-01: Table names that would collide under naive concatenation
        // e.g. "a" + ":" + "b_c" vs "a_b" + ":" + "c"
        var set1 = new[] { new TableIdentifier("corp", "sales", "order_items") };
        var set2 = new[] { new TableIdentifier("corp", "sales_order", "items") };

        var hash1 = CatalogVisibleBitSet.ComputeSha256(set1);
        var hash2 = CatalogVisibleBitSet.ComputeSha256(set2);

        hash1.ShouldNotBe(hash2);
    }

    [Fact]
    public void CatalogVisibleBitSet_InstanceComputeSha256_Produces64CharHex()
    {
        var bitset = new CatalogVisibleBitSet(10);
        bitset.Set(2);
        bitset.Set(5);

        var hashInstance = bitset.ComputeSha256();

        hashInstance.Length.ShouldBe(64);
        Sha256HexRegex.IsMatch(hashInstance).ShouldBeTrue();
    }
}
