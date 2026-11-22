#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Federation;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SubgraphFieldMaskingExecutorTests
{
    public sealed class TestUser
    {
        public string Id { get; set; } = "123";
        public string Name { get; set; } = "Alice";
        public string Email { get; set; } = "alice@example.com";
        public string Department { get; set; } = "Engineering";
        public List<string> Tags { get; set; } = ["tag-secret-1", "tag-secret-2"];
        public string[] Labels { get; set; } = ["lbl-secret-1", "lbl-secret-2"];
    }

    public sealed class TestQuery
    {
        public TestUser User => new();
    }

    private static IColumnMaskingProvider CreateMaskingProvider()
    {
        var provider = Substitute.For<IColumnMaskingProvider>();
        provider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>())
            .Returns(ci => $"[MASKED:{ci.Arg<string>()}]");
        return provider;
    }

    private static async Task<IRequestExecutor> CreateExecutorAsync(
        IColumnMaskingProvider maskingProvider,
        ISubgraphResultMasker masker,
        GatewayOptions gatewayOptions)
    {
        var services = new ServiceCollection();
        services.AddSingleton(maskingProvider);
        services.AddSingleton(masker);
        services.AddSingleton(Options.Create(gatewayOptions));

        return await services.AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .AddFusionResultMasking()
            .BuildRequestExecutorAsync();
    }

    private static ClaimsPrincipal CreateNonAdminUser(string tenant = "tenant-a") =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Reader"), new Claim("tenant_id", tenant), new Claim(ClaimTypes.PrimarySid, "S-1-USER")],
            "TestAuth"));

    private static ClaimsPrincipal CreateAdminUser(string tenant = "tenant-a") =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "GovernanceAdmin"), new Claim("tenant_id", tenant), new Claim(ClaimTypes.PrimarySid, "S-1-ADMIN")],
            "TestAuth"));

    [Fact]
    public async Task Executor_EmailField_NonAdmin_ReturnsMasked()
    {
        var maskingProvider = CreateMaskingProvider();
        var gatewayOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        };

        var masker = new SubgraphResultMasker(
            maskingProvider,
            Options.Create(gatewayOptions),
            NullLogger<SubgraphResultMasker>.Instance);

        var executor = await CreateExecutorAsync(maskingProvider, masker, gatewayOptions);
        var request = OperationRequestBuilder.New()
            .SetDocument("{ user { email } }")
            .SetGlobalState(nameof(ClaimsPrincipal), CreateNonAdminUser())
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var result = executionResult as OperationResult;
        result.ShouldNotBeNull();
        (result.Errors == null || result.Errors.Count == 0).ShouldBeTrue();

        using var doc = JsonDocument.Parse(executionResult.ToJson());
        var email = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("email").GetString();
        email.ShouldBe("[MASKED:email]");

        result.ContextData.ShouldNotBeNull();
        result.ContextData.ShouldNotBeNull();
        result.ContextData.ContainsKey("MaskingApplied").ShouldBeTrue();
    }

    [Fact]
    public async Task Executor_AliasedSensitiveField_Masked()
    {
        var maskingProvider = CreateMaskingProvider();
        var gatewayOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        };

        var masker = new SubgraphResultMasker(
            maskingProvider,
            Options.Create(gatewayOptions),
            NullLogger<SubgraphResultMasker>.Instance);

        var executor = await CreateExecutorAsync(maskingProvider, masker, gatewayOptions);
        var request = OperationRequestBuilder.New()
            .SetDocument("{ user { contact: email } }")
            .SetGlobalState(nameof(ClaimsPrincipal), CreateNonAdminUser())
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var result = executionResult as OperationResult;
        result.ShouldNotBeNull();
        (result.Errors == null || result.Errors.Count == 0).ShouldBeTrue();

        using var doc = JsonDocument.Parse(executionResult.ToJson());
        var contact = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("contact").GetString();
        contact.ShouldBe("[MASKED:contact]");

        result.ContextData.ShouldNotBeNull();
        result.ContextData.ShouldNotBeNull();
        result.ContextData.ContainsKey("MaskingApplied").ShouldBeTrue();
    }

    [Fact]
    public async Task Executor_CatalogUnavailable_UnknownScalarMasked()
    {
        var maskingProvider = CreateMaskingProvider();
        var gatewayOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true,
                DefaultMaskUnknownSensitiveScalars = true
            }
        };

        var catalog = Substitute.For<IFederatedMaskingCatalog>();
        catalog.GetClassificationsAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyDictionary<string, MaskingRule>>>(_ => throw new InvalidOperationException("catalog unavailable"));

        var masker = new SubgraphResultMasker(
            maskingProvider,
            Options.Create(gatewayOptions),
            NullLogger<SubgraphResultMasker>.Instance,
            catalog: catalog);

        var executor = await CreateExecutorAsync(maskingProvider, masker, gatewayOptions);
        var request = OperationRequestBuilder.New()
            .SetDocument("{ user { department } }")
            .SetGlobalState(nameof(ClaimsPrincipal), CreateNonAdminUser())
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var result = executionResult as OperationResult;
        result.ShouldNotBeNull();
        (result.Errors == null || result.Errors.Count == 0).ShouldBeTrue();

        using var doc = JsonDocument.Parse(executionResult.ToJson());
        var dept = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("department").GetString();
        dept.ShouldBe("[MASKED:department]");

        result.ContextData.ShouldNotBeNull();
        result.ContextData.ShouldNotBeNull();
        result.ContextData.ContainsKey("MaskingApplied").ShouldBeTrue();
    }

    [Fact]
    public async Task Executor_AdminBypass_ReturnsClear()
    {
        var maskingProvider = CreateMaskingProvider();
        var gatewayOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        };

        var masker = new SubgraphResultMasker(
            maskingProvider,
            Options.Create(gatewayOptions),
            NullLogger<SubgraphResultMasker>.Instance);

        var executor = await CreateExecutorAsync(maskingProvider, masker, gatewayOptions);
        var request = OperationRequestBuilder.New()
            .SetDocument("{ user { email } }")
            .SetGlobalState(nameof(ClaimsPrincipal), CreateAdminUser())
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var result = executionResult as OperationResult;
        result.ShouldNotBeNull();
        (result.Errors == null || result.Errors.Count == 0).ShouldBeTrue();

        using var doc = JsonDocument.Parse(executionResult.ToJson());
        var email = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("email").GetString();
        email.ShouldBe("alice@example.com");

        result.ContextData.ShouldNotBeNull();
        result.ContextData.ContainsKey("MaskingApplied").ShouldBeFalse();
    }

    [Fact]
    public async Task Executor_NoMaskedField_MaskingAppliedNotSet()
    {
        var maskingProvider = CreateMaskingProvider();
        var gatewayOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        };

        var masker = new SubgraphResultMasker(
            maskingProvider,
            Options.Create(gatewayOptions),
            NullLogger<SubgraphResultMasker>.Instance);

        var executor = await CreateExecutorAsync(maskingProvider, masker, gatewayOptions);
        var request = OperationRequestBuilder.New()
            .SetDocument("{ user { id name } }")
            .SetGlobalState(nameof(ClaimsPrincipal), CreateNonAdminUser())
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var result = executionResult as OperationResult;
        result.ShouldNotBeNull();
        (result.Errors == null || result.Errors.Count == 0).ShouldBeTrue();

        using var doc = JsonDocument.Parse(executionResult.ToJson());
        var id = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("id").GetString();
        var name = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("name").GetString();
        id.ShouldBe("123");
        name.ShouldBe("Alice");

        result.ContextData.ShouldNotBeNull();
        result.ContextData.ContainsKey("MaskingApplied").ShouldBeFalse();
    }

    [Fact]
    public async Task Executor_CatalogUnavailable_UnknownListOfScalarsMasked()
    {
        var maskingProvider = CreateMaskingProvider();
        var gatewayOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true,
                DefaultMaskUnknownSensitiveScalars = true
            }
        };

        var masker = new SubgraphResultMasker(
            maskingProvider,
            Options.Create(gatewayOptions),
            NullLogger<SubgraphResultMasker>.Instance,
            catalog: new FailingMaskingCatalog());

        var executor = await CreateExecutorAsync(maskingProvider, masker, gatewayOptions);
        var request = OperationRequestBuilder.New()
            .SetDocument("{ user { id tags labels } }")
            .SetGlobalState(nameof(ClaimsPrincipal), CreateNonAdminUser())
            .Build();

        var executionResult = await executor.ExecuteAsync(request);
        var result = executionResult as OperationResult;
        result.ShouldNotBeNull();
        (result.Errors == null || result.Errors.Count == 0).ShouldBeTrue();

        using var doc = JsonDocument.Parse(executionResult.ToJson());
        var user = doc.RootElement.GetProperty("data").GetProperty("user");
        user.GetProperty("id").GetString().ShouldBe("123");
        var tags = user.GetProperty("tags");
        tags.GetArrayLength().ShouldBe(2);
        tags[0].GetString().ShouldBe("[MASKED:tags]");
        tags[1].GetString().ShouldBe("[MASKED:tags]");

        var labels = user.GetProperty("labels");
        labels.GetArrayLength().ShouldBe(2);
        labels[0].GetString().ShouldBe("[MASKED:labels]");
        labels[1].GetString().ShouldBe("[MASKED:labels]");

        result.ContextData.ShouldNotBeNull();
        result.ContextData.ContainsKey("MaskingApplied").ShouldBeTrue();
    }

    [Fact]
    public async Task MaskResultDataAsync_CatalogUnavailable_MasksScalarListsAndJsonArrays()
    {
        var maskingProvider = new Autheris.Tests.Common.Fakes.StubColumnMaskingProvider((_, _, _) => "[REDACTED]");
        var gatewayOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true,
                DefaultMaskUnknownSensitiveScalars = true
            }
        };

        var masker = new SubgraphResultMasker(
            maskingProvider,
            Options.Create(gatewayOptions),
            NullLogger<SubgraphResultMasker>.Instance,
            catalog: new FailingMaskingCatalog());

        using var jsonDoc = JsonDocument.Parse("""["j1","j2"]""");
        var raw = new Dictionary<string, object?>
        {
            ["id"] = "1",
            ["tags"] = new List<string> { "t1", "t2" },
            ["jsonTags"] = jsonDoc.RootElement.Clone(),
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["id"] = "item-1", ["note"] = "secret-note" }
            }
        };

        var masked = await masker.MaskResultDataAsync(raw, CreateNonAdminUser(), null) as IReadOnlyDictionary<string, object?>;
        masked.ShouldNotBeNull();
        masked["id"].ShouldBe("1");

        var tags = masked["tags"].ShouldBeAssignableTo<IReadOnlyList<object?>>();
        tags.ShouldBe(["[REDACTED]", "[REDACTED]"]);

        var jsonTags = masked["jsonTags"].ShouldBeOfType<JsonElement>();
        jsonTags.ValueKind.ShouldBe(JsonValueKind.Array);
        jsonTags[0].GetString().ShouldBe("[REDACTED]");
        jsonTags[1].GetString().ShouldBe("[REDACTED]");

        var items = masked["items"].ShouldBeAssignableTo<IReadOnlyList<object?>>();
        items.ShouldNotBeNull();
        var firstItem = items[0].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>();
        firstItem.ShouldNotBeNull();
        firstItem["id"].ShouldBe("item-1");
        firstItem["note"].ShouldBe("[REDACTED]");
    }

    private sealed class FailingMaskingCatalog : IFederatedMaskingCatalog
    {
        public ValueTask<IReadOnlyDictionary<string, MaskingRule>> GetClassificationsAsync(TenantId tenant, CancellationToken ct = default)
            => ValueTask.FromException<IReadOnlyDictionary<string, MaskingRule>>(new InvalidOperationException("catalog unavailable"));
    }
}
