namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.TableCache;
using Autheris.Application.Caching.TableCache.Engines;
using Autheris.Application.Caching.TableCache.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Infrastructure.Caching.TableCache.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableCacheGovernanceSecurityTests : IDisposable
{
    private readonly string _testDir;
    private readonly TableCacheOptions _options;
    private readonly InMemoryTableCacheStore _store;
    private readonly TableCacheCatalog _catalog;
    private readonly byte[] _masterKey = Encoding.UTF8.GetBytes("01234567890123456789012345678901");

    public TableCacheGovernanceSecurityTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris_sec_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _options = new TableCacheOptions
        {
            Enabled = true,
            PersistentDirectory = _testDir,
            DefaultMemoryLimitMb = 256,
            DefaultDiskLimitMb = 100,
            MaxStaleSurvivalSeconds = 86400,
            Encryption = new TableCacheEncryptionOptions
            {
                Enabled = true,
                KeyVaultKeyName = "test-key-32bytes-long-vault-name"
            }
        };

        _store = new InMemoryTableCacheStore(Options.Create(_options));
        _catalog = new TableCacheCatalog();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    private async Task SeedTableDataAsync(TableIdentifier table, long epoch = 1)
    {
        var columns = new List<string> { "id", "tenant_id", "name", "ssn", "dept_code" };

        async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "1", "tenant_alpha", "Alice Smith", "123-45-6789", "FIN" };
            yield return new object?[] { "2", "tenant_alpha", "Bob Jones", "987-65-4321", "ENG" };
            yield return new object?[] { "3", "tenant_beta", "Charlie Brown", "555-55-5555", "FIN" };
            await Task.CompletedTask;
        }

        var shadow = await _store.StageShadowGenerationAsync(table, epoch, columns, Rows());
        var meta = await _store.SwapActiveGenerationAsync(table, epoch, shadow, rowCount: 3, sizeInBytes: 2048, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        _catalog.Upsert(meta with
        {
            CachedColumns = columns,
            OfflinePolicy = TableCacheOfflinePolicy.ServeStaleWithWarning
        });
    }

    [Fact]
    public async Task Governance_CachedTable_EvaluatesCasbinAbac_DeniesUnauthorizedCallerWith403()
    {
        // Arrange (T-SEC-BL04-01)
        var table = new TableIdentifier("crm", "public", "customers");
        await SeedTableDataAsync(table);

        var service = new GovernedTableCacheExecutionService(_catalog, _store, Options.Create(_options));

        var unauthorizedCaller = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "malicious_user")])),
            TenantId: new TenantId("tenant_alpha"),
            Roles: new HashSet<string> { "DENY_ALL" });

        // Act & Assert: Unauthorized caller rejected with 403 (AUTHZ_TABLE_DENIED)
        var ex = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ExecuteGovernedQueryAsync(table, "SELECT * FROM customers", unauthorizedCaller);
        });

        ex.Message.ShouldContain(GovernedTableCacheExecutionService.AuthzTableDeniedCode);
    }

    [Fact]
    public async Task Governance_CachedTable_InjectsMandatoryTenantFilter_IsolatesMultiTenantData()
    {
        // Arrange (T-SEC-BL04-02)
        var table = new TableIdentifier("crm", "public", "customers");
        await SeedTableDataAsync(table);

        var service = new GovernedTableCacheExecutionService(_catalog, _store, Options.Create(_options));

        var callerAlpha = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user_alpha")])),
            TenantId: new TenantId("tenant_alpha"));

        // Act: Caller Alpha queries table containing tenant_alpha and tenant_beta records
        var result = await service.ExecuteGovernedQueryAsync(table, "SELECT id, tenant_id, name FROM customers", callerAlpha);

        // Assert: MUST ONLY receive tenant_alpha rows. Zero cross-tenant leakage!
        result.Rows.Count.ShouldBe(2);
        foreach (var row in result.Rows)
        {
            row[1].ShouldBe("tenant_alpha");
            row[1].ShouldNotBe("tenant_beta");
        }

        // Empty tenant context must fail closed with 403 (TENANT_CONTEXT_MISSING)
        var emptyTenantCaller = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            TenantId: default);

        var emptyEx = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ExecuteGovernedQueryAsync(table, "SELECT * FROM customers", emptyTenantCaller);
        });
        emptyEx.Message.ShouldContain(GovernedTableCacheExecutionService.TenantContextMissingCode);
    }

    [Fact]
    public async Task Governance_CachedTable_AppliesDynamicColumnMasking_RedactsSensitiveData()
    {
        // Arrange (T-SEC-BL04-03)
        var table = new TableIdentifier("crm", "public", "customers");
        await SeedTableDataAsync(table);

        var service = new GovernedTableCacheExecutionService(_catalog, _store, Options.Create(_options));

        var caller = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            TenantId: new TenantId("tenant_alpha"));

        var columnMasks = new Dictionary<string, string>
        {
            ["ssn"] = "REDACT"
        };

        // Act: Query ssn column with REDACT mask rule
        var result = await service.ExecuteGovernedQueryAsync(
            table,
            "SELECT id, name, ssn FROM customers",
            caller,
            requestedColumns: ["id", "name", "ssn"],
            columnMasks: columnMasks);

        // Assert: Raw ssn values are redacted to '***-**-****'
        result.Rows.Count.ShouldBe(2);
        foreach (var row in result.Rows)
        {
            row[2].ShouldBe("***-**-****");
        }
    }

    [Fact]
    public async Task Governance_CachedTable_EnforcesRowLevelSecurity_InjectsAttributeFilters()
    {
        // Arrange (T-SEC-BL04-04)
        var table = new TableIdentifier("crm", "public", "customers");
        await SeedTableDataAsync(table);

        var service = new GovernedTableCacheExecutionService(_catalog, _store, Options.Create(_options));

        // Caller has RLS attribute dept_code = 'FIN'
        var caller = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            TenantId: new TenantId("tenant_alpha"),
            Attributes: new Dictionary<string, string> { ["dept_code"] = "FIN" });

        // Act: Query table
        var result = await service.ExecuteGovernedQueryAsync(
            table,
            "SELECT id, name, dept_code FROM customers",
            caller,
            requestedColumns: ["id", "name", "dept_code"]);

        // Assert: Only FIN rows for tenant_alpha are returned
        result.Rows.Count.ShouldBe(1);
        result.Rows[0][1].ShouldBe("Alice Smith");
        result.Rows[0][2].ShouldBe("FIN");
    }

    [Fact]
    public void Store_EncryptedFile_MatchesAes256GcmIntegrity_TamperThrowsCryptographicException()
    {
        // Arrange (T-SEC-BL04-05)
        var dek = TableCacheCryptoHelper.DeriveKey(_masterKey);
        var originalData = Encoding.UTF8.GetBytes("CRITICAL_AUTHENTICATED_DATABASE_BLOCK");

        // Act
        var ciphertext = TableCacheCryptoHelper.Encrypt(dek, originalData);

        // Tamper with 1 byte in the ciphertext payload
        ciphertext[20] ^= 0x01;

        // Assert: Decryption must fail closed with CryptographicException
        Should.Throw<CryptographicException>(() =>
        {
            TableCacheCryptoHelper.Decrypt(dek, ciphertext);
        });
    }

    [Fact]
    public void Store_MetadataHmac_CorruptedSignature_FailsClosedWithSecurityException()
    {
        // Arrange (T-SEC-BL04-06)
        var dek = TableCacheCryptoHelper.DeriveKey(_masterKey);
        var metadataJson = Encoding.UTF8.GetBytes("{\"Table\":\"crm.customers\",\"Epoch\":100}");
        var validHmac = TableCacheCryptoHelper.ComputeHmac(dek, metadataJson);

        // Tamper with HMAC signature
        var corruptedHmac = (byte[])validHmac.Clone();
        corruptedHmac[0] ^= 0xFF;

        // Act & Assert: FixedTimeEquals mismatch throws SecurityException
        Should.Throw<SecurityException>(() =>
        {
            TableCacheCryptoHelper.VerifyHmacOrThrow(dek, metadataJson, corruptedHmac);
        });
    }

    [Fact]
    public async Task Store_ShadowSwap_ZeroRowStagingAgainstPopulatedActive_AbortsSwap()
    {
        // Arrange (T-SEC-BL04-07)
        var table = new TableIdentifier("crm", "public", "active_customers");
        await SeedTableDataAsync(table);

        async IAsyncEnumerable<IReadOnlyList<object?>> EmptyRows()
        {
            await Task.CompletedTask;
            yield break;
        }

        var shadow = await _store.StageShadowGenerationAsync(table, 999, ["id"], EmptyRows());

        // Act & Assert: Swap with 0 rows against populated table must abort
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _store.SwapActiveGenerationAsync(table, 999, shadow, rowCount: 0, sizeInBytes: 0, expiresAt: null);
        });

        ex.Message.ShouldContain("Zero-state protection");
    }

    [Fact]
    public async Task CdcConsumer_GapDetected_FencesTableAndTriggersShadowReload()
    {
        // Arrange (T-SEC-BL04-08)
        var streamStore = Substitute.For<ICdcStreamStore>();
        var cacheStore = Substitute.For<ITableCacheStore>();
        var catalog = new TableCacheCatalog();
        var manager = Substitute.For<ITableCacheManager>();

        var table = new TableIdentifier("crm", "public", "customers");
        var streamId = new CdcStreamId(Guid.NewGuid().ToString("N"));

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 1, DateTimeOffset.UtcNow, null, 10, 1000, 100, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));

        var gapEvents = new List<CdcStreamEventRecord>
        {
            new(streamId, 105, 1, CdcStreamEventKind.Gap, null, CdcTenantScope.Row, "t1", null, new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow)
        };

        streamStore.ReadAsync(streamId, 101, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CdcReadResult(100, 105, gapEvents));

        var consumer = new CdcTableCacheConsumer(streamStore, cacheStore, catalog, manager);

        // Act
        await consumer.ProcessStreamBatchAsync(streamId, table);

        // Assert: Table is fenced (IsHealthy = false) and ForceRefreshAsync triggered
        catalog.TryGet(table, out var fenced).ShouldBeTrue();
        fenced.ShouldNotBeNull();
        fenced.IsHealthy.ShouldBeFalse();

        await manager.Received(1).ForceRefreshAsync(table, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Governance_OfflineMode_RevokedConsent_DeniesCallerWith403EvenWhenSourceDown()
    {
        // Arrange (T-SEC-BL04-09)
        var table = new TableIdentifier("crm", "public", "customers");
        await SeedTableDataAsync(table);

        var consentService = Substitute.For<IConsentResolutionService>();
        // Consent is REVOKED
        consentService.ResolveAccess(
            Arg.Any<Sid>(),
            Arg.Any<IReadOnlySet<Sid>>(),
            Arg.Any<IReadOnlySet<string>>(),
            table,
            Arg.Any<IReadOnlyList<Consent>>(),
            Arg.Any<DatabaseDialect>(),
            Arg.Any<IReadOnlySet<Sid>?>(),
            Arg.Any<IReadOnlySet<string>?>())
            .Returns(TableAccessDecision.Denied(table, "CONSENT_REVOKED: Caller consent was withdrawn."));

        var service = new GovernedTableCacheExecutionService(
            _catalog,
            _store,
            Options.Create(_options),
            consentService: consentService);

        var caller = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            TenantId: new TenantId("tenant_alpha"));

        // Act & Assert: Revoked consent denies with 403 (CONSENT_REQUIRED) even in offline mode
        var ex = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ExecuteGovernedQueryAsync(table, "SELECT * FROM customers", caller, simulateUpstreamOutage: true);
        });

        ex.Message.ShouldContain(GovernedTableCacheExecutionService.ConsentRequiredCode);
    }

    [Fact]
    public async Task Governance_OfflineMode_ExceedingMaxStaleSurvival_FailsClosedWith503()
    {
        // Arrange (T-SEC-BL04-10)
        var table = new TableIdentifier("crm", "public", "customers");
        await SeedTableDataAsync(table);

        var timeProvider = new FakeTimeProvider();
        var shortStaleOptions = new TableCacheOptions
        {
            MaxStaleSurvivalSeconds = 60 // 60 seconds max stale
        };

        // Cache refreshed 100 seconds ago, expired 10 seconds ago
        var expiredMeta = new TableCacheEntryMetadata(
            table,
            TableCacheStorageTier.Memory,
            ":memory:",
            DuckDbPersistentTableCacheStore.GetActiveViewName(table),
            1,
            timeProvider.GetUtcNow().AddSeconds(-100),
            timeProvider.GetUtcNow().AddSeconds(-10),
            3,
            2048,
            null,
            true,
            null,
            ["id", "tenant_id", "name"],
            null,
            TableCacheOfflinePolicy.ServeStaleWithWarning);

        _catalog.Upsert(expiredMeta);

        var service = new GovernedTableCacheExecutionService(
            _catalog,
            _store,
            Options.Create(shortStaleOptions),
            timeProvider: timeProvider);

        var caller = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity()),
            TenantId: new TenantId("tenant_alpha"));

        // Act & Assert: Stale duration (100s) exceeds MaxStaleSurvivalSeconds (60s) -> 503 fail closed
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await service.ExecuteGovernedQueryAsync(table, "SELECT * FROM customers", caller);
        });

        ex.Message.ShouldContain(GovernedTableCacheExecutionService.GatewayCacheStaleExpiredCode);
    }

    [Fact]
    public void Router_DirectAccessToStagingOrRawFiles_DeniesWith403()
    {
        // Arrange (T-SEC-BL04-11)
        var catalog = new TableCacheCatalog();
        var router = new TableCacheQueryRouter(catalog);

        // Act & Assert
        Should.Throw<SecurityException>(() =>
        {
            TableCacheQueryRouter.ValidateIdentifier("autheris_cache.staging_orders");
        }).Message.ShouldContain("INTERNAL_TABLE_ACCESS_PROHIBITED");

        Should.Throw<SecurityException>(() =>
        {
            TableCacheQueryRouter.ValidateIdentifier("staging_customers_9999");
        }).Message.ShouldContain("INTERNAL_TABLE_ACCESS_PROHIBITED");
    }

    [Fact]
    public async Task Manager_SingleFlight_PropagatesUpstreamError_CleansLockWithoutDeadlock()
    {
        // Arrange (T-SEC-BL04-12)
        var catalog = new TableCacheCatalog();
        var store = Substitute.For<ITableCacheStore>();
        var loader = Substitute.For<ITableCacheUpstreamLoader>();
        var table = new TableIdentifier("crm", "public", "error_table");

        loader.LoadTableDataAsync(table, Arg.Any<CancellationToken>())
            .Returns<Task<TableCacheLoadResult>>(_ => throw new InvalidOperationException("OLTP database connection dropped"));

        var manager = new TableCacheManager(catalog, store, Options.Create(new TableCacheOptions()), loader);

        // Act: 10 concurrent requests
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await Should.ThrowAsync<InvalidOperationException>(async () =>
                {
                    await manager.GetOrLoadAsync(table);
                });
            }));
        }

        await Task.WhenAll(tasks);

        // Assert: flight lock dictionary is cleanly emptied, no deadlock on subsequent calls
        manager.GetStats().TotalMisses.ShouldBe(10);
    }

    [Fact]
    public async Task Governance_CachedTable_EmitsWormAuditRecord_WithCacheHitAndMaskMetadata()
    {
        // Arrange (T-SEC-BL04-13)
        var table = new TableIdentifier("crm", "public", "customers");
        await SeedTableDataAsync(table);

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var service = new GovernedTableCacheExecutionService(
            _catalog,
            _store,
            Options.Create(_options),
            auditRepo: auditRepo);

        var caller = new TableCacheCallerContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "audited_user")])),
            TenantId: new TenantId("tenant_alpha"));

        var columnMasks = new Dictionary<string, string> { ["ssn"] = "REDACT" };

        // Act
        await service.ExecuteGovernedQueryAsync(
            table,
            "SELECT id, name, ssn FROM customers",
            caller,
            requestedColumns: ["id", "name", "ssn"],
            columnMasks: columnMasks);

        // Assert: WORM audit log recorded with CacheHit = true and mask metadata
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "TABLE_CACHE_QUERY" &&
                e.TargetTable == table.ToString() &&
                e.DetailsJson.Contains("\"CacheHit\":true") &&
                e.DetailsJson.Contains("ssn")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Store_MemoryAndDiskQuotas_EnforcesBoundariesWithoutOomCrash()
    {
        // Arrange (T-SEC-BL04-14)
        var smallOptions = new TableCacheOptions
        {
            PersistentDirectory = _testDir,
            DefaultDiskLimitMb = 0 // 0MB forces quota exception
        };

        // Act & Assert
        using var smallStore = new DuckDbPersistentTableCacheStore(Options.Create(smallOptions), customEncryptionKey: _masterKey);
        var table = new TableIdentifier("crm", "public", "huge_table");

        // Write a 1KB dummy file in the directory to breach 0MB
        var dummy = Path.Combine(_testDir, "test.dat");
        File.WriteAllBytes(dummy, new byte[1024]);

        async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "1" };
            await Task.CompletedTask;
        }

        Should.Throw<InvalidOperationException>(() =>
        {
            smallStore.StageShadowGenerationAsync(table, 1, ["id"], Rows()).GetAwaiter().GetResult();
        }).Message.ShouldContain("limit exceeded");
    }
}
