namespace Autheris.Tests.Unit.Catalog;

using System.Data.Common;
using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DatasourceTestingServiceSqlTests
{
    private const string Source = "gateway_lwe";

    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly ISqlConnectionFactory _factory = Substitute.For<ISqlConnectionFactory>();

    private static ClaimsPrincipal Admin() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim("tenant_id", "t1") }, "Test"));

    private DatasourceTestingService CreateService(bool configured = true)
    {
        var table = new Table { SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql };
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new TableMetadata { Identifier = new TableIdentifier(Source, "dbo", "orders"), Table = table, Columns = [], PrimaryKeyColumns = [] }
        });
        var gateway = new GatewayOptions { DataSources = new SqlDataSourceOptions() };
        if (configured)
        {
            gateway.DataSources.Connections[Source] = new DataSourceConnectionOptions
            {
                Provider = "SqlServer",
                ConnectionString = "Server=10.160.2.94,1433;Database=Secret_DB;Integrated Security=true;",
                WindowsAuth = new SqlServerWindowsAuthOptions { Mode = SqlServerWindowsAuthMode.Ntlm, UseManagedNtlm = true }
            };
        }

        return new DatasourceTestingService(
            _metadataRepo, Substitute.For<IHttpClientFactory>(), _auditRepo, null, null, null,
            _factory, Options.Create(gateway), new SqlSourceDescriber());
    }

    [Fact]
    public async Task OpenSucceeds_ReportsRealHostPortAndAuthPath()
    {
        _factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<DbConnection>(new SqliteConnection()));

        var result = await CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), Admin());

        result.IsSuccess.ShouldBeTrue();
        result.Diagnostics.TargetHost.ShouldBe("10.160.2.94");
        result.Diagnostics.TargetPort.ShouldBe(1433);
        result.Diagnostics.TlsProtocolVersion.ShouldBeNull();
        result.Diagnostics.ProbeDetails["Dialect"].ShouldBe("SqlServer");
        result.Diagnostics.ProbeDetails["AuthPath"].ShouldBe("Integrated-ManagedNtlm");
        result.Diagnostics.TargetHost.ShouldNotBe("sql-engine");
    }

    [Fact]
    public async Task OpenFails_ReturnsClassifiedCategoryAndNoSecrets()
    {
        var failure = new SqlServerWindowsAuthException(
            SqlServerWindowsAuthFailureCategory.NtlmNegotiation,
            "SQL Server Windows Authentication (managed NTLM) failed during NTLM negotiation (step 'challenge').",
            new InvalidOperationException("Password=hunter2"),
            ntlmStatus: "InvalidToken", ntlmStep: "challenge");
        _factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbConnection>>(_ => throw failure);

        var result = await CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), Admin());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("managed NTLM");
        result.ErrorMessage.ShouldNotContain("hunter2");
        result.Diagnostics.ProbeDetails["Category"].ShouldBe("NtlmNegotiation");
        result.Diagnostics.ProbeDetails["NtlmStatus"].ShouldBe("InvalidToken");
        result.Diagnostics.TargetHost.ShouldBe("10.160.2.94");
        await _auditRepo.Received(1).RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenFailsWithUnclassifiedError_ReportsConnectionFailedAndExceptionType()
    {
        _factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbConnection>>(_ => throw new TimeoutException("boom"));

        var result = await CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), Admin());

        result.IsSuccess.ShouldBeFalse();
        result.Diagnostics.ProbeDetails["Category"].ShouldBe("ConnectionFailed");
        result.Diagnostics.ProbeDetails["Exception"].ShouldBe("TimeoutException");
    }

    [Fact]
    public async Task NoConfiguredConnection_FailsWithoutFabricatedHost()
    {
        var result = await CreateService(configured: false).TestDatasourceAsync(Source, new DatasourceTestRequest(), Admin());

        result.IsSuccess.ShouldBeFalse();
        result.Diagnostics.TargetHost.ShouldBeEmpty();
        result.Diagnostics.ProbeDetails["Category"].ShouldBe("NotConfigured");
        await _factory.DidNotReceive().CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>());
    }
}
