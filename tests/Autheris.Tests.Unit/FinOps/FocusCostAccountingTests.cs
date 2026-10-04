namespace Autheris.Tests.Unit.FinOps;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.FinOps.Interfaces;
using Autheris.Application.FinOps.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class FocusCostAccountingTests
{
    private readonly GatewayOptions _options;

    public FocusCostAccountingTests()
    {
        _options = new GatewayOptions
        {
            FinOps = new FinOpsOptions
            {
                Enabled = true,
                DefaultMonthlyBudget = 100m,
                SoftCapRatio = 0.8,
                PricePerThousandPromptTokens = 0.003m,
                PricePerThousandCompletionTokens = 0.015m,
                PricePerComputeSecond = 0.0001m
            }
        };
    }

    [Fact]
    public async Task RecordUsage_CalculatesCostCorrectly_AccordingToFOCUSv12()
    {
        // Arrange
        var service = new FocusCostAccountingService(
            Options.Create(_options),
            NullLogger<FocusCostAccountingService>.Instance);

        // 10,000 prompt tokens = 10 * 0.003 = 0.030 EUR
        // 2,000 completion tokens = 2 * 0.015 = 0.030 EUR
        // 500 ms compute = 0.5 * 0.0001 = 0.00005 EUR
        // Total expected = 0.06005 EUR

        // Act
        await service.RecordUsageAsync(
            tenantId: "tenant-acme",
            principalId: "user-123",
            operationName: "GetEnterpriseAnalytics",
            category: "AI-Inference",
            promptTokens: 10000,
            completionTokens: 2000,
            computeMs: 500,
            tags: new Dictionary<string, string> { ["model"] = "gemini-1.5-pro" },
            ct: CancellationToken.None);

        var records = new List<FocusCostRecord>();
        await foreach (var r in service.GetRecordsAsync(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), "tenant-acme"))
        {
            records.Add(r);
        }

        // Assert
        records.Count.ShouldBe(1);
        var rec = records[0];
        rec.SubAccountId.ShouldBe("tenant-acme");
        rec.ResourceId.ShouldBe("GetEnterpriseAnalytics");
        rec.Currency.ShouldBe("EUR");
        rec.PricingCategory.ShouldBe("AI-Inference");
        rec.BilledCost.ShouldBe(0.06005m);
        rec.EffectiveCost.ShouldBe(0.06005m);
        rec.ConsumedQuantity.ShouldBe(12000);
        rec.ConsumedUnit.ShouldBe("Tokens");
        rec.Tags.ShouldNotBeNull();
        rec.Tags!["model"].ShouldBe("gemini-1.5-pro");
    }

    [Fact]
    public async Task CheckBudget_SoftCapWarning_WhenThresholdReached()
    {
        // Arrange
        var customOptions = new GatewayOptions
        {
            FinOps = new FinOpsOptions
            {
                Enabled = true,
                DefaultMonthlyBudget = 10m, // 10 EUR budget
                SoftCapRatio = 0.8, // 8 EUR warning threshold
                PricePerThousandPromptTokens = 0.003m,
                PricePerThousandCompletionTokens = 0.015m
            }
        };

        var service = new FocusCostAccountingService(
            Options.Create(customOptions),
            NullLogger<FocusCostAccountingService>.Instance);

        // Record usage that reaches ~8.50 EUR (e.g. 500k completion tokens = 500 * 0.015 = 7.50 EUR + 334k prompt tokens = 1.00 EUR = 8.50 EUR)
        await service.RecordUsageAsync("tenant-warning", "user-1", "Query", "AI-Inference", promptTokens: 334000, completionTokens: 500000, computeMs: 0);

        // Act
        var status = await service.CheckBudgetAsync("tenant-warning");

        // Assert
        status.IsWarning.ShouldBeTrue();
        status.IsExceeded.ShouldBeFalse();
        status.CurrentSpend.ShouldBeGreaterThanOrEqualTo(8.0m);
        status.BudgetLimit.ShouldBe(10m);
    }

    [Fact]
    public async Task CheckBudget_HardCapExceeded_WhenBudgetLimitPassed()
    {
        // Arrange
        var customOptions = new GatewayOptions
        {
            FinOps = new FinOpsOptions
            {
                Enabled = true,
                DefaultMonthlyBudget = 5m,
                PricePerThousandCompletionTokens = 0.015m
            }
        };

        var service = new FocusCostAccountingService(
            Options.Create(customOptions),
            NullLogger<FocusCostAccountingService>.Instance);

        // Record usage of 6.00 EUR (400k completion tokens * 0.015 = 6.00 EUR)
        await service.RecordUsageAsync("tenant-exceeded", "user-1", "Query", "AI-Inference", 0, 400000, 0);

        // Act
        var status = await service.CheckBudgetAsync("tenant-exceeded");

        // Assert
        status.IsExceeded.ShouldBeTrue();
        status.CurrentSpend.ShouldBe(6.0m);
        status.BudgetLimit.ShouldBe(5m);
    }

    [Fact]
    public async Task GetRecordsAsync_FiltersByTenantAndDate()
    {
        // Arrange
        var service = new FocusCostAccountingService(
            Options.Create(_options),
            NullLogger<FocusCostAccountingService>.Instance);

        await service.RecordUsageAsync("tenant-a", "user-1", "OpA", "AI", 1000, 0, 10);
        await service.RecordUsageAsync("tenant-b", "user-2", "OpB", "AI", 2000, 0, 10);

        // Act: Filter specifically for tenant-a
        var recordsA = new List<FocusCostRecord>();
        await foreach (var r in service.GetRecordsAsync(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), "tenant-a"))
        {
            recordsA.Add(r);
        }

        // Assert
        recordsA.Count.ShouldBe(1);
        recordsA[0].SubAccountId.ShouldBe("tenant-a");
    }

#pragma warning disable CA2012
    [Fact]
    public async Task FinOpsBudgetMiddleware_RejectsRequest_With429_WhenBudgetExceeded()
    {
        // Arrange
        var accounting = Substitute.For<IFinOpsAccountingService>();
        accounting.IsEnabled.Returns(true);
        accounting.CheckBudgetAsync("tenant-overlimit", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<BudgetStatus>(new BudgetStatus(
                IsExceeded: true,
                IsWarning: true,
                CurrentSpend: 150m,
                BudgetLimit: 100m,
                TenantId: "tenant-overlimit")));

        var middleware = new FinOpsBudgetMiddleware(
            next: (ctx) => Task.CompletedTask,
            NullLogger<FinOpsBudgetMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";
        context.Response.Body = new MemoryStream();

        var claims = new[] { new Claim("tenant_id", "tenant-overlimit") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        // Act
        await middleware.InvokeAsync(context, accounting);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        responseText.ShouldContain("FINOPS_BUDGET_EXCEEDED");
    }

    [Fact]
    public async Task FinOpsBudgetMiddleware_AddsWarningHeader_WhenSoftCapReached()
    {
        // Arrange
        var accounting = Substitute.For<IFinOpsAccountingService>();
        accounting.IsEnabled.Returns(true);
        accounting.CheckBudgetAsync("tenant-softcap", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<BudgetStatus>(new BudgetStatus(
                IsExceeded: false,
                IsWarning: true,
                CurrentSpend: 85m,
                BudgetLimit: 100m,
                TenantId: "tenant-softcap")));

        var middleware = new FinOpsBudgetMiddleware(
            next: (ctx) => Task.CompletedTask,
            NullLogger<FinOpsBudgetMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";

        var claims = new[] { new Claim("tenant_id", "tenant-softcap") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        // Act
        await middleware.InvokeAsync(context, accounting);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers.ContainsKey("X-FinOps-Budget-Warning").ShouldBeTrue();
    }

    [Fact]
    public void BuildFocusCsv_NeutralizesFormulaInjection_AndEscapesQuotes()
    {
        // Arrange
        var records = new List<FocusCostRecord>
        {
            new(
                ChargePeriodStart: "2026-10-01T00:00:00Z",
                ChargePeriodEnd: "2026-10-02T00:00:00Z",
                BilledCost: 1.25m,
                EffectiveCost: 1.25m,
                Currency: "EUR",
                ConsumedQuantity: 1000,
                ConsumedUnit: "Tokens",
                SubAccountId: "=cmd|' /C calc'!A0",
                ResourceId: "Query\"WithQuotes",
                ServiceName: "+@maliciousService",
                PricingCategory: "AI-Inference"
            )
        };

        // Act (using reflection to invoke private BuildFocusCsv method on FinOpsEndpoints)
        var buildMethod = typeof(Autheris.Api.Endpoints.FinOpsEndpoints)
            .GetMethod("BuildFocusCsv", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        buildMethod.ShouldNotBeNull();

        var csv = (string)buildMethod.Invoke(null, new object[] { records })!;

        // Assert: formula prefixes must be neutralized with leading single quote
        csv.ShouldContain("\"'=cmd|' /C calc'!A0\"");
        csv.ShouldContain("\"Query\"\"WithQuotes\"");
        csv.ShouldContain("\"'+@maliciousService\"");
    }
#pragma warning restore CA2012
}
