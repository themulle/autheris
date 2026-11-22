namespace Autheris.Tests.Unit.DependencyInjection;

using System;
using System.Reflection;
using Autheris.Application.DataSources;
using Autheris.Application.OData;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Tenancy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RequiredConstructorCompositionTests
{
    [Fact]
    public void ODataHandler_PublicConstructor_IsSingleNonOptionalActivatorConstructor()
    {
        var ctors = typeof(ODataHandler).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var ctor = ctors.ShouldHaveSingleItem();
        ctor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false).ShouldBeTrue();

        var nullability = new NullabilityInfoContext();
        foreach (var param in ctor.GetParameters())
        {
            param.IsOptional.ShouldBeFalse($"Parameter '{param.Name}' must be required.");
            param.HasDefaultValue.ShouldBeFalse($"Parameter '{param.Name}' must not have a default value.");
            nullability.Create(param).ReadState.ShouldBe(
                NullabilityState.NotNull,
                $"Parameter '{param.Name}' must be non-nullable.");
        }
    }

    [Fact]
    public void StreamRlsPolicyEnforcer_PublicConstructor_IsSingleNonOptionalActivatorConstructor()
    {
        var ctors = typeof(StreamRlsPolicyEnforcer).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var ctor = ctors.ShouldHaveSingleItem();
        ctor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false).ShouldBeTrue();

        var nullability = new NullabilityInfoContext();
        foreach (var param in ctor.GetParameters())
        {
            param.IsOptional.ShouldBeFalse($"Parameter '{param.Name}' must be required.");
            param.HasDefaultValue.ShouldBeFalse($"Parameter '{param.Name}' must not have a default value.");
            nullability.Create(param).ReadState.ShouldBe(
                NullabilityState.NotNull,
                $"Parameter '{param.Name}' must be non-nullable.");
        }
    }

    [Fact]
    public void ODataHandler_Constructor_ThrowsArgumentNullException_WhenAnyDependencyIsNull()
    {
        var ctor = typeof(ODataHandler).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldHaveSingleItem();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var tenantResolver = new TenantResolver(() => new TenancyOptions(), null, NullLogger<TenantResolver>.Instance);
        object[] ValidArgs() =>
        [
            new InMemoryTableMetadataRepository(),
            new StubGatewayExecutionService(),
            NullLogger<ODataHandler>.Instance,
            new TestHostEnvironment(Environments.Production),
            memoryCache,
            StaticCatalogVersionProvider.Instance,
            tenantResolver,
            PermissiveDataSourceOptionsResolver.Instance,
            new IngressTransportContext(),
            DenyAllODataQueryPolicyEnforcer.Instance
        ];

        var parameters = ctor.GetParameters();
        parameters.Length.ShouldBe(10);

        for (var i = 0; i < parameters.Length; i++)
        {
            var args = ValidArgs();
            args[i] = null!;
            var ex = Should.Throw<TargetInvocationException>(() => ctor.Invoke(args));
            var argNull = ex.InnerException.ShouldBeOfType<ArgumentNullException>();
            argNull.ParamName.ShouldBe(parameters[i].Name);
        }
    }

    [Fact]
    public void StreamRlsPolicyEnforcer_Constructor_ThrowsArgumentNullException_WhenAnyDependencyIsNull()
    {
        var ctor = typeof(StreamRlsPolicyEnforcer).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldHaveSingleItem();
        var options = Options.Create(new GatewayOptions());
        var consentRepo = new InMemoryConsentRepository();
        var resolution = new ConsentResolutionService();
        var pdp = new AccessDecisionPoint(options, resolution, consentRepo);
        object[] ValidArgs() =>
        [
            pdp,
            new InMemoryTableMetadataRepository(),
            StubColumnMaskingProvider.Null,
            new FakeEpochValidationService(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            consentRepo,
            resolution,
            new InMemoryConsentCacheService(),
            NullClientIpResolver.Instance,
            options,
            NullMandatoryRowFilterResolver.Instance,
            DisabledCallerAttributeResolver.Instance,
            TimeProvider.System,
            AllowAllTransportGate.Instance
        ];

        var parameters = ctor.GetParameters();
        parameters.Length.ShouldBe(14);

        for (var i = 0; i < parameters.Length; i++)
        {
            var args = ValidArgs();
            args[i] = null!;
            var ex = Should.Throw<TargetInvocationException>(() => ctor.Invoke(args));
            var argNull = ex.InnerException.ShouldBeOfType<ArgumentNullException>();
            argNull.ParamName.ShouldBe(parameters[i].Name);
        }

        // Also reject IOptions<GatewayOptions> whose Value is null.
        var nullOptArgs = ValidArgs();
        var optionsIndex = Array.FindIndex(parameters, p => p.ParameterType == typeof(IOptions<GatewayOptions>));
        nullOptArgs[optionsIndex] = new NullValueGatewayOptions();
        var optEx = Should.Throw<TargetInvocationException>(() => ctor.Invoke(nullOptArgs));
        optEx.InnerException.ShouldBeOfType<ArgumentNullException>().ParamName.ShouldBe("options");
    }

    private sealed class NullValueGatewayOptions : IOptions<GatewayOptions>
    {
        public GatewayOptions Value => null!;
    }
}
