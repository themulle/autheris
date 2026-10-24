namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using Autheris.Extensions.DataCatalog;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>EXT-17: strict qualifiedName parsing shared by the Purview sync and webhook paths.</summary>
[Trait("Category", "Security")]
public sealed class PurviewQualifiedNameTests
{
    [Fact]
    public void Ext17_MssqlUri_MapsDbSchemaTable()
    {
        PurviewQualifiedName.TryParse("mssql://sql01.example.com:1433/finance/sales/orders", out var id).ShouldBeTrue();

        id.Domain.ShouldBe("finance");
        id.Schema.ShouldBe("sales");
        id.TableName.ShouldBe("orders");
    }

    [Fact]
    public void Ext17_BarePath_TwoAndThreeSegments()
    {
        PurviewQualifiedName.TryParse("finance_db/accounts", out var two).ShouldBeTrue();
        two.Domain.ShouldBe("finance_db");
        two.Schema.ShouldBe("dbo");
        two.TableName.ShouldBe("accounts");

        PurviewQualifiedName.TryParse("finance_db/sales/accounts", out var three).ShouldBeTrue();
        three.Schema.ShouldBe("sales");
    }

    [Fact]
    public void Ext17_SchemeNeverBecomesDomain()
    {
        PurviewQualifiedName.TryParse("mssql://host/db/sch/tbl", out var id).ShouldBeTrue();

        id.Domain.ShouldNotBe("mssql:");
        id.Domain.ShouldNotBe("mssql");
        id.Domain.ShouldNotBe("host");
        id.Domain.ShouldBe("db");

        // Scheme and host without a usable path must not produce an identifier.
        PurviewQualifiedName.TryParse("mssql://host", out _).ShouldBeFalse();
        PurviewQualifiedName.TryParse("mssql://host/onlyone", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("onlyone")]
    [InlineData("db//table")]
    [InlineData("db/schema/")]
    [InlineData("/db/table")]
    [InlineData("mssql://host/db//table")]
    [InlineData("db/ /table")]
    public void Ext17_EmptyOrInvalidSegment_ReturnsFalse(string? input)
    {
        PurviewQualifiedName.TryParse(input, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("db/%2e%2e/table")]
    [InlineData("db/../table")]
    [InlineData("db/./table")]
    [InlineData("db/a%2fb/table")]
    [InlineData("db/a%2Fb")]
    [InlineData("db/ta%00ble")]
    [InlineData("db/a\\b")]
    [InlineData("db/ta\u0001ble")]
    [InlineData("db/ta\nble")]
    [InlineData("db/t%5cx")]
    [InlineData("db/t%2541")]
    [InlineData("db/table?x=1")]
    [InlineData("db/table#frag")]
    [InlineData("javascript:alert/1")]
    [InlineData("db/sch.ema/table")]
    public void Ext17_TraversalAndEncodedSeparators_ReturnFalse(string input)
    {
        PurviewQualifiedName.TryParse(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void Ext17_TooManySegments_ReturnsFalse()
    {
        PurviewQualifiedName.TryParse("mssql://host/a/b/c/d", out _).ShouldBeFalse();
        PurviewQualifiedName.TryParse("a/b/c/d", out _).ShouldBeFalse();
    }

    [Fact]
    public void Ext17_OverLength_ReturnsFalse()
    {
        PurviewQualifiedName.TryParse("db/" + new string('x', 200), out _).ShouldBeFalse();
        PurviewQualifiedName.TryParse("db/" + new string('x', 600) + "/t", out _).ShouldBeFalse();
    }

    [Fact]
    public void Ext17_NoEmptySchemaReachesTableIdentifier()
    {
        // Every accepted name yields non-empty components; no partial or default identifier on failure.
        foreach (var input in new[] { "db/t", "mssql://h/db/s/t", "a/b/c" })
        {
            PurviewQualifiedName.TryParse(input, out var id).ShouldBeTrue();
            id.Domain.ShouldNotBeNullOrWhiteSpace();
            id.Schema.ShouldNotBeNullOrWhiteSpace();
            id.TableName.ShouldNotBeNullOrWhiteSpace();
        }

        PurviewQualifiedName.TryParse("db//t", out var rejected).ShouldBeFalse();
        rejected.ShouldBe(default);
    }
}
