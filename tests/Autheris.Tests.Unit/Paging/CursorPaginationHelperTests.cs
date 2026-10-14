namespace Autheris.Tests.Unit.Paging;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Common.Paging;
using Shouldly;
using Xunit;

public sealed class CursorPaginationHelperTests
{
    [Fact]
    public void EncodeAndDecodeCursor_Roundtrip_Succeeds()
    {
        var cursor = CursorPaginationHelper.EncodeCursor(42);
        cursor.ShouldNotBeNullOrWhiteSpace();

        var offset = CursorPaginationHelper.DecodeCursor(cursor);
        offset.ShouldBe(42);
    }

    [Fact]
    public void DecodeCursor_NullOrEmpty_ReturnsZero()
    {
        CursorPaginationHelper.DecodeCursor(null).ShouldBe(0);
        CursorPaginationHelper.DecodeCursor("").ShouldBe(0);
        CursorPaginationHelper.DecodeCursor("   ").ShouldBe(0);
    }

    [Fact]
    public void DecodeCursor_InvalidBase64_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => CursorPaginationHelper.DecodeCursor("not-valid-base64!"));
    }

    [Fact]
    public void DecodeCursor_MissingPrefix_ThrowsArgumentException()
    {
        var badBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unknown:format:123"));
        Should.Throw<ArgumentException>(() => CursorPaginationHelper.DecodeCursor(badBase64));
    }

    [Fact]
    public void ApplyPagination_FirstPage_ReturnsRequestedItemsAndPageInfo()
    {
        var items = Enumerable.Range(1, 100).Select(i => $"Item_{i}").ToList();

        var result = CursorPaginationHelper.ApplyPagination(items, first: 10, after: null);

        result.Nodes.Count.ShouldBe(10);
        result.Nodes[0].ShouldBe("Item_1");
        result.Nodes[9].ShouldBe("Item_10");
        result.PageInfo.HasNextPage.ShouldBeTrue();
        result.PageInfo.HasPreviousPage.ShouldBeFalse();
        result.PageInfo.StartCursor.ShouldNotBeNull();
        result.PageInfo.EndCursor.ShouldNotBeNull();
        result.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public void ApplyPagination_SecondPageWithAfter_ReturnsNextItems()
    {
        var items = Enumerable.Range(1, 50).Select(i => $"Item_{i}").ToList();

        // Get first page
        var page1 = CursorPaginationHelper.ApplyPagination(items, first: 5, after: null);
        var endCursor = page1.PageInfo.EndCursor;

        // Get second page
        var page2 = CursorPaginationHelper.ApplyPagination(items, first: 5, after: endCursor);

        page2.Nodes.Count.ShouldBe(5);
        page2.Nodes[0].ShouldBe("Item_6");
        page2.Nodes[4].ShouldBe("Item_10");
        page2.PageInfo.HasPreviousPage.ShouldBeTrue();
        page2.PageInfo.HasNextPage.ShouldBeTrue();
    }

    [Fact]
    public void ApplyPagination_ExceedingMaxAllowedPageSize_SetsTruncatedFlag()
    {
        var items = Enumerable.Range(1, 1000).Select(i => $"Item_{i}").ToList();

        var result = CursorPaginationHelper.ApplyPagination(items, first: 600, after: null, maxAllowedPageSize: 500);

        result.Nodes.Count.ShouldBe(500);
        result.IsTruncated.ShouldBeTrue();
        result.PageInfo.HasNextPage.ShouldBeTrue();
    }

    [Fact]
    public void ApplyPagination_NegativeFirst_ThrowsArgumentOutOfRangeException()
    {
        var items = new List<string> { "A", "B" };
        Should.Throw<ArgumentOutOfRangeException>(() => CursorPaginationHelper.ApplyPagination(items, first: -5, after: null));
    }
}
