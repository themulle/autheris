namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;
using Xunit;

public sealed class VirtualFilterKeyProviderTests
{
    private readonly TenantId _tenantId = new("tenant-beta");
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-TEST")
    }));

    private VirtualFilter CreateFilter(string name, string[] keyColumns)
    {
        return new VirtualFilter
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Name = name,
            Source = "mssql_ds",
            KeyColumns = keyColumns,
            Status = FilterApprovalStatus.Active
        };
    }

    [Fact]
    public async Task GetAllowedKeysAsync_InMemoryTuples_ReturnsKeys()
    {
        var provider = new DefaultVirtualFilterKeyProvider();
        var filter = CreateFilter("vf_test1", new[] { "client.org_id" });

        provider.SetInMemoryTuples("vf_test1", new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["org_id"] = "100" },
            new Dictionary<string, object?> { ["org_id"] = "105" },
            new Dictionary<string, object?> { ["org_id"] = "110" }
        });

        var keys = await provider.GetAllowedKeysAsync(filter, _user, _tenantId, "org_id");

        keys.Count.ShouldBe(3);
        keys.ShouldContain("100");
        keys.ShouldContain("105");
        keys.ShouldContain("110");
    }

    [Fact]
    public async Task GetAllowedKeyTuplesAsync_InMemoryTuples_ReturnsTuples()
    {
        var provider = new DefaultVirtualFilterKeyProvider();
        var filter = CreateFilter("vf_multi", new[] { "org.region", "org.dept_id" });

        provider.SetInMemoryTuples("vf_multi", new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["region"] = "EMEA", ["dept_id"] = 10 },
            new Dictionary<string, object?> { ["region"] = "APAC", ["dept_id"] = 20 }
        });

        var tuples = await provider.GetAllowedKeyTuplesAsync(filter, _user, _tenantId, filter.KeyColumns);

        tuples.Count.ShouldBe(2);
        tuples[0]["region"].ShouldBe("EMEA");
        tuples[0]["dept_id"].ShouldBe(10);
    }

    [Fact]
    public async Task GetAllowedKeysAsync_WithCache_UsesCachedResults()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new DefaultVirtualFilterKeyProvider(cache: cache);
        var filter = CreateFilter("vf_cached", new[] { "client.id" });

        provider.SetInMemoryTuples("vf_cached", new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "1" }
        });

        var keysFirst = await provider.GetAllowedKeysAsync(filter, _user, _tenantId, "id");
        keysFirst.ShouldContain("1");

        // Change underlying data
        provider.SetInMemoryTuples("vf_cached", new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "2" }
        });

        // Second call should return cached "1"
        var keysSecond = await provider.GetAllowedKeysAsync(filter, _user, _tenantId, "id");
        keysSecond.ShouldContain("1");
    }

    [Fact]
    public async Task GetAllowedKeysAsync_MissingConnector_FailsClosed()
    {
        var provider = new DefaultVirtualFilterKeyProvider();
        var filter = CreateFilter("vf_no_connector", new[] { "client.id" });

        // No in-memory tuples, no connector registry -> must throw GatewaySecurityException
        await Should.ThrowAsync<GatewaySecurityException>(() =>
            provider.GetAllowedKeysAsync(filter, _user, _tenantId, "id").AsTask());
    }
}
