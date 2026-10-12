namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.ResourceGroups;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Garnet;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>
/// Round 3 / GAP-C: per-tenant resource group fairness (H-07), Redis/Garnet TLS (H-01) and the
/// per-tenant data source allowlist for WebSQL and SQL endpoints (C-03).
/// </summary>
public sealed class IntegrationGapCTests
{
    // =========================================================================
    // H-07: resource group fairness per tenant
    // =========================================================================

    private static ResourceGroupManager CreateManager(
        int maxConcurrency,
        int maxQueueDepth,
        int timeoutSeconds,
        int perTenantPercent = 50,
        int perTenantAbsolute = 0)
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                Interactive = new ResourceGroupTierConfigOptions(maxConcurrency, maxQueueDepth, timeoutSeconds),
                MaxConcurrentPerTenantPercent = perTenantPercent,
                MaxConcurrentPerTenant = perTenantAbsolute
            }
        });
        return new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);
    }

    private static ResourceGroupTierMetrics InteractiveMetrics(ResourceGroupManager manager) =>
        manager.GetMetrics().Tiers.Single(t => t.Tier == ResourceGroupTier.Interactive);

    [Fact]
    public async Task H07_SingleTenantCannotOccupyWholeTier_OtherTenantStillGetsSlots()
    {
        using var manager = CreateManager(maxConcurrency: 4, maxQueueDepth: 0, timeoutSeconds: 1);
        manager.GetMaxConcurrencyPerTenant(ResourceGroupTier.Interactive).ShouldBe(2);

        var a1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        var a2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        a1.Success.ShouldBeTrue();
        a2.Success.ShouldBeTrue();

        // Tier still has 2 free slots, but tenant A has used up its share
        var a3 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        a3.Success.ShouldBeFalse();
        a3.RejectionReason.ShouldBe("QueueFull");

        var b1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-b");
        var b2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-b");
        b1.Success.ShouldBeTrue();
        b2.Success.ShouldBeTrue();

        InteractiveMetrics(manager).ActiveConcurrency.ShouldBe(4);

        await a1.Lease!.DisposeAsync();
        await a2.Lease!.DisposeAsync();
        await b1.Lease!.DisposeAsync();
        await b2.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task H07_TenantShareExhausted_TimesOutOnlyForThatTenant()
    {
        using var manager = CreateManager(maxConcurrency: 4, maxQueueDepth: 10, timeoutSeconds: 1);

        var a1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        var a2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");

        var waitingA = manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a").AsTask();
        await Task.Delay(50);

        // Tenant B is not blocked by tenant A's waiting request
        var b1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-b");
        b1.Success.ShouldBeTrue();

        var a3 = await waitingA;
        a3.Success.ShouldBeFalse();
        a3.RejectionReason.ShouldBe("Timeout");
        InteractiveMetrics(manager).TotalRejectedTimeout.ShouldBeGreaterThanOrEqualTo(1L);

        await a1.Lease!.DisposeAsync();
        await a2.Lease!.DisposeAsync();
        await b1.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task H07_QueuedTenantRequest_AcquiresAfterOwnLeaseIsReleased()
    {
        using var manager = CreateManager(maxConcurrency: 4, maxQueueDepth: 10, timeoutSeconds: 5);

        var a1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        var a2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");

        var waitingA = manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a").AsTask();
        await Task.Delay(50);
        waitingA.IsCompleted.ShouldBeFalse();
        InteractiveMetrics(manager).QueuedRequests.ShouldBe(1);

        await a1.Lease!.DisposeAsync();

        var a3 = await waitingA;
        a3.Success.ShouldBeTrue();
        manager.GetTenantActiveCount(ResourceGroupTier.Interactive, "tenant-a").ShouldBe(2);

        await a2.Lease!.DisposeAsync();
        await a3.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task H07_LeaseRelease_FreesTenantAndGlobalSlot_AndRemovesEmptyTenantEntry()
    {
        using var manager = CreateManager(maxConcurrency: 4, maxQueueDepth: 0, timeoutSeconds: 1);

        var a1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        var a2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        manager.GetTenantActiveCount(ResourceGroupTier.Interactive, "tenant-a").ShouldBe(2);
        manager.GetTrackedTenantCount(ResourceGroupTier.Interactive).ShouldBe(1);

        await a1.Lease!.DisposeAsync();
        await a1.Lease!.DisposeAsync(); // idempotent: must not release twice
        manager.GetTenantActiveCount(ResourceGroupTier.Interactive, "tenant-a").ShouldBe(1);
        InteractiveMetrics(manager).ActiveConcurrency.ShouldBe(1);

        await a2.Lease!.DisposeAsync();
        manager.GetTenantActiveCount(ResourceGroupTier.Interactive, "tenant-a").ShouldBe(0);
        manager.GetTrackedTenantCount(ResourceGroupTier.Interactive).ShouldBe(0);
        InteractiveMetrics(manager).ActiveConcurrency.ShouldBe(0);

        // Rejected requests must not leave tenant entries behind either
        var x1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-x");
        var x2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-x");
        var x3 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-x");
        x3.Success.ShouldBeFalse();
        await x1.Lease!.DisposeAsync();
        await x2.Lease!.DisposeAsync();
        manager.GetTrackedTenantCount(ResourceGroupTier.Interactive).ShouldBe(0);

        // Full share available again
        var again1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        var again2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        again1.Success.ShouldBeTrue();
        again2.Success.ShouldBeTrue();
        await again1.Lease!.DisposeAsync();
        await again2.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task H07_CancelledWhileWaitingForTenantShare_ReleasesEverything()
    {
        using var manager = CreateManager(maxConcurrency: 4, maxQueueDepth: 10, timeoutSeconds: 10);

        var a1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        var a2 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = false;
        try
        {
            await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a", cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        cancelled.ShouldBeTrue();
        InteractiveMetrics(manager).QueuedRequests.ShouldBe(0);
        manager.GetTenantActiveCount(ResourceGroupTier.Interactive, "tenant-a").ShouldBe(2);

        await a1.Lease!.DisposeAsync();
        await a2.Lease!.DisposeAsync();
        manager.GetTrackedTenantCount(ResourceGroupTier.Interactive).ShouldBe(0);
    }

    [Fact]
    public async Task H07_Concurrency_NeverExceedsTenantOrTierLimits()
    {
        using var manager = CreateManager(maxConcurrency: 6, maxQueueDepth: 100, timeoutSeconds: 30);
        manager.GetMaxConcurrencyPerTenant(ResourceGroupTier.Interactive).ShouldBe(3);

        string[] tenants = ["tenant-a", "tenant-b", "tenant-c"];
        var current = new int[tenants.Length];
        var maxObserved = new int[tenants.Length];
        int globalCurrent = 0;
        int globalMax = 0;
        int failures = 0;

        static void UpdateMax(ref int target, int value)
        {
            int snapshot;
            while (value > (snapshot = Volatile.Read(ref target)))
            {
                if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot)
                {
                    break;
                }
            }
        }

        var tasks = new List<Task>();
        for (int t = 0; t < tenants.Length; t++)
        {
            int tenantIndex = t;
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var lease = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, tenants[tenantIndex]);
                    if (!lease.Success)
                    {
                        Interlocked.Increment(ref failures);
                        return;
                    }

                    UpdateMax(ref maxObserved[tenantIndex], Interlocked.Increment(ref current[tenantIndex]));
                    UpdateMax(ref globalMax, Interlocked.Increment(ref globalCurrent));
                    await Task.Delay(5);
                    Interlocked.Decrement(ref current[tenantIndex]);
                    Interlocked.Decrement(ref globalCurrent);
                    await lease.Lease!.DisposeAsync();
                }));
            }
        }

        await Task.WhenAll(tasks);

        failures.ShouldBe(0);
        globalMax.ShouldBeLessThanOrEqualTo(6);
        foreach (var observed in maxObserved)
        {
            observed.ShouldBeLessThanOrEqualTo(3);
        }

        var metrics = InteractiveMetrics(manager);
        metrics.ActiveConcurrency.ShouldBe(0);
        metrics.QueuedRequests.ShouldBe(0);
        metrics.TotalAcquired.ShouldBe(60L);
        manager.GetTrackedTenantCount(ResourceGroupTier.Interactive).ShouldBe(0);
    }

    [Fact]
    public void H07_PerTenantLimit_AbsoluteOverridesPercent_AndIsBounded()
    {
        using (var absolute = CreateManager(maxConcurrency: 5, maxQueueDepth: 0, timeoutSeconds: 1, perTenantAbsolute: 1))
        {
            absolute.GetMaxConcurrencyPerTenant(ResourceGroupTier.Interactive).ShouldBe(1);
        }

        using (var capped = CreateManager(maxConcurrency: 5, maxQueueDepth: 0, timeoutSeconds: 1, perTenantAbsolute: 100))
        {
            capped.GetMaxConcurrencyPerTenant(ResourceGroupTier.Interactive).ShouldBe(5);
        }

        using (var minimumOne = CreateManager(maxConcurrency: 1, maxQueueDepth: 0, timeoutSeconds: 1))
        {
            minimumOne.GetMaxConcurrencyPerTenant(ResourceGroupTier.Interactive).ShouldBe(1);
        }

        using (var fullTier = CreateManager(maxConcurrency: 8, maxQueueDepth: 0, timeoutSeconds: 1, perTenantPercent: 100))
        {
            fullTier.GetMaxConcurrencyPerTenant(ResourceGroupTier.Interactive).ShouldBe(8);
        }
    }

    [Fact]
    public async Task H07_Middleware_TenantShareExhausted_Returns429_OtherTenantPasses()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                Interactive = new ResourceGroupTierConfigOptions(MaxConcurrency: 2, MaxQueueDepth: 0, TimeoutSeconds: 1)
            }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);
        var held = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        held.Success.ShouldBeTrue();

        var nextCalls = 0;
        var middleware = new ResourceGroupMiddleware(
            _ => { nextCalls++; return Task.CompletedTask; },
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var contextA = new DefaultHttpContext();
        contextA.Request.Path = "/graphql";
        contextA.Items[TenantResolutionMiddleware.TenantIdItemKey] = new TenantId("tenant-a");
        contextA.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(contextA);
        contextA.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        nextCalls.ShouldBe(0);

        var contextB = new DefaultHttpContext();
        contextB.Request.Path = "/graphql";
        contextB.Items[TenantResolutionMiddleware.TenantIdItemKey] = new TenantId("tenant-b");
        contextB.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(contextB);
        contextB.Response.StatusCode.ShouldNotBe(StatusCodes.Status429TooManyRequests);
        nextCalls.ShouldBe(1);

        await held.Lease!.DisposeAsync();
    }

    // =========================================================================
    // H-01: Redis / Garnet TLS
    // =========================================================================

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void H01_Redis_NonLoopbackWithoutTls_OutsideDevelopment_AbortsStart()
    {
        var ex = Should.Throw<InvalidOperationException>(() => RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t"),
            new RedisOptions(),
            null,
            Env("Production")));
        ex.Message.ShouldContain("TLS");

        // Several endpoints: one non-loopback endpoint is enough to require TLS
        Should.Throw<InvalidOperationException>(() => RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("localhost:6379,10.0.0.5:6379,password=s3cr3t"),
            new RedisOptions(),
            null,
            Env("Staging")));
    }

    [Fact]
    public void H01_Redis_UseTls_SetsSslAndSslHost()
    {
        var secured = RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t"),
            new RedisOptions { UseTls = true, SslHost = "redis.corp.example" },
            null,
            Env("Production"));

        secured.Ssl.ShouldBeTrue();
        secured.SslHost.ShouldBe("redis.corp.example");

        // ssl=true in the connection string is accepted as well
        var fromConnectionString = RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("redis.internal:6380,password=s3cr3t,ssl=true"),
            new RedisOptions(),
            null,
            Env("Production"));
        fromConnectionString.Ssl.ShouldBeTrue();
    }

    [Fact]
    public void H01_Redis_LoopbackOrDevelopment_DoesNotRequireTls()
    {
        Should.NotThrow(() => RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("127.0.0.1:6379,password=s3cr3t"), new RedisOptions(), null, Env("Production")));
        Should.NotThrow(() => RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("localhost:6379,password=s3cr3t"), new RedisOptions(), null, Env("Production")));
        Should.NotThrow(() => RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("redis.internal:6379"), new RedisOptions(), null, Env("Development")));

        RedisConnectionSecurity.HasNonLoopbackEndpoint(ConfigurationOptions.Parse("localhost:6379,127.0.0.1:6380")).ShouldBeFalse();
        RedisConnectionSecurity.HasNonLoopbackEndpoint(ConfigurationOptions.Parse("redis:6379")).ShouldBeTrue();
    }

    [Fact]
    public void H01_Redis_CertificatePinning_RequiresTlsAndValidThumbprints()
    {
        using var cert = CreateSelfSignedCertificate("redis.internal");
        var sha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);

        // Pinning without TLS is a misconfiguration
        Should.Throw<InvalidOperationException>(() => RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t"),
            new RedisOptions { AllowedServerCertificateThumbprints = [sha256] },
            null,
            Env("Development")));

        // Invalid thumbprint format
        Should.Throw<InvalidOperationException>(() => RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t"),
            new RedisOptions { UseTls = true, AllowedServerCertificateThumbprints = ["not-a-thumbprint"] },
            null,
            Env("Production")));

        var pinned = RedisConnectionSecurity.Apply(
            ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t"),
            new RedisOptions { UseTls = true, AllowedServerCertificateThumbprints = [sha256] },
            null,
            Env("Production"));
        pinned.Ssl.ShouldBeTrue();
    }

    [Fact]
    public void H01_PinnedCertificateValidator_AcceptsOnlyPinnedCertificates()
    {
        using var pinnedCert = CreateSelfSignedCertificate("redis.internal");
        using var otherCert = CreateSelfSignedCertificate("attacker.internal");

        // Separators and case are normalized ("AB-CD-..." from BitConverter)
        var formatted = BitConverter.ToString(pinnedCert.GetCertHash(HashAlgorithmName.SHA256));
        var pins = RedisConnectionSecurity.NormalizeThumbprints([formatted], "test");
        pins.ShouldHaveSingleItem().ShouldBe(pinnedCert.GetCertHashString(HashAlgorithmName.SHA256));

        var validator = RedisConnectionSecurity.CreatePinnedCertificateValidator(pins);
        validator(new object(), pinnedCert, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
        validator(new object(), otherCert, null, SslPolicyErrors.None).ShouldBeFalse();
        validator(new object(), null, null, SslPolicyErrors.RemoteCertificateNotAvailable).ShouldBeFalse();

        // SHA-1 pins are supported as well
        var sha1Validator = RedisConnectionSecurity.CreatePinnedCertificateValidator(
            RedisConnectionSecurity.NormalizeThumbprints([pinnedCert.GetCertHashString()], "test"));
        sha1Validator(new object(), pinnedCert, null, SslPolicyErrors.None).ShouldBeTrue();
    }

    [Fact]
    public void H01_GarnetServerArguments_ContainTlsOnlyWhenEnabled()
    {
        var plain = GarnetServerManager.BuildServerArguments(new GarnetOptions(), "pw", null);
        plain.ShouldNotContain("--tls");
        plain.ShouldContain("--auth");

        var tls = GarnetServerManager.BuildServerArguments(
            new GarnetOptions { EnableTls = true, TlsCertFile = "/certs/garnet.pfx" },
            "pw",
            "pfx-pw");
        tls.ShouldContain("--tls");
        var certIndex = tls.IndexOf("--cert-file-name");
        certIndex.ShouldBeGreaterThanOrEqualTo(0);
        tls[certIndex + 1].ShouldBe("/certs/garnet.pfx");
        var pwIndex = tls.IndexOf("--cert-password");
        pwIndex.ShouldBeGreaterThanOrEqualTo(0);
        tls[pwIndex + 1].ShouldBe("pfx-pw");

        Should.Throw<InvalidOperationException>(() =>
            GarnetServerManager.BuildServerArguments(new GarnetOptions { EnableTls = true }, "pw", null));
    }

    [Fact]
    public void H01_GarnetTls_MissingCertificate_AbortsStart()
    {
        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Garnet = new GarnetOptions
                {
                    EnableEmbeddedServer = true,
                    Host = "127.0.0.1",
                    Port = 3297,
                    EnableTls = true,
                    TlsCertFile = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.pfx")
                }
            }
        });

        using var manager = new GarnetServerManager(options, environment: Env("Production"));
        Should.Throw<InvalidOperationException>(() => manager.StartServer());
        manager.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void H01_GarnetTls_UnresolvableCertPasswordSecret_FailsFast()
    {
        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Garnet = new GarnetOptions
                {
                    EnableTls = true,
                    TlsCertFile = "/certs/garnet.pfx",
                    TlsCertPasswordSecretRef = $"GAPC_UNSET_{Guid.NewGuid():N}"
                }
            }
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            using var unresolved = new GarnetServerManager(options);
        });

        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("pfx-secret"));
        Should.NotThrow(() =>
        {
            using var resolved = new GarnetServerManager(options, secretProvider: provider);
        });
    }

    [Fact]
    public void H01_GarnetClientTls_IsConfiguredOnlyWhenEnabled()
    {
        var plain = RedisConnectionSecurity.ApplyGarnetClientTls(new ConfigurationOptions(), new GarnetOptions());
        plain.Ssl.ShouldBeFalse();

        var tls = RedisConnectionSecurity.ApplyGarnetClientTls(
            new ConfigurationOptions(),
            new GarnetOptions { EnableTls = true, Host = "127.0.0.1" });
        tls.Ssl.ShouldBeTrue();
        tls.SslHost.ShouldBe("127.0.0.1");

        var withHost = RedisConnectionSecurity.ApplyGarnetClientTls(
            new ConfigurationOptions(),
            new GarnetOptions { EnableTls = true, TlsSslHost = "garnet.local" });
        withHost.SslHost.ShouldBe("garnet.local");
    }

    // =========================================================================
    // C-03: per-tenant data source allowlist (WebSQL + SQL endpoints)
    // =========================================================================

    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-GAPC-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static ITableMetadataRepository CreateRepository()
    {
        var employees = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "employees"),
            Table = new Table { TableName = "employees", SchemaName = "public", SourceName = string.Empty, SourceType = "PostgreSQL" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().Equals(employees.Identifier) ? employees : null));
        return repo;
    }

    private static GovernedSqlExecutionService CreateSqlService(
        List<string> allowedDataSources,
        Dictionary<string, List<string>> tenantAllowlist)
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowedDataSources = allowedDataSources,
                TenantDataSourceAllowlist = tenantAllowlist,
                DefaultMaxRows = 100,
                MaxAllowedRows = 500
            }
        };

        var consentRepository = Substitute.For<IConsentRepository>();
        consentRepository.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var consentResolution = Substitute.For<IConsentResolutionService>();
        consentResolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>(),
                null,
                hasUnconstrainedColumnAllow: true));

        var devEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        devEnv.EnvironmentName.Returns("Development");

        return new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: consentResolution,
            tableRepository: CreateRepository(),
            connectionFactory: null,
            clientIpResolver: null,
            environment: devEnv,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            secretProvider: null);
    }

    private static Task ExecuteAsync(GovernedSqlExecutionService service, string? dataSource) =>
        service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest("SELECT id FROM employees", DataSourceName: dataSource),
            CreateUser(),
            new TenantId(Tenant),
            (_, _) => Task.CompletedTask);

    [Fact]
    public async Task C03_TenantAllowlist_RestrictsTenantToListedDataSources()
    {
        var service = CreateSqlService(
            ["analytics", "hr_prod"],
            new Dictionary<string, List<string>> { [Tenant] = ["analytics"] });

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, "hr_prod"));
        ex.Message.ShouldContain("tenant");

        await Should.NotThrowAsync(() => ExecuteAsync(service, "analytics"));
    }

    [Fact]
    public async Task C03_TenantAllowlist_CannotExtendGlobalAllowlist()
    {
        var service = CreateSqlService(
            [],
            new Dictionary<string, List<string>> { [Tenant] = ["finance", "default"] });

        await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, "finance"));
        await Should.NotThrowAsync(() => ExecuteAsync(service, "default"));
    }

    [Fact]
    public async Task C03_TenantAllowlist_WithoutDefault_RejectsDefaultDataSource()
    {
        var service = CreateSqlService(
            ["analytics"],
            new Dictionary<string, List<string>> { [Tenant] = ["analytics"] });

        await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, null));
        await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, "default"));
    }

    [Fact]
    public async Task C03_TenantWithoutEntry_KeepsGlobalAllowlist()
    {
        var service = CreateSqlService(
            ["analytics"],
            new Dictionary<string, List<string>> { ["tenant_b"] = ["default"] });

        await Should.NotThrowAsync(() => ExecuteAsync(service, "analytics"));
        await Should.NotThrowAsync(() => ExecuteAsync(service, null));
        await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, "hr_prod"));
    }

    [Fact]
    public async Task C03_TenantAllowlist_IsEnforcedForSqlEndpointBufferedPath()
    {
        var service = CreateSqlService(
            ["analytics"],
            new Dictionary<string, List<string>> { [Tenant] = ["default"] });

        // SqlEndpointExecutionService runs endpoints via ExecuteQueryBufferedAsync
        await Should.ThrowAsync<WebSqlPolicyException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id FROM employees", DataSourceName: "analytics"),
            CreateUser(),
            new TenantId(Tenant)));
    }

    [Fact]
    public void C03_TenantAllowlist_KeyMatchingAndEmptyList()
    {
        var options = new WebSqlOptions
        {
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>
            {
                ["TENANT_A"] = ["Analytics"],
                ["tenant_empty"] = []
            }
        };

        GovernedSqlExecutionService.IsDataSourceAllowedForTenant(options, new TenantId("tenant_a"), "analytics").ShouldBeTrue();
        GovernedSqlExecutionService.IsDataSourceAllowedForTenant(options, new TenantId("tenant_a"), "default").ShouldBeFalse();
        GovernedSqlExecutionService.IsDataSourceAllowedForTenant(options, new TenantId("tenant_empty"), "default").ShouldBeFalse();
        GovernedSqlExecutionService.IsDataSourceAllowedForTenant(options, new TenantId("tenant_other"), "default").ShouldBeTrue();
        GovernedSqlExecutionService.IsDataSourceAllowedForTenant(new WebSqlOptions(), new TenantId("tenant_a"), "default").ShouldBeTrue();
    }
}
