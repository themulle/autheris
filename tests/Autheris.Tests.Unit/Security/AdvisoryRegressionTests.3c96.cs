namespace Autheris.Tests.Unit.Security;

using System.Threading.Tasks;
using Autheris.Tests.Unit.Dbt;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    [Theory]
    [InlineData("reimport_keeps_stricter_table_governance")]
    [InlineData("reimport_does_not_reactivate")]
    [InlineData("reimport_keeps_masking_rules")]
    [InlineData("reimport_keeps_column_governance")]
    [InlineData("new_table_imported_unchanged")]
    public Task Ghsa_jw88_3c96_regression(string bullet) => SemanticImportGovernanceHelper.AssertAsync(bullet);
}
