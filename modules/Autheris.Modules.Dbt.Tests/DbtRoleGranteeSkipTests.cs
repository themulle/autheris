namespace Autheris.Modules.Dbt.Tests;

using System;
using System.IO;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Application.VirtualFilters;
using Autheris.Modules.Dbt;
using Shouldly;
using Xunit;

public sealed class DbtRoleGranteeSkipTests
{
    private static readonly DbtGovernanceImportCaller Caller =
        new(new VirtualFilterActor(new Sid("S-1-5-21-LWE-GOVADMIN"), IsSync: false), new TenantId("postgres"));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("role")]
    [InlineData("Role")]
    [InlineData("tenant_role")]
    public async Task Security_DbtAndOpenMetadata_RoleGrantee_SkippedNeverMapped_Dbt(string granteeType)
    {
        var (service, _) = DbtTests.GovernanceService();

        var json = """
            {"access_profiles":{"analysts":{"scope":"lwetem_prod.*.*","uncovered":"deny",
              "grantee":{"type":"__TYPE__","sid":"S-1-5-21-ANALYSTS","role":"Analyst"},
              "bindings":[{"filter":"nicht_ausgelieferte_krane","target":"lwetem_prod.*.*.client_id"}]}}}
            """.Replace("__TYPE__", granteeType, StringComparison.Ordinal);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: true);

        result.Success.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("grantee type is not supported", StringComparison.Ordinal) && w.Contains("skipped", StringComparison.Ordinal));
    }
}
