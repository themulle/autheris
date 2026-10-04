using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Xunit;

namespace Autheris.Tests.Unit;

public class CompositeKeyTests
{
    [Fact]
    public void CompositeKey_EqualityAndHashCode_WorkCorrectly()
    {
        var key1 = new CompositeKey("TENANT_01", 100);
        var key2 = new CompositeKey("TENANT_01", 100);
        var key3 = new CompositeKey("TENANT_02", 100);
        var key4 = new CompositeKey("TENANT_01", 101);

        Assert.Equal(key1, key2);
        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
        Assert.NotEqual(key1, key3);
        Assert.NotEqual(key1, key4);
        Assert.True(key1 == key2);
        Assert.False(key1 == key3);
    }

    [Fact]
    public void CompositeKey_EqualityAndComparison_AreConsistentAcrossNumericTypes()
    {
        var keyInt = new CompositeKey("TENANT_01", (int)100);
        var keyLong = new CompositeKey("TENANT_01", (long)100);

        Assert.Equal(keyInt, keyLong);
        Assert.Equal(keyInt.GetHashCode(), keyLong.GetHashCode());
        Assert.Equal(0, keyInt.CompareTo(keyLong));
        Assert.True(keyInt == keyLong);

        var dict = new Dictionary<CompositeKey, string> { [keyInt] = "test" };
        Assert.True(dict.ContainsKey(keyLong));
        Assert.Equal("test", dict[keyLong]);
    }

    [Fact]
    public void CompositeKey_DictionaryLookup_FindsValueByEqualCompositeKey()
    {
        var key1 = new CompositeKey("DE", 2026, "INV-001");
        var key2 = new CompositeKey("DE", 2026, "INV-001");

        var dict = new Dictionary<CompositeKey, string>
        {
            [key1] = "Invoice DE 2026-001"
        };

        Assert.True(dict.ContainsKey(key2));
        Assert.Equal("Invoice DE 2026-001", dict[key2]);
    }

    [Fact]
    public void CompositeKey_ToString_FormatsCleanly()
    {
        var key = new CompositeKey("CORP", 42, "ITEM_A");
        Assert.Equal("(CORP, 42, ITEM_A)", key.ToString());
    }

    [Fact]
    public void TableMetadata_PrimaryKeyColumns_IdentifiesSingleAndCompositeKeys()
    {
        var singlePkMeta = new TableMetadata
        {
            PrimaryKeyColumns = new[] { "id" }
        };
        Assert.False(singlePkMeta.IsCompositePrimaryKey);
        Assert.Single(singlePkMeta.PrimaryKeyColumns);

        var compositePkMeta = new TableMetadata
        {
            PrimaryKeyColumns = new[] { "company_code", "fiscal_year", "doc_no" }
        };
        Assert.True(compositePkMeta.IsCompositePrimaryKey);
        Assert.Equal(3, compositePkMeta.PrimaryKeyColumns.Count);
    }

    [Fact]
    public void TableRelation_CompositeJoinKeys_IdentifiesCompositeRelation()
    {
        var singleRel = new TableRelation
        {
            JoinKeysParent = new[] { "id" },
            JoinKeysChild = new[] { "parent_id" }
        };
        Assert.False(singleRel.IsComposite);

        var compositeRel = new TableRelation
        {
            JoinKeysParent = new[] { "tenant_id", "order_id" },
            JoinKeysChild = new[] { "tenant_id", "order_id" }
        };
        Assert.True(compositeRel.IsComposite);
        Assert.Equal(2, compositeRel.JoinKeysParent.Count);
        Assert.Equal(2, compositeRel.JoinKeysChild.Count);
    }
}
