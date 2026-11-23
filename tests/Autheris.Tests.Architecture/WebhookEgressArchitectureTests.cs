using System;
using System.Linq;
using Autheris.Application.Governance.Notifications;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed class WebhookEgressArchitectureTests
{
    [Fact]
    public void Notifications_use_ssrf_handler()
    {
        var eventExtensions = RepositoryScan.ProductionFiles
            .Single(f => f.RelativePath.Replace('\\', '/').EndsWith("Api/Extensions/DependencyInjection/GatewayEventServiceExtensions.cs", StringComparison.OrdinalIgnoreCase));

        eventExtensions.Text.ShouldContain("EgressIntegrations.GovernanceNotifications");
        eventExtensions.Text.ShouldContain("AddSecureOutboundHandlers(EgressIntegrations.GovernanceNotifications)");

        var dispatcher = RepositoryScan.ProductionFiles
            .Single(f => f.RelativePath.Replace('\\', '/').EndsWith("Application/Governance/Notifications/GovernanceNotificationDispatcher.cs", StringComparison.OrdinalIgnoreCase));

        dispatcher.Text.ShouldContain("EgressIntegrations.GovernanceNotifications");
        dispatcher.Text.ShouldContain("IHttpClientFactory");
        dispatcher.Text.ShouldNotContain("new HttpClient(");
        dispatcher.Text.ShouldNotContain("AllowAutoRedirect = true");
    }
}
