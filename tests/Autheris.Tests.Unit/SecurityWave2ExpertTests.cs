namespace Autheris.Tests.Unit;

using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Extensions.Cdc;
using HotChocolate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class SecurityWave2ExpertTests
{
    [Theory]
    [InlineData("dbo; DROP TABLE Users;--", "invoices")]
    [InlineData("dbo", "invoices]; DROP TABLE Users;--")]
    [InlineData("dbo", "inv oices")]
    [InlineData("dbo", "invoices' OR '1'='1")]
    public async Task MssqlChangeTracking_SqlInjection_Prevented_WhenTableHasMaliciousName(string schema, string tableName)
    {
        var factory = Substitute.For<ISqlConnectionFactory>();
        var watermarkStore = new InMemoryMssqlWatermarkStore();
        var eventChannel = Substitute.For<ICdcEventChannel>();
        var options = Options.Create(new GatewayOptions
        {
            MssqlChangeTracking = new MssqlChangeTrackingOptions
            {
                Enabled = true,
                ConnectionString = "Server=localhost;Database=Test;"
            }
        });
        var logger = Substitute.For<ILogger<MssqlChangeTrackingPoller>>();

        var poller = new MssqlChangeTrackingPoller(factory, watermarkStore, eventChannel, options, logger);
        var table = new TableIdentifier("finance", schema, tableName);

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await poller.PollTableChangesAsync(table);
        });
    }

    [Fact]
    public void QuickstartProfile_InProduction_ThrowsValidationException_FailClosed()
    {
        var options = new GatewayOptions
        {
            Profile = "Quickstart"
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
        {
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv);
        });

        ex.Message.ShouldContain("Security violation: The GettingStarted profile 'Quickstart' may be active ONLY in the Development environment.");
    }

    [Fact]
    public void QuickstartProfile_InDevelopment_IsPermitted()
    {
        // The Quickstart relaxations are expanded into concrete Insecure.* values by DevConfiguration
        // (legacy "Profile" is an alias for Dev:Preset), not implied by the accessors any more.
        var devEnv = Substitute.For<IHostEnvironment>();
        devEnv.EnvironmentName.Returns("Development");

        var config = new Microsoft.Extensions.Configuration.ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Profile"] = "Quickstart" });
        Autheris.Api.Configuration.DevConfiguration.Apply(config, devEnv);
        var options = config.GetSection("Gateway").Get<GatewayOptions>()!;

        Should.NotThrow(() =>
        {
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, devEnv);
        });

        options.IsOpenSchemaAllowed.ShouldBeTrue();
        options.IsIntrospectionForced.ShouldBeTrue();
        options.IsAllCorsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void ErrorSanitizingFilter_InDevelopment_AddsActionableFixItHints()
    {
        var devEnv = Substitute.For<IHostEnvironment>();
        devEnv.EnvironmentName.Returns("Development");
        var logger = Substitute.For<ILogger<ErrorSanitizingFilter>>();
        var filter = new ErrorSanitizingFilter(devEnv, logger);

        var unauthError = ErrorBuilder.New()
            .SetMessage("Authentication is required for catalog queries.")
            .SetCode("UNAUTHORIZED")
            .Build();

        var sanitized = filter.OnError(unauthError);
        sanitized.Extensions.ShouldNotBeNull();
        sanitized.Extensions!.ContainsKey("dev_fix_hints").ShouldBeTrue();
        var hints = sanitized.Extensions["dev_fix_hints"] as string[];
        hints.ShouldNotBeNull();
        hints!.Length.ShouldBeGreaterThan(0);
        hints[0].ShouldContain("X-Test-User-Sid");
    }

    [Fact]
    public void ErrorSanitizingFilter_InProduction_NeverExposesFixHints()
    {
        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");
        var logger = Substitute.For<ILogger<ErrorSanitizingFilter>>();
        var filter = new ErrorSanitizingFilter(prodEnv, logger);

        var unauthError = ErrorBuilder.New()
            .SetMessage("Authentication is required for catalog queries.")
            .SetCode("UNAUTHORIZED")
            .Build();

        var sanitized = filter.OnError(unauthError);
        if (sanitized.Extensions != null)
        {
            sanitized.Extensions.ContainsKey("dev_fix_hints").ShouldBeFalse();
        }
    }

    [Fact]
    public void MssqlChangeTracking_ParseTrackedTables_HandlesVariousFormats()
    {
        var list = MssqlChangeTrackingHostedService.ParseTrackedTables(
        [
            "finance.dbo.invoices",
            "hr.employees",
            "orders",
            "",
            "   "
        ]);

        list.Count.ShouldBe(3);
        list[0].ShouldBe(new TableIdentifier("finance", "dbo", "invoices"));
        list[1].ShouldBe(new TableIdentifier("hr", "dbo", "employees"));
        list[2].ShouldBe(new TableIdentifier("default", "dbo", "orders"));
    }

    [Fact]
    public async Task WatermarkStore_SetsAndAdvancesWatermark_Safely()
    {
        var store = new InMemoryMssqlWatermarkStore();
        var table = new TableIdentifier("finance", "dbo", "invoices");

        var initial = await store.GetWatermarkAsync(table);
        initial.ShouldBe(0);

        await store.SetWatermarkAsync(table, 100);
        var v1 = await store.GetWatermarkAsync(table);
        v1.ShouldBe(100);

        // Watermark should not go backwards
        await store.SetWatermarkAsync(table, 50);
        var v2 = await store.GetWatermarkAsync(table);
        v2.ShouldBe(100);

        await store.SetWatermarkAsync(table, 150);
        var v3 = await store.GetWatermarkAsync(table);
        v3.ShouldBe(150);
    }
}
