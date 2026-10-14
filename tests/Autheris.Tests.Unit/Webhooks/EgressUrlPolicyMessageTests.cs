namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EgressUrlPolicyMessageTests
{
    [Fact]
    public void ValidateStatic_PrivateIpLiteral_MessageDoesNotContainIp()
    {
        var ex = Should.Throw<SecurityException>(() => EgressUrlPolicy.ValidateStatic(new Uri("https://10.1.2.3/hook")));
        ex.Message.ShouldNotContain("10.1.2.3");
        ex.Message.ShouldContain("strictly forbidden");
        ex.Data["ResolvedIp"].ShouldBe("10.1.2.3");
    }

    [Fact]
    public async Task ValidateResolvedAsync_PrivateIpLiteral_MessageDoesNotContainIp()
    {
        var ex = await Should.ThrowAsync<SecurityException>(() => EgressUrlPolicy.ValidateResolvedAsync(new Uri("https://192.168.7.9/hook"), isDev: false));
        ex.Message.ShouldNotContain("192.168.7.9");
    }
}
