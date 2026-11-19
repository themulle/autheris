using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Role grantees were removed from the consent model (step 0029): the enum has no role member and no production code outside the
/// historic migration steps and the frozen baseline names the dropped columns or the retired member.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class RoleGranteeRemovalRatchetTests
{
    private const string Migrations = "/Persistence/Migrations/";

    [GeneratedRegex(@"enum\s+GranteeType\s*\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex GranteeTypeEnum();

    [GeneratedRegex(@"\b(role_id|role_name)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DroppedColumns();

    [GeneratedRegex(@"GranteeType\.Role\b", RegexOptions.CultureInvariant)]
    private static partial Regex RetiredMember();

    [Fact]
    public void Architecture_GranteeType_HasNoRoleMember()
    {
        var model = RepositoryScan.ProductionFiles.Single(f => f.RelativePath.EndsWith("Autheris.Domain/Model/TableModels.cs", StringComparison.Ordinal));
        var body = GranteeTypeEnum().Match(model.Text).Groups["body"].Value;
        body.ShouldNotBeNullOrWhiteSpace();

        var names = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.Split('=')[0].Trim())
            .ToList();
        names.ShouldBe(["User", "Group", "ServicePrincipal"]);

        // The persisted values are never renumbered: the retired value 3 stays unassigned.
        body.ShouldContain("ServicePrincipal = 4");
        body.ShouldNotContain("= 3");
    }

    [Fact]
    public void ProductionCode_NamesNeitherTheDroppedRoleColumnsNorTheRetiredMember()
    {
        var offenders = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (file.RelativePath.Contains(Migrations, StringComparison.Ordinal))
            {
                continue;
            }

            if (DroppedColumns().IsMatch(file.Text))
            {
                offenders.Add(file.RelativePath + " (role_id / role_name)");
            }

            if (RetiredMember().IsMatch(file.Text))
            {
                offenders.Add(file.RelativePath + " (GranteeType.Role)");
            }
        }

        offenders.ShouldBeEmpty("Role grantees were removed by step 0029; historic migration steps are the only place that names them.");
    }
}
