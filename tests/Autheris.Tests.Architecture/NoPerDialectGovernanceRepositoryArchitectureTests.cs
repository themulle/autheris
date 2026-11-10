namespace Autheris.Tests.Architecture;

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

public sealed class NoPerDialectGovernanceRepositoryArchitectureTests
{
    [Fact]
    [Trait("Category", "Architecture")]
    public void No_dialect_prefixed_governance_repository_types()
    {
        var infraAssembly = typeof(GovernanceRepository).Assembly;
        var dialectRepoPattern = new Regex(@"^(PostgreSql|SqlServer|Sqlite)GovernanceRepository", RegexOptions.Compiled);

        var violatingTypes = infraAssembly.GetTypes()
            .Where(t => dialectRepoPattern.IsMatch(t.Name))
            .Select(t => t.FullName)
            .ToList();

        violatingTypes.ShouldBeEmpty("WP-SIMPL-20d: All per-dialect governance repositories must be removed in favor of the unified GovernanceRepository.");
    }
}
