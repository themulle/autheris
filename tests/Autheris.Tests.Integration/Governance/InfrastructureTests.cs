using Autheris.Tests.Common;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public class InfrastructureTests : IDisposable
{
    private readonly InProcessChannelEventBus _eventBus = new();
    private readonly EpochValidationService _epochService;
    private readonly IMemoryCache _memoryCache;
    private readonly ConsentCacheService _cacheService;
    private readonly PostgreSqlGovernanceRepository _repository;

    public InfrastructureTests()
    {
        _epochService = new EpochValidationService(eventBus: _eventBus);
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _cacheService = new ConsentCacheService(_memoryCache, _epochService, _eventBus);
        _repository = GovernanceTestDatabase.CreateRepository(_epochService);
    }

    [Fact]
    public async Task EpochAndConsentCache_InvalidationFlow_WorksCorrectly()
    {
        var table = new TableIdentifier("finance", "dbo", "invoices");
        var userSid = new Sid("S-1-5-21-9999");
        var decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>
        {
            ["amount"] = ColumnAccessLevel.Clear
        });

        // 1. Initially nothing cached
        var cached = await _cacheService.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, userSid, table);
        cached.ShouldBeNull();

        // 2. Set cache
        await _cacheService.SetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, userSid, table, decision, TimeSpan.FromMinutes(5));

        // 3. Cache hit
        var hit = await _cacheService.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, userSid, table);
        hit.ShouldNotBeNull();
        hit.IsAllowed.ShouldBeTrue();
        hit.ColumnAccess["amount"].ShouldBe(ColumnAccessLevel.Clear);

        // 4. Invalidate epoch
        await _epochService.InvalidateEpochAsync(table);

        // 5. Subsequent lookup fails because epoch has changed
        var invalidated = await _cacheService.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, userSid, table);
        invalidated.ShouldBeNull();
    }

    [Fact]
    public async Task GovernanceRepository_SeededCatalog_Contains100Tables()
    {
        var allTables = await _repository.GetAllTablesAsync();
        allTables.Count.ShouldBeGreaterThanOrEqualTo(100);

        var financeTable1 = await _repository.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
        financeTable1.ShouldNotBeNull();
        financeTable1.Columns.Count.ShouldBe(5);
        financeTable1.Columns.ShouldContain(c => c.ColumnName == "amount");
        ClassificationMetadata.IsColumnSensitive(financeTable1, "email").ShouldBeTrue();
        financeTable1.ColumnMaskingRules.ContainsKey("email").ShouldBeTrue();
    }

    [Fact]
    public async Task AuditLog_Sha256HashChain_MaintainsTamperProofIntegrity()
    {
        var actorSid = new Sid("S-1-5-21-AUDIT-USER");

        // Record 3 events
        await _repository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            EventType = "TABLE_QUERY",
            ActorSid = actorSid,
            TargetTable = "finance.dbo.finance_table_1",
            Decision = "ALLOW",
            TraceId = "trace-001",
            DetailsJson = "{\"columns\":[\"id\",\"amount\"]}"
        });

        await _repository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            EventType = "TABLE_QUERY",
            ActorSid = actorSid,
            TargetTable = "hr.dbo.hr_table_1",
            Decision = "DENY",
            TraceId = "trace-002",
            DetailsJson = "{\"reason\":\"Zero Trust\"}"
        });

        await _repository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            EventType = "CONSENT_REVOKED",
            ActorSid = actorSid,
            TargetTable = "sales.dbo.sales_table_1",
            Decision = "ALLOW",
            TraceId = "trace-003",
            DetailsJson = "{\"revoked_consent_id\":\"12345\"}"
        });

        // Verify valid chain
        var isValid = await _repository.VerifyAuditHashChainAsync();
        isValid.ShouldBeTrue();

        var entries = (await _repository.GetAuditLogEntriesAsync()).OrderBy(e => e.ChainSequence).ToList();
        entries.Count.ShouldBeGreaterThanOrEqualTo(3);

        // Verify that entry hashes are chained: entry 2's PrevHash == entry 1's EntryHash
        var lastEntries = entries.TakeLast(3).ToList();
        lastEntries[1].PrevHash.ShouldBe(lastEntries[0].EntryHash);
        lastEntries[2].PrevHash.ShouldBe(lastEntries[1].EntryHash);
    }

    [Fact]
    public async Task AuditLog_WhenRepositoryRestarted_MaintainsContinuousHashChainWithoutResettingToGenesis()
    {
        var actorSid = new Sid("S-1-5-21-AUDIT-RESTART");

        {
            // 1. Initial Repository Instance
            string dbName;
            using (var repo1 = GovernanceTestDatabase.CreateRepository(_epochService, out dbName))
            {
                await repo1.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    EventType = "INITIAL_EVENT_1",
                    ActorSid = actorSid,
                    TargetTable = "finance.dbo.finance_table_1",
                    Decision = "ALLOW",
                    TraceId = "trace-restart-001"
                });
                await repo1.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    EventType = "INITIAL_EVENT_2",
                    ActorSid = actorSid,
                    TargetTable = "finance.dbo.finance_table_1",
                    Decision = "ALLOW",
                    TraceId = "trace-restart-002"
                });
            }

            // 2. Second Repository Instance (simulates process restart pointing to existing DB)
            using (var repo2 = GovernanceTestDatabase.CreateRepositoryOn(dbName, _epochService))
            {
                await repo2.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    EventType = "POST_RESTART_EVENT_3",
                    ActorSid = actorSid,
                    TargetTable = "finance.dbo.finance_table_1",
                    Decision = "ALLOW",
                    TraceId = "trace-restart-003"
                });

                var entries = (await repo2.GetAuditLogEntriesAsync()).OrderBy(e => e.ChainSequence).ToList();
                entries.Count.ShouldBe(3);
                entries[2].PrevHash.ShouldBe(entries[1].EntryHash);

                var isValid = await repo2.VerifyAuditHashChainAsync();
                isValid.ShouldBeTrue("Audit hash chain must remain continuous across process restarts and not reset to GENESIS.");
            }
        }
    }

    [Fact]
    public async Task GetRelationsForTable_ReturnsConfiguredRelation()
    {
        var parentTable = new TableIdentifier("finance", "dbo", "finance_table_1");
        var relations = await _repository.GetRelationsForTableAsync(parentTable);

        relations.ShouldNotBeNull();
        relations.Count.ShouldBeGreaterThanOrEqualTo(1);

        var itemsRel = relations.FirstOrDefault(r => r.RelationName == "items");
        itemsRel.ShouldNotBeNull();
        itemsRel.ChildTableIdentifier.Domain.ShouldBe("finance");
        itemsRel.ChildTableIdentifier.TableName.ShouldBe("finance_items");
        itemsRel.JoinKeysParent.ShouldBe(["id"]);
        itemsRel.JoinKeysChild.ShouldBe(["parent_id"]);
        itemsRel.Cardinality.ShouldBe(RelationCardinality.OneToMany);
    }

    [Fact]
    public async Task AuditLog_Sha256HashChain_SurvivesRepositoryReinstantiation()
    {
        var actorSid = new Sid("S-1-5-21-PERSIST-AUDIT");

        await _repository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            EventType = "CONFIG_CHANGE",
            ActorSid = actorSid,
            TargetTable = "finance.dbo.finance_table_1",
            Decision = "ALLOW",
            TraceId = "trace-pre-reboot",
            DetailsJson = "{}"
        });

        // Simulate reboot: new repository instance on same database
        using var newRepo = GovernanceTestDatabase.CreateRepository(_epochService);

        // Record subsequent event
        await newRepo.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            EventType = "CONFIG_CHANGE",
            ActorSid = actorSid,
            TargetTable = "finance.dbo.finance_table_1",
            Decision = "ALLOW",
            TraceId = "trace-post-reboot",
            DetailsJson = "{}"
        });

        // Verification must succeed!
        var isChainValid = await newRepo.VerifyAuditHashChainAsync();
        isChainValid.ShouldBeTrue();
    }

    [Fact]
    public async Task AuditLog_WhenEntryIdIsTampered_VerificationFails()
    {
        var actorSid = new Sid("S-1-5-21-AUDIT-TAMPER");
        var originalId = Guid.NewGuid();

        await _repository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = originalId,
            EventType = "CONFIG_CHANGE",
            ActorSid = actorSid,
            TargetTable = "finance.dbo.finance_table_1",
            Decision = "ALLOW",
            TraceId = "trace-tamper-test",
            DetailsJson = "{}"
        });

        // Tamper with the Entry Id directly in the database
        GovernanceTestDatabase.ExecuteRaw(
            GovernanceTestDatabase.ConnectionStringOf(_repository),
            "UPDATE AUDIT_LOG_ENTRIES SET id = @tamperedId WHERE id = @originalId",
            bypassAppendOnlyTriggers: true,
            parameters: [("tamperedId", Guid.NewGuid().ToString()), ("originalId", originalId.ToString())]);

        // Verification must fail because Id is cryptographically bound into the hash chain
        var isChainValid = await _repository.VerifyAuditHashChainAsync();
        isChainValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Consent_RowScope_PersistsAndLoadsCorrectly()
    {
        var targetTable = new TableIdentifier("finance", "dbo", "finance_table_1");
        var depTable = new TableIdentifier("finance", "dbo", "finance_items");
        var userSid = new Sid("S-1-5-21-ADV-RLS-USER");

        var meta = await _repository.GetTableMetadataAsync(targetTable);
        meta.ShouldNotBeNull();
        string targetColumn = meta.Columns[0].ColumnName;
        string policyName = $"finance_ch_{Guid.NewGuid():N}";

        await ((Autheris.Application.VirtualFilters.IVirtualFilterRepository)_repository).ApplyAsync(
            new Autheris.Application.VirtualFilters.VirtualFilterChangeSet
            {
                SaveFilters =
                [
                    new VirtualFilter
                    {
                        TenantId = Autheris.Tests.Common.TestTenants.Default,
                        Name = policyName,
                        Source = meta.Table.SourceName,
                        KeyColumns = [$"c.{targetColumn}"],
                        Structured = new StructuredFilterDefinition
                        {
                            From = new TableIdentifier(meta.Table.SourceName, depTable.Schema, depTable.TableName),
                            FromAlias = "c",
                            Where = [new FilterCondition("c.country", FilterConditionOperator.Eq, "CH")]
                        }
                    }
                ]
            });

        var consent = new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableId = meta.Table.Id,
            TableIdentifier = targetTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            RowScope =
            [
                new GrantRowScopeEntry(
                    policyName,
                    new Dictionary<string, System.Text.Json.JsonElement>(),
                    new Dictionary<string, string> { [targetColumn] = targetColumn },
                    UncoveredPolicy.Skip)
            ]
        };

        var created = await _repository.CreateConsentAsync(consent);
        created.ShouldNotBeNull();

        var loadedList = await _repository.GetActiveConsentsForSubjectsAsync(
            new[] { userSid }, targetTable, DateTimeOffset.UtcNow, Autheris.Tests.Common.TestTenants.Default);

        var loaded = loadedList.FirstOrDefault(c => c.Id == consent.Id);
        loaded.ShouldNotBeNull();
        loaded.RowScope.Count.ShouldBe(1);
        loaded.RowScope[0].PolicyName.ShouldBe(policyName);
        loaded.RowScope[0].ColumnMap.ShouldNotBeNull();
        loaded.RowScope[0].ColumnMap![targetColumn].ShouldBe(targetColumn);
        loaded.RowScope[0].Uncovered.ShouldBe(UncoveredPolicy.Skip);
    }

    private void DeletePolicyEpoch(TableIdentifier table) =>
        GovernanceTestDatabase.ExecuteRaw(
            GovernanceTestDatabase.ConnectionStringOf(_repository),
            "DELETE FROM POLICY_EPOCHS WHERE lower(domain) = lower(@domain) AND lower(schema_name) = lower(@schema) AND lower(table_name) = lower(@table)",
            parameters: [("domain", table.Domain), ("schema", table.Schema), ("table", table.TableName)]);

    [Fact]
    public async Task IncrementTableEpoch_WhenNoRowInPolicyEpochs_InsertsInitialEpochAndIncrements()
    {
        var table = new TableIdentifier("sales", "dbo", "sales_table_1");
        var userSid = new Sid("S-1-5-21-SALES-USER");

        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        // Delete epoch row to simulate table added without initial epoch record
        DeletePolicyEpoch(table);

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // Still delete epoch to test Revoke
        DeletePolicyEpoch(table);
        var epochBefore = await _repository.GetTableEpochAsync(table);
        epochBefore.ShouldBe(1L);

        var userAuth = Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, userSid.Value),
                new System.Security.Claims.Claim("tenant_id", Autheris.Tests.Common.TestTenants.Default.Value)
            ], "test")));
        await _repository.RevokeConsentAsync(consent.Id, userSid, "Testing epoch increment", userAuth);

        var epochAfter = await _repository.GetTableEpochAsync(table);
        epochAfter.ShouldBe(2L);
    }

    [Fact]
    public async Task ConsentCache_WhenEntryEvicted_RemovesKeyFromTableIndex()
    {
        var table = new TableIdentifier("crm", "dbo", "contacts");
        var userSid = new Sid("S-1-5-21-EVICT-USER");
        var decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>());

        await _cacheService.SetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, userSid, table, decision, TimeSpan.FromMilliseconds(50));
        _cacheService.GetTrackedKeyCount(table).ShouldBe(1);

        // Accessing cache triggers expiration callback in MemoryCache, so poll through the cache until the entry has expired.
        await WaitHelper.WaitUntil(
            async () =>
            {
                _ = await _cacheService.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, userSid, table);
                return _cacheService.GetTrackedKeyCount(table) == 0;
            },
            WaitHelper.HangGuard,
            "the expired entry was never removed from the table index");

        _cacheService.GetTrackedKeyCount(table).ShouldBe(0);
    }

    [Fact]
    public async Task GetActiveConsentsForSubjects_WhenSubjectCasingDiffers_ResolvesConsentCaseInsensitively()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        // Stored with lowercase canonical Windows SID
        var storedSid = new Sid("s-1-5-21-1000-2000-12345");
        // Queried with uppercase canonical Windows SID
        var querySid = new Sid("S-1-5-21-1000-2000-12345");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = storedSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // Act: Query using querySid with different casing
        var activeConsents = await _repository.GetActiveConsentsForSubjectsAsync(new[] { querySid }, table, DateTimeOffset.UtcNow, Autheris.Tests.Common.TestTenants.Default);

        // Assert: Must resolve the consent case-insensitively
        activeConsents.Count.ShouldBe(1);
        activeConsents[0].Id.ShouldBe(consent.Id);
    }

    [Fact]
    public async Task GetAllTables_WhenPolicyEpochMissing_ReturnsCorrectDomainFromSourceName()
    {
        // Arrange: Delete policy epoch for finance_table_1
        using (var cmd = _repository.RawConnection().CreateCommand())
        {
            cmd.CommandText = "DELETE FROM POLICY_EPOCHS WHERE table_name = 'finance_table_1'";
            await cmd.ExecuteNonQueryAsync();
        }

        // Act
        var tables = await _repository.GetAllTablesAsync();

        // Assert
        var table1 = tables.FirstOrDefault(t => t.Table.TableName == "finance_table_1");
        table1.ShouldNotBeNull();
        table1.Identifier.Domain.ShouldBe("finance");
        table1.Table.SourceName.ShouldBe("finance");
    }

    [Fact]
    public async Task CreateConsentAsync_WhenFailureOccursMidway_RollsBackTransactionAtomically()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var initialEpoch = await _repository.GetTableEpochAsync(table);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = consentId,
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TX-TEST"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule
                {
                    Id = Guid.NewGuid(),
                    ConsentId = consentId,
                    TableColumnId = Guid.NewGuid(),
                    ColumnName = "amount",
                    AccessLevel = ColumnAccessLevel.Clear
                }
            },
            RowFilters = new[]
            {
                new ConsentRowFilter
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ConsentId = consentId,
                    ColumnName = "amount",
                    Operator = "=",
                    ValueType = "numeric",
                    ValueJson = "100",
                    ValueSource = "literal"
                },
                new ConsentRowFilter
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), // Duplicate primary key triggers failure
                    ConsentId = consentId,
                    ColumnName = "amount",
                    Operator = "=",
                    ValueType = "numeric",
                    ValueJson = "200",
                    ValueSource = "literal"
                }
            }
        };

        // Act & Assert
        await Should.ThrowAsync<Exception>(() => _repository.CreateConsentAsync(consent));

        // Verify that CONSENTS record was rolled back atomically
        using (var cmd = _repository.RawConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM CONSENTS WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", consentId.ToString());
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            count.ShouldBe(0);
        }

        // Verify that epoch was not incremented
        var postEpoch = await _repository.GetTableEpochAsync(table);
        postEpoch.ShouldBe(initialEpoch);
    }

    public void Dispose()
    {
        _cacheService.Dispose();
        _memoryCache.Dispose();
        _repository.Dispose();
        _eventBus.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
