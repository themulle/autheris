namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// SQL-4: with the AST compiler (the only path since X1) every query on an HMAC-masked column failed, because the gateway's
/// pre-rendered mask expression (dialect SQL with bound key parameters) was parsed again as Trino SQL.
/// </summary>
public sealed class AstCompilerHmacSql4Tests(ITestOutputHelper output)
{
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AST"), new Claim("tenant_id", Tenant)], "Test"));

    private static GovernedSqlExecutionService CreateService(string sourceType)
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "employees"),
            Table = new Table { TableName = "employees", SchemaName = "public", SourceType = sourceType },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = true },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["email"] = new MaskingRule { RuleType = "HMAC" } }
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(table.Identifier.Equals(ci.Arg<TableIdentifier>()) ? table : null));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));

        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("super-secret-hmac-value-0123456789"));
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "GQL-HMAC", HmacKeyId = "k1" }
        });
        return new GovernedSqlExecutionService(
            gatewayOptions, executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(gatewayOptions, secrets),
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: secrets);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public async Task AstCompiler_HmacMaskedColumn_IsRewritten(string sourceType)
    {
        var service = CreateService(sourceType);

        var compiled = await service.CompileAsync("SELECT id, email FROM employees", User(), new TenantId(Tenant));
        output.WriteLine(compiled.Sql);

        // X1: the key and the tenant are bound parameters of the compiled statement, never text.
        compiled.Sql.ShouldNotContain("super-secret");
        compiled.Sql.ShouldNotContain("tenant_a");
        compiled.ShouldBindTenant("tenant_a");
        var maskBinds = compiled.Parameters.Where(p => p.Origin == TrinoSqlEngine.Ast.Emit.ParameterOrigin.Mask).ToList();
        maskBinds.ShouldNotBeEmpty();
        // The HMAC key is the HKDF-derived per-tenant key, not the master secret.
        maskBinds.Any(p => p.Value is byte[] raw && Encoding.UTF8.GetString(raw).Contains("super-secret")).ShouldBeFalse();
        maskBinds.Any(p => p.Value is string text && text.Contains("super-secret")).ShouldBeFalse();
    }
}
