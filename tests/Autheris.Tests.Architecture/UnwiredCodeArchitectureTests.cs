namespace Autheris.Tests.Architecture;

using System;
using System.Linq;
using System.Reflection;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Autheris.Application.State.Caching.TableCache.Governance;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UnwiredCodeArchitectureTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(Autheris.Domain.Options.GatewayOptions).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.GovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly,
        typeof(Program).Assembly
    ];

    [Fact]
    public void No_unauthenticated_mcp_session_factory()
    {
        Type[] sessionFactoryTypes =
        [
            typeof(IMcpProtocolHandler),
            typeof(McpProtocolHandler),
            typeof(IMcpSessionStore),
            typeof(McpSessionStore)
        ];

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in sessionFactoryTypes)
        {
            var factories = type.GetMethods(Flags)
                .Where(m => m.ReturnType == typeof(McpSessionContext) &&
                            !m.IsSpecialName &&
                            m.Name.StartsWith("Create", StringComparison.Ordinal))
                .ToList();

            factories.ShouldNotBeEmpty($"Expected {type.Name} to declare a session factory returning McpSessionContext.");

            foreach (var factory in factories)
            {
                var parameters = factory.GetParameters();
                var isTwoStringSignature =
                    parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(string);
                isTwoStringSignature.ShouldBeFalse(
                    $"{type.Name}.{factory.Name} must not expose a two-string unauthenticated session factory.");

                var requiredParamCount = parameters.Count(p => !p.IsOptional);
                requiredParamCount.ShouldBeGreaterThanOrEqualTo(
                    3,
                    $"{type.Name}.{factory.Name} must require explicit caller identity parameters alongside service principal and tenant.");

                var userSidParam = parameters.FirstOrDefault(p => string.Equals(p.Name, "userSid", StringComparison.Ordinal));
                if (userSidParam is not null)
                {
                    userSidParam.IsOptional.ShouldBeFalse(
                        $"{type.Name}.{factory.Name} must not default userSid so callers cannot omit caller identity.");
                }
            }
        }

        // Also ensure no other public or internal method in Application/Api returns McpSessionContext from (string, string).
        Assembly[] appAndApi =
        [
            typeof(McpProtocolHandler).Assembly,
            typeof(Program).Assembly
        ];

        foreach (var assembly in appAndApi)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(Flags))
                {
                    if (method.IsSpecialName || method.ReturnType != typeof(McpSessionContext))
                    {
                        continue;
                    }

                    if (!method.IsPublic && !method.IsAssembly && !method.IsFamilyOrAssembly)
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    var isTwoStringSignature =
                        parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType == typeof(string);
                    isTwoStringSignature.ShouldBeFalse(
                        $"{type.FullName}.{method.Name}(string, string) creates an MCP session without caller identity.");
                }
            }
        }
    }

    [Theory]
    [InlineData("McpStdioRunner")]
    [InlineData("IMcpStdioRunner")]
    [InlineData("TableCacheAdminEndpoints")]
    public void Unwired_types_do_not_exist_in_production_assemblies(string typeName)
    {
        foreach (var assembly in ProductionAssemblies)
        {
            var found = assembly.GetTypes().Any(t =>
                string.Equals(t.Name, typeName, StringComparison.Ordinal) ||
                t.FullName?.EndsWith("." + typeName, StringComparison.Ordinal) == true);
            found.ShouldBeFalse($"Unwired type '{typeName}' must not exist in {assembly.GetName().Name}.");
        }
    }

    [Fact]
    public void Governed_table_cache_execution_service_is_retained()
    {
        typeof(GovernedTableCacheExecutionService).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(typeof(IDbtMetadataIngestionService), "IngestManifestFileAsync")]
    [InlineData(typeof(DbtMetadataIngestionService), "IngestManifestFileAsync")]
    [InlineData(typeof(IDbtMetadataIngestionService), "IngestGovernanceFileAsync")]
    [InlineData(typeof(DbtMetadataIngestionService), "IngestGovernanceFileAsync")]
    [InlineData(typeof(DbtMetadataIngestionService), "ValidateSafeFilePath")]
    [InlineData(typeof(IDbtExposurePublisher), "ExportExposuresFileAsync")]
    [InlineData(typeof(DbtExposurePublisher), "ExportExposuresFileAsync")]
    [InlineData(typeof(DbtExposurePublisher), "ValidateSafeFilePath")]
    [InlineData(typeof(IDbtContractValidator), "ValidateContractsFileAsync")]
    [InlineData(typeof(DbtContractValidator), "ValidateContractsFileAsync")]
    [InlineData(typeof(DbtContractValidator), "ValidateSafeFilePath")]
    public void Unwired_dbt_file_helpers_and_prefix_denylists_are_removed(Type targetType, string methodName)
    {
        const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        targetType.GetMethods(AllFlags)
            .Any(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ShouldBeFalse($"{targetType.Name}.{methodName} should have been removed.");
    }
}
