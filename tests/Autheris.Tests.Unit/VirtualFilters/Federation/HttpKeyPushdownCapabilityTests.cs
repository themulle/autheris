namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-8 (M7, SEC-15): the pushdown capability is derived from the endpoint descriptor only; anything the
/// executor would drop, reject or answer incompletely is "no capability", and unsafe key values are not pushable.
/// </summary>
public sealed class HttpKeyPushdownCapabilityTests
{
    private static HttpEndpointDescriptor Descriptor(
        HttpBatchType batch = HttpBatchType.QueryParameterList,
        string? param = "ids",
        string path = "/customers",
        string key = "id",
        HttpPaginationConfig? paging = null,
        string? tenantParam = null) =>
        new()
        {
            BaseUrl = "https://api.example.internal",
            PathTemplate = path,
            BatchType = batch,
            BatchParamName = param,
            PrimaryKeyField = key,
            Pagination = paging,
            TenantIdQueryParam = tenantParam,
            CompleteResponse = true
        };

    [Theory]
    [InlineData(HttpBatchType.QueryParameterList, "/customers", HttpKeyPushdownKind.QueryParameterList)]
    [InlineData(HttpBatchType.JsonBodyArray, "/customers/batch", HttpKeyPushdownKind.JsonBodyArray)]
    [InlineData(HttpBatchType.ParallelSingleRequests, "/api/v1/customers/{id}", HttpKeyPushdownKind.PathLookup)]
    public void SupportedBatchTypes_ResolveToTheirKind(HttpBatchType batch, string path, HttpKeyPushdownKind kind)
    {
        var capability = HttpKeyPushdownCapability.Resolve(Descriptor(batch, path: path));

        capability.Kind.ShouldBe(kind);
        capability.ParameterName.ShouldBe("ids");
        capability.KeyField.ShouldBe("id");
    }

    [Fact]
    public void PagedEndpoint_HasNoCapability()
    {
        // The executor's batch path bypasses pagination; an incomplete page would be silently missing.
        var paged = new HttpPaginationConfig(HttpPaginationStrategy.PageNumber);

        HttpKeyPushdownCapability.Resolve(Descriptor(paging: paged)).Kind.ShouldBe(HttpKeyPushdownKind.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a b")]
    [InlineData("ids&x=1")]
    [InlineData("tenant_id")]      // reserved (gateway-set) names are dropped by the executor
    [InlineData("TenantId")]
    public void MissingOrUnusableParameterName_HasNoCapability(string? name)
    {
        // ST-28: BuildUrl would drop the argument and fetch everything.
        HttpKeyPushdownCapability.Resolve(Descriptor(param: name)).Kind.ShouldBe(HttpKeyPushdownKind.None);
    }

    [Fact]
    public void ParameterNameEqualToTheTenantParameter_HasNoCapability()
    {
        HttpKeyPushdownCapability.Resolve(Descriptor(param: "org", tenantParam: "org")).Kind.ShouldBe(HttpKeyPushdownKind.None);
    }

    [Fact]
    public void BatchTypeNone_HasNoCapability()
    {
        HttpKeyPushdownCapability.Resolve(Descriptor(HttpBatchType.None)).Kind.ShouldBe(HttpKeyPushdownKind.None);
    }

    [Theory]
    [InlineData("/customers")]                 // placeholder missing: the key would become a query parameter
    [InlineData("/api/{customerId}")]
    public void ParallelSingleRequests_WithoutPrimaryKeyPlaceholder_HasNoCapability(string path)
    {
        HttpKeyPushdownCapability.Resolve(Descriptor(HttpBatchType.ParallelSingleRequests, path: path)).Kind.ShouldBe(HttpKeyPushdownKind.None);
    }

    [Fact]
    public void NoDescriptor_HasNoCapability()
    {
        HttpKeyPushdownCapability.Resolve(null).Kind.ShouldBe(HttpKeyPushdownKind.None);
    }

    [Theory]
    [InlineData(HttpBatchType.QueryParameterList, "47", true)]
    [InlineData(HttpBatchType.QueryParameterList, "a,b", false)]            // the list separator
    [InlineData(HttpBatchType.QueryParameterList, "", false)]
    [InlineData(HttpBatchType.QueryParameterList, "a\nb", false)]
    [InlineData(HttpBatchType.JsonBodyArray, "a,b", true)]                  // serialized, never concatenated
    [InlineData(HttpBatchType.JsonBodyArray, "x\\\", \"*", true)]
    [InlineData(HttpBatchType.JsonBodyArray, "", false)]
    [InlineData(HttpBatchType.ParallelSingleRequests, "A/B", false)]        // ST-29
    [InlineData(HttpBatchType.ParallelSingleRequests, "..", false)]
    [InlineData(HttpBatchType.ParallelSingleRequests, "a\\b", false)]
    [InlineData(HttpBatchType.ParallelSingleRequests, "a,b", true)]
    public void KeyValues_AreCheckedPerKind(HttpBatchType batch, string value, bool eligible)
    {
        var path = batch == HttpBatchType.ParallelSingleRequests ? "/c/{id}" : "/c";

        HttpKeyPushdownCapability.Resolve(Descriptor(batch, path: path)).IsValueEligible(value).ShouldBe(eligible);
    }

    [Fact]
    public void OverlongValue_IsNotEligible()
    {
        HttpKeyPushdownCapability.Resolve(Descriptor()).IsValueEligible(new string('x', 257)).ShouldBeFalse();
        HttpKeyPushdownCapability.Resolve(Descriptor()).IsValueEligible(new string('x', 256)).ShouldBeTrue();
    }
}
