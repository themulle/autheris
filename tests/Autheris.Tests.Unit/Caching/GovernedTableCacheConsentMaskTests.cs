namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State.Caching.TableCache.Governance;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedTableCacheConsentMaskTests
{
    private readonly ITableCacheCatalog _catalog = Substitute.For<ITableCacheCatalog>();
    private readonly ITableCacheStore _store = Substitute.For<ITableCacheStore>();
    private readonly InMemoryAuditLogRepository _auditRepo = new();
    private readonly TableCacheOptions _options = new() { Enabled = true, PersistentDirectory = "/tmp/cache" };

    private static readonly TableIdentifier TestTable = new("crm", "public", "customers");

    private static IConsentResolutionService AllowUnconstrainedResolution() =>
        StubConsentResolutionService.AllowUnconstrained();

    [Fact]
    public void Constructor_WithoutConsentService_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
        {
            _ = new GovernedTableCacheExecutionService(
                _catalog,
                _store,
                Options.Create(_options),
                _auditRepo,
                consentService: null!,
                maskingProvider: StubColumnMaskingProvider.Asterisks);
        });

        Should.Throw<ArgumentNullException>(() =>
        {
            _ = new GovernedTableCacheExecutionService(
                _catalog,
                _store,
                Options.Create(_options),
                _auditRepo,
                consentService: AllowUnconstrainedResolution(),
                maskingProvider: null!);
        });
    }

    [Fact]
    public async Task PrincipalWithoutSid_Denied()
    {
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, AllowUnconstrainedResolution(), StubColumnMaskingProvider.Asterisks);

        var principal = new ClaimsPrincipal(new ClaimsIdentity()); // No claims at all
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ExecuteGovernedQueryAsync(TestTable, "SELECT * FROM customers", caller);
        });

        ex.Message.ShouldContain(GovernedTableCacheExecutionService.ConsentRequiredCode);
    }

    [Fact]
    public async Task NameClaimEqualToGrantedSid_WithoutSidClaim_Denied()
    {
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, AllowUnconstrainedResolution(), StubColumnMaskingProvider.Asterisks);

        // Only ClaimTypes.Name, NO ClaimTypes.PrimarySid
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "S-1-5-21-999")], "Auth"));
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ExecuteGovernedQueryAsync(TestTable, "SELECT * FROM customers", caller);
        });

        ex.Message.ShouldContain(GovernedTableCacheExecutionService.ConsentRequiredCode);
    }

    [Fact]
    public async Task GroupConsent_ViaGroupSid_Allowed()
    {
        var seenCalls = new List<(Sid User, IReadOnlySet<Sid> Groups)>();
        var consentService = new RecordingResolution((user, groups) =>
        {
            seenCalls.Add((user, groups));
            return TableAccessDecision.Allowed(TestTable, new Dictionary<string, ColumnAccessLevel>());
        });
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, consentService, StubColumnMaskingProvider.Asterisks);

        var userSid = "S-1-5-21-1";
        var groupSid = "S-1-5-21-500";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, userSid),
            Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim(groupSid)
        ], "Auth"));

        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        // Setup store connection returning 0 rows
        DbConnection dbConn = new CallbackDbConnection((_, _) => new StubDbDataReader(["id"], []));
        _store.LeaseReaderConnectionAsync(TestTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));
        var entryMeta = new TableCacheEntryMetadata(TestTable, TableCacheStorageTier.DuckDbDisk, "/tmp", "v_test", 1, DateTimeOffset.UtcNow, null, 0, 0, null, true, null, [], null, TableCacheOfflinePolicy.ServeStaleWithWarning);
        _catalog.TryGet(TestTable, out Arg.Any<TableCacheEntryMetadata?>()).Returns(x => { x[1] = entryMeta; return true; });

        var result = await service.ExecuteGovernedQueryAsync(TestTable, "SELECT id FROM customers", caller);

        result.ShouldNotBeNull();
        seenCalls.ShouldHaveSingleItem();
        seenCalls[0].User.Value.ShouldBe(userSid);
        seenCalls[0].Groups.ShouldContain(new Sid(groupSid));
    }

    [Fact]
    public async Task UnknownMaskType_ValueIsNulled()
    {
        // Masking provider returns raw value when unknown rule type is encountered
        var masking = new StubColumnMaskingProvider((_, raw, _) => raw);
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, AllowUnconstrainedResolution(), masking);

        var userSid = "S-1-5-21-1";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid)], "Auth"));
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        var unknownRule = new MaskingRule { RuleType = "UNKNOWN_CUSTOM_TYPE" };

        DbConnection dbConn = new CallbackDbConnection((_, _) => new StubDbDataReader(["secret_col"], [["RAW_SECRET_DATA"]]));
        _store.LeaseReaderConnectionAsync(TestTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));
        var entryMeta = new TableCacheEntryMetadata(TestTable, TableCacheStorageTier.DuckDbDisk, "/tmp", "v_test", 1, DateTimeOffset.UtcNow, null, 1, 100, null, true, null, [], null, TableCacheOfflinePolicy.ServeStaleWithWarning);
        _catalog.TryGet(TestTable, out Arg.Any<TableCacheEntryMetadata?>()).Returns(x => { x[1] = entryMeta; return true; });

        var masks = new Dictionary<string, MaskingRule> { ["secret_col"] = unknownRule };
        var result = await service.ExecuteGovernedQueryAsync(TestTable, "SELECT secret_col FROM customers", caller, columnMasks: masks);

        result.Rows.Count.ShouldBe(1);
        result.Rows[0][0].ShouldBeNull(); // Value must be replaced with null fail-closed!
    }

    [Fact]
    public async Task HashMask_UsesKeyedProvider_NotPlainSha256()
    {
        var hashRule = new MaskingRule { RuleType = "HASH", HmacKeyId = "vault-key-1" };
        var calls = new List<(string Column, object? Raw, MaskingRule Rule)>();
        var masking = new StubColumnMaskingProvider((col, raw, rule) =>
        {
            calls.Add((col, raw, rule));
            return "KEYED_HMAC_RESULT";
        });
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, AllowUnconstrainedResolution(), masking);

        var userSid = "S-1-5-21-1";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid)], "Auth"));
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        DbConnection dbConn = new CallbackDbConnection((_, _) => new StubDbDataReader(["ssn"], [["123-45-6789"]]));
        _store.LeaseReaderConnectionAsync(TestTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));
        var entryMeta = new TableCacheEntryMetadata(TestTable, TableCacheStorageTier.DuckDbDisk, "/tmp", "v_test", 1, DateTimeOffset.UtcNow, null, 1, 100, null, true, null, [], null, TableCacheOfflinePolicy.ServeStaleWithWarning);
        _catalog.TryGet(TestTable, out Arg.Any<TableCacheEntryMetadata?>()).Returns(x => { x[1] = entryMeta; return true; });

        var masks = new Dictionary<string, MaskingRule> { ["ssn"] = hashRule };
        var result = await service.ExecuteGovernedQueryAsync(TestTable, "SELECT ssn FROM customers", caller, columnMasks: masks);

        result.Rows[0][0].ShouldBe("KEYED_HMAC_RESULT");
        calls.ShouldBe([("ssn", "123-45-6789", hashRule)]);
    }

    [Fact]
    public async Task MaskLookup_IsCaseInsensitive()
    {
        var masking = new StubColumnMaskingProvider("***@***.***");
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, AllowUnconstrainedResolution(), masking);

        var userSid = "S-1-5-21-1";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid)], "Auth"));
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        var rule = new MaskingRule { RuleType = "REDACT" };

        DbConnection dbConn = new CallbackDbConnection((_, _) => new StubDbDataReader(["EMAIL"], [["user@corp.com"]]));
        _store.LeaseReaderConnectionAsync(TestTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));
        var entryMeta = new TableCacheEntryMetadata(TestTable, TableCacheStorageTier.DuckDbDisk, "/tmp", "v_test", 1, DateTimeOffset.UtcNow, null, 1, 100, null, true, null, [], null, TableCacheOfflinePolicy.ServeStaleWithWarning);
        _catalog.TryGet(TestTable, out Arg.Any<TableCacheEntryMetadata?>()).Returns(x => { x[1] = entryMeta; return true; });

        // lowercase in masks map
        var masks = new Dictionary<string, MaskingRule> { ["email"] = rule };
        var result = await service.ExecuteGovernedQueryAsync(TestTable, "SELECT EMAIL FROM customers", caller, columnMasks: masks);

        result.Rows[0][0].ShouldBe("***@***.***");
    }

    private sealed class RecordingResolution(Func<Sid, IReadOnlySet<Sid>, TableAccessDecision> resolve) : IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(
            Sid userSid, IReadOnlySet<Sid> subjectGroupSids, IReadOnlySet<string> userRoles, TableIdentifier table,
            IReadOnlyList<Consent> activeConsents, DatabaseDialect dialect = DatabaseDialect.SqlServer,
            IReadOnlySet<Sid>? allUserSids = null, IReadOnlySet<string>? callerScopes = null)
            => resolve(userSid, subjectGroupSids);
    }
}
