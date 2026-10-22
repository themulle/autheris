namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.TableCache.Governance;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedTableCacheConsentMaskTests
{
    private readonly ITableCacheCatalog _catalog = Substitute.For<ITableCacheCatalog>();
    private readonly ITableCacheStore _store = Substitute.For<ITableCacheStore>();
    private readonly IConsentResolutionService _consentService = Substitute.For<IConsentResolutionService>();
    private readonly IColumnMaskingProvider _maskingProvider = Substitute.For<IColumnMaskingProvider>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly TableCacheOptions _options = new() { Enabled = true, PersistentDirectory = "/tmp/cache" };

    private static readonly TableIdentifier TestTable = new("crm", "public", "customers");

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
                maskingProvider: _maskingProvider);
        });

        Should.Throw<ArgumentNullException>(() =>
        {
            _ = new GovernedTableCacheExecutionService(
                _catalog,
                _store,
                Options.Create(_options),
                _auditRepo,
                consentService: _consentService,
                maskingProvider: null!);
        });
    }

    [Fact]
    public async Task PrincipalWithoutSid_Denied()
    {
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, _consentService, _maskingProvider);

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
            _catalog, _store, Options.Create(_options), _auditRepo, _consentService, _maskingProvider);

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
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, _consentService, _maskingProvider);

        var userSid = "S-1-5-21-1";
        var groupSid = "S-1-5-21-500";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, userSid),
            new Claim(ClaimTypes.GroupSid, groupSid)
        ], "Auth"));

        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        _consentService.ResolveAccess(
                Arg.Is<Sid>(s => s.Value == userSid),
                Arg.Is<IReadOnlySet<Sid>>(g => g.Contains(new Sid(groupSid))),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(),
                Arg.Any<IReadOnlySet<string>?>())
            .Returns(TableAccessDecision.Allowed(TestTable, new Dictionary<string, ColumnAccessLevel>()));

        // Setup store mock connection
        var dbConn = Substitute.For<DbConnection>();
        var dbCmd = Substitute.For<DbCommand>();
        var dbReader = Substitute.For<DbDataReader>();

        dbConn.CreateCommand().Returns(dbCmd);
        dbCmd.ExecuteReaderAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbReader));
        dbReader.FieldCount.Returns(1);
        dbReader.GetName(0).Returns("id");
        dbReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        _store.LeaseReaderConnectionAsync(TestTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));
        var entryMeta = new TableCacheEntryMetadata(TestTable, TableCacheStorageTier.DuckDbDisk, "/tmp", "v_test", 1, DateTimeOffset.UtcNow, null, 0, 0, null, true, null, [], null, TableCacheOfflinePolicy.ServeStaleWithWarning);
        _catalog.TryGet(TestTable, out Arg.Any<TableCacheEntryMetadata?>()).Returns(x => { x[1] = entryMeta; return true; });

        var result = await service.ExecuteGovernedQueryAsync(TestTable, "SELECT id FROM customers", caller);

        result.ShouldNotBeNull();
        _consentService.Received(1).ResolveAccess(
            Arg.Is<Sid>(s => s.Value == userSid),
            Arg.Is<IReadOnlySet<Sid>>(g => g.Contains(new Sid(groupSid))),
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<IReadOnlyList<Consent>>(),
            Arg.Any<DatabaseDialect>(),
            Arg.Any<IReadOnlySet<Sid>?>(),
            Arg.Any<IReadOnlySet<string>?>());
    }

    [Fact]
    public async Task UnknownMaskType_ValueIsNulled()
    {
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, _consentService, _maskingProvider);

        var userSid = "S-1-5-21-1";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid)], "Auth"));
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        _consentService.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(TableAccessDecision.Allowed(TestTable, new Dictionary<string, ColumnAccessLevel>()));

        // Masking provider returns raw value when unknown rule type is encountered
        var unknownRule = new MaskingRule { RuleType = "UNKNOWN_CUSTOM_TYPE" };
        _maskingProvider.MaskValue("secret_col", "RAW_SECRET_DATA", unknownRule).Returns("RAW_SECRET_DATA");

        var dbConn = Substitute.For<DbConnection>();
        var dbCmd = Substitute.For<DbCommand>();
        var dbReader = Substitute.For<DbDataReader>();

        dbConn.CreateCommand().Returns(dbCmd);
        dbCmd.ExecuteReaderAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbReader));
        dbReader.FieldCount.Returns(1);
        dbReader.GetName(0).Returns("secret_col");
        dbReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        dbReader.IsDBNull(0).Returns(false);
        dbReader.GetValue(0).Returns("RAW_SECRET_DATA");

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
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, _consentService, _maskingProvider);

        var userSid = "S-1-5-21-1";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid)], "Auth"));
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        _consentService.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(TableAccessDecision.Allowed(TestTable, new Dictionary<string, ColumnAccessLevel>()));

        var hashRule = new MaskingRule { RuleType = "HASH", HmacKeyId = "vault-key-1" };
        _maskingProvider.MaskValue("ssn", "123-45-6789", hashRule).Returns("KEYED_HMAC_RESULT");

        var dbConn = Substitute.For<DbConnection>();
        var dbCmd = Substitute.For<DbCommand>();
        var dbReader = Substitute.For<DbDataReader>();

        dbConn.CreateCommand().Returns(dbCmd);
        dbCmd.ExecuteReaderAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbReader));
        dbReader.FieldCount.Returns(1);
        dbReader.GetName(0).Returns("ssn");
        dbReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        dbReader.IsDBNull(0).Returns(false);
        dbReader.GetValue(0).Returns("123-45-6789");

        _store.LeaseReaderConnectionAsync(TestTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));
        var entryMeta = new TableCacheEntryMetadata(TestTable, TableCacheStorageTier.DuckDbDisk, "/tmp", "v_test", 1, DateTimeOffset.UtcNow, null, 1, 100, null, true, null, [], null, TableCacheOfflinePolicy.ServeStaleWithWarning);
        _catalog.TryGet(TestTable, out Arg.Any<TableCacheEntryMetadata?>()).Returns(x => { x[1] = entryMeta; return true; });

        var masks = new Dictionary<string, MaskingRule> { ["ssn"] = hashRule };
        var result = await service.ExecuteGovernedQueryAsync(TestTable, "SELECT ssn FROM customers", caller, columnMasks: masks);

        result.Rows[0][0].ShouldBe("KEYED_HMAC_RESULT");
        _maskingProvider.Received(1).MaskValue("ssn", "123-45-6789", hashRule);
    }

    [Fact]
    public async Task MaskLookup_IsCaseInsensitive()
    {
        var service = new GovernedTableCacheExecutionService(
            _catalog, _store, Options.Create(_options), _auditRepo, _consentService, _maskingProvider);

        var userSid = "S-1-5-21-1";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid)], "Auth"));
        var caller = new TableCacheCallerContext(principal, new TenantId("tenant-1"));

        _consentService.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(TableAccessDecision.Allowed(TestTable, new Dictionary<string, ColumnAccessLevel>()));

        var rule = new MaskingRule { RuleType = "REDACT" };
        _maskingProvider.MaskValue("EMAIL", "user@corp.com", rule).Returns("***@***.***");

        var dbConn = Substitute.For<DbConnection>();
        var dbCmd = Substitute.For<DbCommand>();
        var dbReader = Substitute.For<DbDataReader>();

        dbConn.CreateCommand().Returns(dbCmd);
        dbCmd.ExecuteReaderAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbReader));
        dbReader.FieldCount.Returns(1);
        dbReader.GetName(0).Returns("EMAIL"); // UPPERCASE in DB
        dbReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        dbReader.IsDBNull(0).Returns(false);
        dbReader.GetValue(0).Returns("user@corp.com");

        _store.LeaseReaderConnectionAsync(TestTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));
        var entryMeta = new TableCacheEntryMetadata(TestTable, TableCacheStorageTier.DuckDbDisk, "/tmp", "v_test", 1, DateTimeOffset.UtcNow, null, 1, 100, null, true, null, [], null, TableCacheOfflinePolicy.ServeStaleWithWarning);
        _catalog.TryGet(TestTable, out Arg.Any<TableCacheEntryMetadata?>()).Returns(x => { x[1] = entryMeta; return true; });

        // lowercase in masks map
        var masks = new Dictionary<string, MaskingRule> { ["email"] = rule };
        var result = await service.ExecuteGovernedQueryAsync(TestTable, "SELECT EMAIL FROM customers", caller, columnMasks: masks);

        result.Rows[0][0].ShouldBe("***@***.***");
    }
}
