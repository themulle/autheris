namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Caching.TableCache;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class TableCacheQueryRouterTests
{
    [Fact]
    public async Task Router_AllColumnsCovered_RoutesToCache()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var table = new TableIdentifier("crm", "public", "customers");

        catalog.Upsert(new TableCacheEntryMetadata(
            table,
            TableCacheStorageTier.DuckDbDisk,
            "/cache/crm_customers.duckdb",
            "crm_public_customers",
            1,
            DateTimeOffset.UtcNow,
            null,
            100,
            2048,
            null,
            true,
            null,
            ["id", "tenant_id", "name", "country", "status"],
            FilterPredicate: null,
            OfflinePolicy: TableCacheOfflinePolicy.FailClosed));

        var router = new TableCacheQueryRouter(catalog);

        // Act
        var decision = await router.EvaluateRoutingAsync(table, ["id", "name", "status"], whereClause: null);

        // Assert
        decision.IsCached.ShouldBeTrue();
        decision.CanSatisfyFully.ShouldBeTrue();
        decision.InternalStorageTarget.ShouldBe("crm_public_customers");
        decision.MissingColumns.ShouldBeNull();
    }

    [Fact]
    public async Task Router_MissingColumn_FallsBackToLiveSource()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var table = new TableIdentifier("crm", "public", "customers");

        catalog.Upsert(new TableCacheEntryMetadata(
            table,
            TableCacheStorageTier.DuckDbDisk,
            "/cache/crm_customers.duckdb",
            "crm_public_customers",
            1,
            DateTimeOffset.UtcNow,
            null,
            100,
            2048,
            null,
            true,
            null,
            ["id", "name"], // Missing "credit_card"
            FilterPredicate: null,
            OfflinePolicy: TableCacheOfflinePolicy.FailClosed));

        var router = new TableCacheQueryRouter(catalog);

        // Act
        var decision = await router.EvaluateRoutingAsync(table, ["id", "name", "credit_card"], whereClause: null);

        // Assert
        decision.IsCached.ShouldBeTrue();
        decision.CanSatisfyFully.ShouldBeFalse();
        decision.MissingColumns.ShouldNotBeNull();
        decision.MissingColumns.ShouldContain("credit_card");
        decision.FallbackReason.ShouldNotBeNull();
        decision.FallbackReason.ShouldContain("lacks requested columns");
    }

    [Fact]
    public void Router_DirectAccessToStagingOrRawFiles_DeniesWith403()
    {
        // Arrange (T-SEC-BL04-11)
        var catalog = new TableCacheCatalog();
        var router = new TableCacheQueryRouter(catalog);

        // Act & Assert: Attempting to directly query internal shadow table
        Should.Throw<SecurityException>(() =>
        {
            TableCacheQueryRouter.ValidateIdentifier("staging_customers_1729384800");
        }).Message.ShouldContain("INTERNAL_TABLE_ACCESS_PROHIBITED");

        Should.Throw<SecurityException>(() =>
        {
            TableCacheQueryRouter.ValidateIdentifier("autheris_cache_staging");
        }).Message.ShouldContain("INTERNAL_TABLE_ACCESS_PROHIBITED");

        Should.Throw<SecurityException>(() =>
        {
            TableCacheQueryRouter.ValidateIdentifier("customers; DROP TABLE users; --");
        }).Message.ShouldContain("INTERNAL_TABLE_ACCESS_PROHIBITED");
    }

    [Fact]
    public async Task Router_CrossSourceJoin_EvaluatesRoutingDecision()
    {
        // Arrange (AC-06)
        var catalog = new TableCacheCatalog();
        var cachedTable = new TableIdentifier("crm", "public", "customers");
        var liveTable = new TableIdentifier("erp", "dbo", "orders");

        catalog.Upsert(new TableCacheEntryMetadata(
            cachedTable,
            TableCacheStorageTier.DuckDbDisk,
            "/cache/crm_customers.duckdb",
            "crm_public_customers",
            1,
            DateTimeOffset.UtcNow,
            null,
            500,
            4096,
            null,
            true,
            null,
            ["id", "company_name"],
            FilterPredicate: null,
            OfflinePolicy: TableCacheOfflinePolicy.FailClosed));

        var router = new TableCacheQueryRouter(catalog);

        // Act
        var cachedDecision = await router.EvaluateRoutingAsync(cachedTable, ["id", "company_name"], whereClause: null);
        var liveDecision = await router.EvaluateRoutingAsync(liveTable, ["order_id", "total"], whereClause: null);

        // Assert
        cachedDecision.IsCached.ShouldBeTrue();
        cachedDecision.CanSatisfyFully.ShouldBeTrue();
        cachedDecision.InternalStorageTarget.ShouldBe("crm_public_customers");

        liveDecision.IsCached.ShouldBeFalse();
        liveDecision.CanSatisfyFully.ShouldBeFalse();
    }
}
