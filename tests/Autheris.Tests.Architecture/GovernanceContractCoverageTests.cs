using System.Reflection;
using System.Text.RegularExpressions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Attributes;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Architecture fitness tests enforcing that every governance repository interface registered in
/// <see cref="GatewayGovernanceServiceExtensions"/> is covered by an abstract <c>*Contract</c> suite
/// with both <c>PostgreSql*ContractTests</c> and <c>SqlServer*ContractTests</c> subclasses, and that
/// no contract test is skipped via <c>Skip =</c>.
/// </summary>
public sealed class GovernanceContractCoverageTests
{
    private const string ContractDirectory = "tests/Autheris.Tests.Integration/Governance/Contract";
    private const string AllowlistFileName = "governance-contract-allowlist.txt";

    private static readonly Regex AbstractContractClassRegex = new(
        @"public\s+abstract\s+class\s+(?<name>\w+Contract)\s*:\s*GovernanceContractBase\b",
        RegexOptions.Compiled);

    private static readonly Regex PostgreSqlSubclassRegex = new(
        @"public\s+sealed\s+class\s+PostgreSql(?<name>\w+Contract)Tests\b[^{]*:\s*\k<name>\b",
        RegexOptions.Compiled);

    private static readonly Regex SqlServerSubclassRegex = new(
        @"public\s+sealed\s+class\s+SqlServer(?<name>\w+Contract)Tests\b[^{]*:\s*\k<name>\b",
        RegexOptions.Compiled);

    private static readonly Regex InterfaceDocTagRegex = new(
        @"<c>(?<iface>I[A-Z]\w+)</c>",
        RegexOptions.Compiled);

    private static readonly Regex SkipAttributePropertyRegex = new(
        @"\bSkip\s*=",
        RegexOptions.Compiled);

    private static readonly string[] RequiredCrossCuttingMethods =
    [
        "Reads_never_return_other_tenant_rows",
        "Writes_cannot_modify_other_tenant_rows",
        "Missing_or_unparseable_tenant_id_throws",
        "Upsert_is_idempotent",
        "Unique_violation_maps_to_conflict_exception",
        "Paging_is_stable_and_ordered",
        "Page_size_above_cap_is_clamped_or_rejected",
        "Soft_deleted_rows_are_invisible_to_reads"
    ];

    [Fact]
    public void Every_governance_repository_interface_has_contract_on_both_engines()
    {
        var registeredInterfaces = DiscoverRegisteredGovernanceInterfaces();
        Assert.NotEmpty(registeredInterfaces);

        var allowlist = RepositoryScan.ReadBaseline(AllowlistFileName)
            .ToHashSet(StringComparer.Ordinal);

        var unknownAllowlisted = allowlist.Except(registeredInterfaces, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(
            unknownAllowlisted.Count == 0,
            $"Every entry in {AllowlistFileName} must correspond to a governance repository interface registered in GatewayGovernanceServiceExtensions. Unknown: {string.Join(", ", unknownAllowlisted)}");

        var contractFiles = RepositoryScan.EnumerateFiles(ContractDirectory, "*.cs");
        var coveredInterfaces = new HashSet<string>(StringComparer.Ordinal);
        var discoveredContracts = new List<string>();

        foreach (var file in contractFiles)
        {
            if (file.EndsWith("/GovernanceContractBase.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var text = RepositoryScan.ReadText(file);
            var abstractMatch = AbstractContractClassRegex.Match(text);
            Assert.True(
                abstractMatch.Success,
                $"{file} must declare a 'public abstract class <Name>Contract : GovernanceContractBase'");

            var contractName = abstractMatch.Groups["name"].Value;
            discoveredContracts.Add(contractName);

            var hasPg = PostgreSqlSubclassRegex.Matches(text)
                .Cast<Match>()
                .Any(m => string.Equals(m.Groups["name"].Value, contractName, StringComparison.Ordinal));
            Assert.True(hasPg, $"{file} must declare 'public sealed class PostgreSql{contractName}Tests : {contractName}'");

            var hasSqlServer = SqlServerSubclassRegex.Matches(text)
                .Cast<Match>()
                .Any(m => string.Equals(m.Groups["name"].Value, contractName, StringComparison.Ordinal));
            Assert.True(hasSqlServer, $"{file} must declare 'public sealed class SqlServer{contractName}Tests : {contractName}'");

            foreach (var requiredMethod in RequiredCrossCuttingMethods)
            {
                Assert.True(
                    text.Contains(requiredMethod, StringComparison.Ordinal),
                    $"{contractName} ({file}) must implement the cross-cutting contract test '{requiredMethod}'");
            }

            foreach (Match m in InterfaceDocTagRegex.Matches(text))
            {
                var iface = m.Groups["iface"].Value;
                if (registeredInterfaces.Contains(iface))
                {
                    coveredInterfaces.Add(iface);
                }
            }
        }

        Assert.NotEmpty(discoveredContracts);

        var staleAllowlisted = allowlist.Intersect(coveredInterfaces, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(
            staleAllowlisted.Count == 0,
            $"Interfaces already covered by a contract class must be removed from the shrink-only baseline {AllowlistFileName}: {string.Join(", ", staleAllowlisted)}");

        var uncovered = registeredInterfaces
            .Except(coveredInterfaces, StringComparer.Ordinal)
            .Except(allowlist, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            $"Every governance repository interface not in {AllowlistFileName} must be covered by an abstract *Contract class with PostgreSql and SqlServer subclasses under {ContractDirectory}. Uncovered: {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void No_skipped_contract_tests()
    {
        var contractFiles = RepositoryScan.EnumerateFiles(ContractDirectory, "*.cs");
        var violations = new List<string>();

        foreach (var file in contractFiles)
        {
            var lines = RepositoryScan.ReadText(file).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (RepositoryScan.IsCommentLine(trimmed))
                {
                    continue;
                }

                if (SkipAttributePropertyRegex.IsMatch(trimmed))
                {
                    violations.Add($"{file}:{i + 1}: {trimmed}");
                }
            }
        }

        Assert.True(violations.Count == 0, $"Contract tests under {ContractDirectory} must never use Skip =: {string.Join("; ", violations)}");
    }

    private static HashSet<string> DiscoverRegisteredGovernanceInterfaces()
    {
        var method = typeof(GatewayGovernanceServiceExtensions).GetMethod(
            "RegisterGovernanceAggregateStores",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var genericParam in method!.GetGenericArguments())
        {
            foreach (var constraint in genericParam.GetGenericParameterConstraints())
            {
                if (constraint.IsInterface && !IsExcludedInterface(constraint))
                {
                    result.Add(constraint.Name);
                }
            }
        }

        foreach (var inherited in typeof(IGovernanceRepository).GetInterfaces())
        {
            if (!IsExcludedInterface(inherited))
            {
                result.Add(inherited.Name);
            }
        }

        var closed = method.MakeGenericMethod(
            typeof(PostgreSqlGovernanceRepository),
            typeof(PostgreSqlGovernanceMigrationRunner));
        var services = new ServiceCollection();
        closed.Invoke(null, [services, true]);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType.IsInterface && !IsExcludedInterface(descriptor.ServiceType))
            {
                result.Add(descriptor.ServiceType.Name);
            }
        }

        return result;
    }

    private static bool IsExcludedInterface(Type type) =>
        type == typeof(IDisposable) ||
        type == typeof(IAsyncDisposable) ||
        type == typeof(IConsentMaterializationGuard);
}
