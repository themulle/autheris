using System.Reflection;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-ARC-C-01: Guards the physical split of <see cref="GatewayOptions"/> and ratchets the number of
/// production classes injecting <c>IOptions&lt;GatewayOptions&gt;</c> / <c>IOptionsMonitor&lt;GatewayOptions&gt;</c>.
/// </summary>
public sealed class GatewayOptionsConsumptionRatchetTests
{
    private const int MaxGatewayOptionsFileLines = 300;
    private const int MaxOptionsWrapperConsumerTypes = 210;

    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(GatewayOptions).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly,
        typeof(Program).Assembly
    ];

    [Fact]
    public void GatewayOptions_SourceFile_StaysAtOrBelow300Lines()
    {
        var file = FindGatewayOptionsFile();
        var lineCount = File.ReadAllLines(file).Length;

        lineCount.ShouldBeLessThanOrEqualTo(
            MaxGatewayOptionsFileLines,
            $"GatewayOptions.cs grew to {lineCount} lines (ratchet ceiling: {MaxGatewayOptionsFileLines}). Extract nested/sibling option classes into dedicated files under src/Autheris.Domain/Options/<Area>/.");
    }

    [Fact]
    public void GatewaySections_ConstantsStartWithRootSection()
    {
        GatewaySections.Root.ShouldBe("Gateway");

        var fields = typeof(GatewaySections)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToList();

        fields.Count.ShouldBeGreaterThanOrEqualTo(40);
        foreach (var field in fields)
        {
            var value = (string?)field.GetRawConstantValue();
            value.ShouldNotBeNullOrWhiteSpace();
            (value == GatewaySections.Root || value.StartsWith(GatewaySections.Root + ":", StringComparison.Ordinal))
                .ShouldBeTrue($"GatewaySections.{field.Name} ('{value}') must be '{GatewaySections.Root}' or start with '{GatewaySections.Root}:'.");
        }
    }

    [Fact]
    public void ProductionTypes_InjectingGatewayOptionsWrapper_DoNotExceedRatchet()
    {
        var wrapperTypes = new HashSet<Type>
        {
            typeof(IOptions<GatewayOptions>),
            typeof(IOptionsMonitor<GatewayOptions>),
            typeof(IOptionsSnapshot<GatewayOptions>)
        };

        var consumers = new List<string>();
        foreach (var assembly in ProductionAssemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass || type.IsAbstract || typeof(IValidateOptions<GatewayOptions>).IsAssignableFrom(type))
                {
                    continue;
                }

                var injectsGatewayOptions = type
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SelectMany(c => c.GetParameters())
                    .Any(p => wrapperTypes.Contains(p.ParameterType));

                if (injectsGatewayOptions)
                {
                    consumers.Add(type.FullName ?? type.Name);
                }
            }
        }

        consumers.Count.ShouldBeLessThanOrEqualTo(
            MaxOptionsWrapperConsumerTypes,
            $"The number of production classes injecting IOptions<GatewayOptions>/IOptionsMonitor<GatewayOptions> ({consumers.Count}) exceeded the ratchet ceiling ({MaxOptionsWrapperConsumerTypes}). Inject narrow section options instead.");
    }

    private static string FindGatewayOptionsFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Autheris.Domain", "Options", "GatewayOptions.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Autheris.Domain/Options/GatewayOptions.cs from " + AppContext.BaseDirectory);
    }
}
