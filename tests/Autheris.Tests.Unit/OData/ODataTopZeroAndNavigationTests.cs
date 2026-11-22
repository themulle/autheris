namespace Autheris.Tests.Unit.OData;

using System.Collections.Generic;
using Autheris.Tests.Common.Fakes;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Application.OData;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>EXT-19: $top=0 returns no rows (count probe) and navigation paths in $filter are rejected.</summary>
public sealed class ODataTopZeroAndNavigationTests
{
    private static readonly TableIdentifier Plain = new("erp", "main", "invoices");
    private static readonly Dictionary<string, ColumnAccessLevel> Access = new(System.StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear };

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ANALYST"), new Claim("tenant_id", "erp")], "Test"));

    [Fact]
    public async Task TopZero_ReturnsEmptyValue_WithCount_AndNoNextLink()
    {
        var rows = Enumerable.Range(1, 3).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i }).ToList();
        var execution = StubGatewayExecutionService.WithPage(new TableQueryPage(rows, TableAccessDecision.Allowed(Plain, Access), 42, false));
        var handler = ODataHandlerTestFactory.Create(new InMemoryTableMetadataRepository(), execution, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            User(), "https://gw/odata/v4", Plain, top: 0, skip: null, select: null, includeCount: true, headers: null);

        result.StatusCode.ShouldBe(200);
        var payload = (IReadOnlyDictionary<string, object?>)result.Payload;
        ((IReadOnlyList<IReadOnlyDictionary<string, object?>>)payload["value"]!).ShouldBeEmpty();
        payload["@odata.count"].ShouldBe(42L);
        payload.ShouldNotContainKey("@odata.nextLink");
    }

    [Theory]
    [InlineData("Customer/Name eq 'x'")]
    [InlineData("Orders/Amount gt 5")]
    public void NavigationPathInFilter_IsRejected(string filter)
    {
        Should.Throw<GatewayInvalidQueryException>(() => ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql))
            .Message.ShouldContain("Navigation properties are not supported");
    }
}
