using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Procedures;

public partial class ProcedureExecutionSecurityTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    [Trait("Category", "Security")]
    public void GatewayStorageOptionsValidator_RejectsOutOfRangeTableReadWait(int waitMs, bool valid)
    {
        Microsoft.Extensions.Hosting.IHostEnvironment env = new TestHostEnvironment("Production");
        var validator = new Autheris.Api.Configuration.Validation.GatewayStorageOptionsValidator(env);

        var result = validator.Validate(null, new GatewayOptions { DataSources = new SqlDataSourceOptions { TableReadWaitTimeoutMs = waitMs } });

        result.Succeeded.ShouldBe(valid);
    }
}
