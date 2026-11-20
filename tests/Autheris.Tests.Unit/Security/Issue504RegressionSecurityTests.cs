namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Api.Security;
using Autheris.Application.Analytics;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Runtime.Jobs.Interfaces;
using Autheris.Application.Runtime.Jobs.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Modules.Dbt;
using Autheris.Tests.Common;
using HotChocolate.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GraphQlBatchingSecurityTests
{
    [Fact]
    public void Batched_request_is_rejected()
    {
        // Positive control: HotChocolate's out-of-the-box server options do not default to AllowedBatching.None.
        var defaultServerOptions = new GraphQLServerOptions();
        defaultServerOptions.Batching.ShouldNotBe(AllowedBatching.None);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayGraphQL(new GatewayOptions());

        using var provider = services.BuildServiceProvider();
        var namedConfigurers = provider.GetServices<IConfigureOptions<GraphQLServerOptions>>()
            .OfType<ConfigureNamedOptions<GraphQLServerOptions>>()
            .ToList();
        namedConfigurers.ShouldNotBeEmpty();

        var monitor = provider.GetRequiredService<IOptionsMonitor<GraphQLServerOptions>>();
        var schemaName = namedConfigurers[0].Name;
        var configured = monitor.Get(schemaName);

        configured.Batching.ShouldBe(AllowedBatching.None);
    }
}

[Trait("Category", "Security")]
public sealed class GatewayStartupValidatorTests
{
    [Fact]
    public void Rejects_empty_entra_client_allowlist()
    {
        var env = new FakeHostEnvironment(Environments.Production);

        var invalidOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EnableTestAuthHandler = false,
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "11111111-2222-3333-4444-555555555555",
                    ClientId = "api://autheris-gateway",
                    AllowedClientIds = []
                }
            }
        };

        var invalidCtx = new StartupCheckContext(invalidOptions, env, _ => null, NullLogger.Instance, null);
        var ex = Should.Throw<ValidationException>(() =>
            AuthenticationStartupChecks.ProductionAuthentication(invalidCtx));
        ex.Message.ShouldContain("AllowedClientIds");

        // Positive control: populated AllowedClientIds passes production authentication check.
        var validOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EnableTestAuthHandler = false,
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "11111111-2222-3333-4444-555555555555",
                    ClientId = "api://autheris-gateway",
                    AllowedClientIds = ["allowed-client-id"]
                }
            }
        };
        var validCtx = new StartupCheckContext(validOptions, env, _ => null, NullLogger.Instance, null);
        Should.NotThrow(() => AuthenticationStartupChecks.ProductionAuthentication(validCtx));
    }

    [Fact]
    public void Rejects_appid_only_v1_token()
    {
        var options = new EntraIdAuthOptions
        {
            Enabled = true,
            TenantId = "tid-1",
            ClientId = "api-client",
            AllowedClientIds = ["allowed-v1-app"]
        };
        var resolver = new FakeIdentitySubjectResolver();

        var unlistedV1Principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("appid", "unlisted-v1-app"),
            new Claim("scp", "user_impersonation")
        ], "Bearer"));

        var error = EntraTokenPolicy.Apply(unlistedV1Principal, options, resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("unlisted-v1-app");
        error.ShouldContain("not in the list of allowed clients");

        // Positive control: listed v1 appid token with scp is accepted.
        var allowedV1Principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("appid", "allowed-v1-app"),
            new Claim("scp", "user_impersonation")
        ], "Bearer"));

        EntraTokenPolicy.Apply(allowedV1Principal, options, resolver).ShouldBeNull();
    }

    [Fact]
    public void Kerberos_branch_fails_without_keytab()
    {
        var env = new FakeHostEnvironment(Environments.Production);
        var tempDir = Path.Combine(Path.GetTempPath(), "autheris-krb5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keytabA = Path.Combine(tempDir, "a.keytab");
            var keytabB = Path.Combine(tempDir, "b.keytab");
            var krb5Conf = Path.Combine(tempDir, "krb5.conf");
            File.WriteAllText(keytabA, "dummy-keytab-a");
            File.WriteAllText(keytabB, "dummy-keytab-b");
            File.WriteAllText(krb5Conf, "[libdefaults]\n default_realm = CORP.EXAMPLE\n");

            static DataSourceConnectionOptions KrbSource(string? keytab, string? config = null) => new()
            {
                Provider = "SqlServer",
                ConnectionString = "Server=sql1;Database=db;Integrated Security=True;Encrypt=True;",
                WindowsAuth = new SqlServerWindowsAuthOptions
                {
                    Mode = SqlServerWindowsAuthMode.Kerberos,
                    KerberosKeytabPath = keytab,
                    KerberosConfigPath = config
                }
            };

            // 1. Missing keytab file
            var missingKeytab = new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["sql1"] = KrbSource(Path.Combine(tempDir, "nonexistent.keytab"))
                    }
                }
            };
            Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateSqlServerWindowsAuth(missingKeytab, env))
                .Message.ShouldContain("KerberosKeytabPath");

            // 2. Missing krb5.conf file when KerberosConfigPath is set
            var missingConfig = new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["sql1"] = KrbSource(keytabA, Path.Combine(tempDir, "nonexistent-krb5.conf"))
                    }
                }
            };
            Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateSqlServerWindowsAuth(missingConfig, env))
                .Message.ShouldContain("KerberosConfigPath");

            // 3. Mixing Kerberos and native NTLM across SQL Server sources
            var mixedModes = new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["sql1"] = KrbSource(keytabA),
                        ["sql2"] = new DataSourceConnectionOptions
                        {
                            Provider = "SqlServer",
                            ConnectionString = "Server=sql2;Database=db;Integrated Security=True;Encrypt=True;",
                            WindowsAuth = new SqlServerWindowsAuthOptions
                            {
                                Mode = SqlServerWindowsAuthMode.Ntlm,
                                UseManagedNtlm = false,
                                Domain = "CORP",
                                Username = "svc"
                            }
                        }
                    }
                }
            };
            Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateSqlServerWindowsAuth(mixedModes, env))
                .Message.ShouldContain("cannot mix Kerberos and native NTLM");

            // 4. Conflicting KerberosKeytabPath across two Kerberos SQL Server sources
            var conflictingKeytabs = new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["sql1"] = KrbSource(keytabA),
                        ["sql2"] = KrbSource(keytabB)
                    }
                }
            };
            Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateSqlServerWindowsAuth(conflictingKeytabs, env))
                .Message.ShouldContain("different Kerberos configuration/credentials");

            // 5. Positive control: valid keytab and krb5.conf files succeed
            var validKerberos = new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["sql1"] = KrbSource(keytabA, krb5Conf)
                    }
                }
            };
            Should.NotThrow(() => GatewayStartupValidator.ValidateSqlServerWindowsAuth(validKerberos, env));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

[Trait("Category", "Security")]
public sealed class CsvExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-csv-504-" + Guid.NewGuid().ToString("N"));

    public CsvExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Header_cells_are_escaped()
    {
        var audit = NullFake.Create<IAuditLogRepository>();
        var env = new FakeHostEnvironment(Environments.Production) { ContentRootPath = _dir };
        string[] columns = ["=cmd|'/C calc'!A0", "+SUM(1,1)", "-1+1", "@evil", "safe_col"];
        IReadOnlyDictionary<string, object?> row = new Dictionary<string, object?>
        {
            ["=cmd|'/C calc'!A0"] = "v1",
            ["+SUM(1,1)"] = "v2",
            ["-1+1"] = "v3",
            ["@evil"] = "v4",
            ["safe_col"] = "normal"
        };
        var sqlResult = new GovernedSqlResult("SELECT 1", columns, [row], 1, 1);
        var sqlService = NullFake.Create<IGovernedSqlExecutionService>((method, _) =>
            method.Name == nameof(IGovernedSqlExecutionService.ExecuteQueryBufferedAsync)
                ? Task.FromResult(sqlResult)
                : NullFake.UseDefault);

        var services = new ServiceCollection();
        services.AddScoped(_ => sqlService);
        using var provider = services.BuildServiceProvider();

        var manager = new AsyncQueryJobManager(audit, environment: env);
        var worker = new AsyncQueryJobBackgroundWorker(
            manager,
            manager,
            provider.GetRequiredService<IServiceScopeFactory>(),
            audit,
            NullLogger<AsyncQueryJobBackgroundWorker>.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "alice"),
            new Claim("sub", "alice"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));
        var tenant = new TenantId("tenant-a");

        var job = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1", Format: "csv"), user, tenant);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitHelper.WaitUntil(
                async () => (await manager.GetJobAsync(job.JobId, tenant, "alice"))?.State == AsyncJobState.Completed,
                TimeSpan.FromSeconds(10),
                "CSV async job did not complete in time.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var stream = await manager.GetJobResultStreamAsync(job.JobId, tenant, "alice");
        stream.ShouldNotBeNull();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync();
        headerLine.ShouldNotBeNull();
        headerLine.ShouldBe("'=cmd|'/C calc'!A0,\"'+SUM(1,1)\",'-1+1,'@evil,safe_col");
    }
}

[Trait("Category", "Security")]
public sealed class HitLStepUpApprovalServiceTests
{
    private static readonly TableIdentifier TargetTable = new("sales", "public", "customers");

    private static HitLStepUpApprovalService CreateService(bool totpValid)
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                RequireTotp2Fa = false,
                RequireDifferentApprover = true,
                ApprovalTimeoutSeconds = 30
            }
        });
        var secretStore = NullFake.Create<ITotpSecretStore>((method, args) =>
            method.Name == nameof(ITotpSecretStore.GetSecretAsync) && (string?)args?[0] == "S-1-5-21-APPROVER"
                ? Task.FromResult<string?>("JBSWY3DPEHPK3PXP")
                : NullFake.UseDefault);
        var totpService = NullFake.Create<ITotpVerificationService>((method, _) =>
            method.Name == nameof(ITotpVerificationService.VerifyAndConsumeTotpAsync)
                ? Task.FromResult(totpValid)
                : NullFake.UseDefault);

        return new HitLStepUpApprovalService(
            options,
            NullLogger<HitLStepUpApprovalService>.Instance,
            totpVerificationService: totpService,
            totpSecretStore: secretStore,
            environment: new FakeHostEnvironment(Environments.Development));
    }

    [Fact]
    public async Task Enrolled_secret_without_code_is_denied_when_totp_not_required()
    {
        var service = CreateService(totpValid: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestTask = service.RequestStepUpApprovalAsync("mcp_query", "tenant-a", "S-1-5-21-REQUESTER", TargetTable, "audit", cts.Token);

        await WaitHelper.WaitUntil(
            () => service.GetPendingTickets("tenant-a").Count == 1,
            TimeSpan.FromSeconds(5),
            "Pending HitL ticket was not registered.");

        var ticket = service.GetPendingTickets("tenant-a").Single();
        var approver = new HitLApproverContext("S-1-5-21-APPROVER", ["S-1-5-21-APPROVER"], "tenant-a");

        var denied = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, approver, totpCode: null, cts.Token);
        denied.IsApproved.ShouldBeFalse();
        denied.Message.ShouldNotBeNull();
        denied.Message.ShouldContain("TOTP 2FA code is required");

        await service.RejectStepUpRequestAsync(ticket.ApprovalId, approver, "cleanup", cts.Token);
        (await requestTask).IsApproved.ShouldBeFalse();
    }

    [Fact]
    public async Task Valid_code_is_approved_positive_control()
    {
        var service = CreateService(totpValid: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestTask = service.RequestStepUpApprovalAsync("mcp_query", "tenant-a", "S-1-5-21-REQUESTER", TargetTable, "audit", cts.Token);

        await WaitHelper.WaitUntil(
            () => service.GetPendingTickets("tenant-a").Count == 1,
            TimeSpan.FromSeconds(5),
            "Pending HitL ticket was not registered.");

        var ticket = service.GetPendingTickets("tenant-a").Single();
        var approver = new HitLApproverContext("S-1-5-21-APPROVER", ["S-1-5-21-APPROVER"], "tenant-a");

        var approved = await service.ApproveStepUpRequestAsync(ticket.ApprovalId, approver, totpCode: "123456", cts.Token);
        approved.IsApproved.ShouldBeTrue();
        (await requestTask).IsApproved.ShouldBeTrue();
    }
}

[Trait("Category", "Security")]
public sealed class AsyncQueryJobScratchQuotaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-quota-504-" + Guid.NewGuid().ToString("N"));

    public AsyncQueryJobScratchQuotaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Quota_is_counted_per_tenant_not_globally()
    {
        var audit = NullFake.Create<IAuditLogRepository>();
        var env = new FakeHostEnvironment(Environments.Production) { ContentRootPath = _dir };
        var jobOptions = Options.Create(new AsyncJobOptions { MaxScratchBytes = 100 });
        var manager = new AsyncQueryJobManager(audit, environment: env, jobOptions: jobOptions);

        // Seed a completed job in tenant-a that already consumes the full 100-byte tenant scratch quota.
        manager.UpdateJob(new AsyncJobDescriptor(
            "exhausted-job-a",
            "tenant-a",
            "alice",
            "SELECT 1",
            "json",
            AsyncJobState.Completed,
            DateTimeOffset.UtcNow,
            BytesProduced: 100));

        // Tenant A has reached its 100-byte scratch quota and cannot submit new jobs.
        var userA = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice"), new Claim("tenant_id", "tenant-a")], "TestAuth"));
        await Should.ThrowAsync<AsyncJobQueueLimitExceededException>(() =>
            manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), userA, new TenantId("tenant-a")));

        // Positive control: Tenant B has its own isolated quota and can still submit jobs.
        var userB = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "bob"), new Claim("tenant_id", "tenant-b")], "TestAuth"));
        var jobB = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), userB, new TenantId("tenant-b"));
        jobB.TenantId.ShouldBe("tenant-b");
    }
}

[Trait("Category", "Security")]
public sealed class AnalyticsEndpointsSecurityTests
{
    private static DefaultHttpContext CreateContext()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "analyst-1"), new Claim("tenant_id", "tenant-a")], "TestAuth"))
        };
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int Status, string Body)> ExecuteResultAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        return (ctx.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Missing_table_returns_403_not_404()
    {
        var tableId = new TableIdentifier("sales", "dbo", "secret_table");
        var request = new AnalyticsEndpoints.AggregateRequestDto(
            "sales.dbo.secret_table",
            Aggregates: [new AnalyticsEndpoints.AggregateSelectionDto("count", null, "n")]);

        var missingService = NullFake.Create<IGovernedAggregationService>((method, _) =>
            method.Name == nameof(IGovernedAggregationService.ExecuteAggregationAsync)
                ? Task.FromException<AggregationResult>(new TableNotFoundException(tableId))
                : NullFake.UseDefault);
        var deniedService = NullFake.Create<IGovernedAggregationService>((method, _) =>
            method.Name == nameof(IGovernedAggregationService.ExecuteAggregationAsync)
                ? Task.FromException<AggregationResult>(new GatewaySecurityException("Access denied.", "ACCESS_DENIED"))
                : NullFake.UseDefault);

        var ctxMissing = CreateContext();
        var (statusMissing, bodyMissing) = await ExecuteResultAsync(
            await AnalyticsEndpoints.HandleAggregateAsync(request, ctxMissing, missingService, CancellationToken.None),
            ctxMissing);

        var ctxDenied = CreateContext();
        var (statusDenied, bodyDenied) = await ExecuteResultAsync(
            await AnalyticsEndpoints.HandleAggregateAsync(request, ctxDenied, deniedService, CancellationToken.None),
            ctxDenied);

        statusMissing.ShouldBe(StatusCodes.Status403Forbidden);
        statusDenied.ShouldBe(StatusCodes.Status403Forbidden);
        bodyMissing.ShouldContain("ACCESS_DENIED");
        bodyDenied.ShouldContain("ACCESS_DENIED");

        // Positive control: authorized existing table returns 200 OK.
        var okResult = new AggregationResult(
            tableId,
            [],
            null,
            new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false));
        var allowedService = NullFake.Create<IGovernedAggregationService>((method, _) =>
            method.Name == nameof(IGovernedAggregationService.ExecuteAggregationAsync)
                ? Task.FromResult(okResult)
                : NullFake.UseDefault);

        var ctxAllowed = CreateContext();
        var (statusAllowed, _) = await ExecuteResultAsync(
            await AnalyticsEndpoints.HandleAggregateAsync(request, ctxAllowed, allowedService, CancellationToken.None),
            ctxAllowed);
        statusAllowed.ShouldBe(StatusCodes.Status200OK);
    }
}

[Trait("Category", "Security")]
public sealed class DbtSemanticManifestIngestionServiceTests
{
    private static readonly TableIdentifier TargetTable = new("postgres", "public", "stg_orders");

    [Fact]
    public async Task Standing_requires_table_ownership_not_any_data_owner_role()
    {
        var tableId = Guid.NewGuid();
        var stored = new TableMetadata
        {
            Identifier = TargetTable,
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = "postgres",
                SchemaName = "public",
                TableName = "stg_orders",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                MinGroupSize = 5,
                NonPersonal = false
            },
            Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" }]
        };

        TableMetadata? persisted = null;
        var metadataRepo = NullFake.Create<ITableMetadataRepository>((method, args) => method.Name switch
        {
            nameof(ITableMetadataRepository.GetTableMetadataAsync) => Task.FromResult<TableMetadata?>(stored),
            nameof(ITableMetadataRepository.GetAllTablesAsync) => Task.FromResult<IReadOnlyList<TableMetadata>>([stored]),
            nameof(ITableMetadataRepository.UpsertTableMetadataAsync) => Task.FromResult(persisted = (TableMetadata)args![0]!),
            _ => NullFake.UseDefault
        });

        var ownerSid = new Sid("S-1-5-21-TABLE-OWNER");
        var unrelatedOwnerSid = new Sid("S-1-5-21-OTHER-DATA-OWNER");
        var ownershipRepo = NullFake.Create<IDataOwnershipRepository>((method, args) =>
            method.Name == nameof(IDataOwnershipRepository.IsAuthorizedApproverForTableAsync)
                ? Task.FromResult(
                    args![0] is TableIdentifier t && t.Equals(TargetTable) &&
                    args[1] is Sid s && s.Equals(ownerSid))
                : NullFake.UseDefault);

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "postgres" }
        });

        var service = new DbtMetadataIngestionService(
            NullFake.Create<IDbtProposalRepository>(),
            metadataRepo,
            NullFake.Create<ILineageGraphStore>(),
            epochRepository: null,
            gatewayOptions: gatewayOptions,
            sqlEndpointLoader: null,
            relationRepository: null,
            virtualFilterAdmin: null,
            tagImport: null,
            artifactStore: null,
            testTargetMap: null,
            logger: NullLogger<DbtMetadataIngestionService>.Instance,
            auditLog: NullFake.Create<IAuditLogRepository>(),
            auditWriter: NullFake.Create<IAuditWriter>(),
            ownershipRepo: ownershipRepo);

        const string manifestJson = """
        {
          "metadata": { "dbt_version": "1.8.0", "project_name": "test_corp" },
          "nodes": {
            "model.test_corp.stg_orders": {
              "name": "stg_orders",
              "database": "postgres",
              "schema": "public",
              "description": "Orders staging table",
              "config": { "materialized": "table" },
              "tags": [],
              "meta": { "autheris.min_group_size": "12" },
              "columns": {
                "id": { "name": "id", "data_type": "integer", "tags": [], "meta": {} }
              }
            }
          }
        }
        """;

        // 1. Caller with generic DataOwner role but NOT an owner of TargetTable has no standing -> rejected
        var unrelatedCaller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(unrelatedOwnerSid, IsSync: false),
            new TenantId("postgres"),
            IsClusterAdmin: false,
            IsGovernanceAdmin: false,
            IsDataOwner: true);
        using (var s1 = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)))
        {
            var r1 = await service.IngestManifestStreamAsync(s1, unrelatedCaller, dryRun: false);
            r1.Success.ShouldBeTrue();
            r1.Warnings.ShouldContain(w => w.Contains("standing"));
            persisted.ShouldNotBeNull();
            persisted.Table.MinGroupSize.ShouldBe(5);
        }

        // 2. Positive control: Caller who IS an authorized table owner of TargetTable has standing -> applied
        var tableOwnerCaller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(ownerSid, IsSync: false),
            new TenantId("postgres"),
            IsClusterAdmin: false,
            IsGovernanceAdmin: false,
            IsDataOwner: true);
        using (var s2 = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)))
        {
            var r2 = await service.IngestManifestStreamAsync(s2, tableOwnerCaller, dryRun: false);
            r2.Success.ShouldBeTrue();
            persisted.ShouldNotBeNull();
            persisted.Table.MinGroupSize.ShouldBe(12);
        }
    }
}
