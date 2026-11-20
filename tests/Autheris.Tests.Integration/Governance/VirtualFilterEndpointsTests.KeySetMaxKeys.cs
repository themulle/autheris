namespace Autheris.Tests.Integration.Governance;

using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

public sealed partial class VirtualFilterEndpointsTests
{
    [Fact]
    public async Task PutAndGetFilter_WithKeySetMaxKeys_RoundTripsAndRejectsLiveMode()
    {
        var liveRejected = Context(Tenant, DavidFilterBody(evaluationMode: "Live", keySetMaxKeys: 250), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", liveRejected, _service), liveRejected))
            .ShouldBe(StatusCodes.Status400BadRequest);

        var putKeySet = Context(Tenant, DavidFilterBody(evaluationMode: "KeySet", keySetMaxKeys: 250), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", putKeySet, _service), putKeySet))
            .ShouldBe(StatusCodes.Status200OK);

        var get = Context(Tenant, null, "FilterAdmin");
        var getResult = await VirtualFilterEndpoints.ListAsync(get, _service);
        await getResult.ExecuteAsync(get);
        get.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        get.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(get.Response.Body);
        doc.RootElement.GetProperty("filters")[0].GetProperty("key_set_max_keys").GetInt32().ShouldBe(250);
    }
}
