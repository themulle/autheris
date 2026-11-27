namespace Autheris.Tests.Unit.Databricks.Provisioning;

using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>In-memory provisioning store; ledger and feedback writes are recorded with their audit entries.</summary>
internal sealed class FakeUcStore : IUcProvisioningStore
{
    public List<Consent> Consents { get; } = [];
    public Dictionary<(string Source, UcPrivilegeKey Key), UcLedgerEntry> Ledger { get; } = [];
    public Dictionary<(TenantId Tenant, Guid Consent), UcGrantFeedback> Feedback { get; } = [];
    public List<AuditLogEntry> Audit { get; } = [];
    public List<string> Writes { get; } = [];

    /// <summary>Runs before every consent read (to simulate a change that commits between two reads of one cycle).</summary>
    public Action<int>? BeforeList { get; set; }

    private int _lists;

    public Task<IReadOnlyList<Consent>> ListTableConsentsAsync(TenantId tenant, string dataSource, DateTimeOffset now, CancellationToken ct)
    {
        BeforeList?.Invoke(++_lists);
        return ListNow(tenant, dataSource);
    }

    private Task<IReadOnlyList<Consent>> ListNow(TenantId tenant, string dataSource) =>
        Task.FromResult<IReadOnlyList<Consent>>(Consents
            .Where(c => c.TenantId == tenant && !c.IsRevoked && !c.IsPatternGrant &&
                        string.Equals(c.TableIdentifier.Domain, dataSource, StringComparison.OrdinalIgnoreCase))
            .ToList());

    public Task<IReadOnlyList<UcLedgerEntry>> ListLedgerAsync(string dataSource, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UcLedgerEntry>>(Ledger.Where(kv => kv.Key.Source == dataSource).Select(kv => kv.Value).ToList());

    public Task<IReadOnlyList<string>> ListLedgerDataSourcesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Ledger.Keys.Select(k => k.Source).Distinct().ToList());

    public long LastToken { get; private set; }

    public Task SaveLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct)
    {
        LastToken = fencingToken;
        foreach (var entry in entries)
        {
            Ledger[(entry.DataSource, entry.Key)] = Ledger.TryGetValue((entry.DataSource, entry.Key), out var old) ? entry with { Id = old.Id } : entry;
            Writes.Add("save:" + entry.State + ":" + entry.FullName);
        }

        Audit.AddRange(audit);
        return Task.CompletedTask;
    }

    public Task DeleteLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct)
    {
        LastToken = fencingToken;
        foreach (var entry in entries)
        {
            Ledger.Remove((entry.DataSource, entry.Key));
            Writes.Add("delete:" + entry.FullName);
        }

        Audit.AddRange(audit);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UcGrantFeedback>> ListFeedbackAsync(TenantId tenant, string dataSource, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UcGrantFeedback>>(Feedback.Values.Where(f => f.Tenant == tenant && f.DataSource == dataSource).ToList());

    public Task<IReadOnlyDictionary<Guid, UcGrantFeedback>> GetFeedbackAsync(TenantId tenant, IReadOnlyCollection<Guid> consentIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, UcGrantFeedback>>(Feedback.Values
            .Where(f => f.Tenant == tenant && consentIds.Contains(f.ConsentId))
            .ToDictionary(f => f.ConsentId));

    public Task SaveFeedbackAsync(TenantId tenant, string dataSource, long fencingToken, IReadOnlyList<UcGrantFeedback> rows, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct)
    {
        LastToken = fencingToken;
        foreach (var row in rows)
        {
            Feedback[(tenant, row.ConsentId)] = row;
        }

        Audit.AddRange(audit);
        return Task.CompletedTask;
    }

    public UcGrantFeedback FeedbackOf(Consent consent) => Feedback[(consent.TenantId, consent.Id)];
}

/// <summary>An in-memory Unity Catalog: direct privileges per securable and principal, SCIM principals and effective catalog access.</summary>
internal sealed class FakeUcClient : IUnityCatalogPermissionsClient
{
    public Dictionary<(UcSecurableType, string), Dictionary<string, HashSet<string>>> Grants { get; } = [];
    public Dictionary<string, UcPrincipalResolution> Principals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CatalogAccess { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];

    /// <summary>When set, every update fails with this code.</summary>
    public string? FailUpdates { get; set; }

    /// <summary>When set, updates succeed but change nothing (the read then misses the privilege).</summary>
    public bool IgnoreUpdates { get; set; }

    public Task<IReadOnlyList<UcPrivilegeAssignment>> GetPermissionsAsync(string dataSource, UcSecurableType type, string fullName, CancellationToken ct)
    {
        Calls.Add("get:" + fullName);
        return Task.FromResult(Read(type, fullName));
    }

    public Task<IReadOnlyList<UcPrivilegeAssignment>> UpdatePermissionsAsync(
        string dataSource, UcSecurableType type, string fullName, IReadOnlyList<UcPermissionChange> changes, CancellationToken ct)
    {
        Calls.Add("patch:" + fullName + ":" + string.Join(",", changes.Select(c => c.Principal + "+" + string.Join("|", c.Add) + "-" + string.Join("|", c.Remove))));
        if (FailUpdates is { } code)
        {
            throw new UcProvisioningException(code);
        }

        if (!IgnoreUpdates)
        {
            foreach (var change in changes)
            {
                var set = Set(type, fullName, change.Principal);
                set.UnionWith(change.Add);
                set.ExceptWith(change.Remove);
            }
        }

        return Task.FromResult(Read(type, fullName));
    }

    public Task<bool> HasEffectivePrivilegeAsync(
        string dataSource, UcSecurableType type, string fullName, string principal, IReadOnlyCollection<string> privileges, CancellationToken ct)
    {
        Calls.Add("effective:" + fullName + ":" + principal);
        return Task.FromResult(CatalogAccess.Contains(principal));
    }

    public Task<UcPrincipalResolution> ResolvePrincipalAsync(string dataSource, GranteeType type, string externalId, CancellationToken ct)
    {
        Calls.Add("resolve:" + externalId);
        return Task.FromResult(Principals.TryGetValue(externalId, out var r) ? r : UcPrincipalResolution.NotFound);
    }

    public HashSet<string> Set(UcSecurableType type, string fullName, string principal)
    {
        if (!Grants.TryGetValue((type, fullName), out var byPrincipal))
        {
            Grants[(type, fullName)] = byPrincipal = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        }

        if (!byPrincipal.TryGetValue(principal, out var set))
        {
            byPrincipal[principal] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        return set;
    }

    public bool Holds(UcSecurableType type, string fullName, string principal, string privilege) =>
        Grants.TryGetValue((type, fullName), out var byPrincipal) && byPrincipal.TryGetValue(principal, out var set) && set.Contains(privilege);

    private IReadOnlyList<UcPrivilegeAssignment> Read(UcSecurableType type, string fullName) =>
        Grants.TryGetValue((type, fullName), out var byPrincipal)
            ? byPrincipal.Where(kv => kv.Value.Count > 0).Select(kv => new UcPrivilegeAssignment(kv.Key, kv.Value.ToList())).ToList()
            : [];
}

/// <summary>Table facts per identifier; every table is Internal with known columns unless set otherwise.</summary>
internal sealed class FakeUcContext : IUcTableContextSource
{
    public Dictionary<TableIdentifier, UcTableFacts> Tables { get; } = [];
    public List<Consent> PatternConsents { get; } = [];
    public bool VirtualFilters { get; set; }
    public Exception? Fail { get; set; }

    public Task<UcTableFacts> GetTableAsync(TenantId tenant, TableIdentifier table, CancellationToken ct) =>
        Fail is { } ex ? Task.FromException<UcTableFacts>(ex)
        : Task.FromResult(Tables.TryGetValue(table, out var facts) ? facts : new UcTableFacts(null, null, false));

    public Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(TenantId tenant, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Consent>>(PatternConsents);

    public Task<bool> HasMandatoryRowFiltersAsync(TenantId tenant, CancellationToken ct) => Task.FromResult(VirtualFilters);
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Builders shared by the reconciler tests: one data source <c>dbx</c>, catalog <c>main</c>, allowed schema <c>main.sales</c>.</summary>
internal static class UcProvisioningScenario
{
    public const string Source = "dbx";
    public static readonly TenantId Tenant = new("acme");
    public const string GroupOid = "8b0c4b5e-6f0e-4a63-9a5e-1b2a4f9d7c11";
    public const string OtherOid = "0f9d7c11-1b2a-4f9d-9a5e-8b0c4b5e6f0e";

    public static GatewayOptions Options(DatabricksProvisioningMode mode = DatabricksProvisioningMode.Enforce) => new()
    {
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [Source] = new()
                {
                    Provider = "databricks",
                    Databricks = new DatabricksDataSourceOptions
                    {
                        Host = DatabricksTestSupport.Host,
                        WarehouseId = "wh-1",
                        Catalog = "main",
                        Provisioning = new DatabricksProvisioningOptions
                        {
                            Mode = mode,
                            Tenant = "acme",
                            AllowedSchemas = ["main.sales"],
                            Auth = new DatabricksAuthOptions { ClientId = "prov-sp", ClientSecretRef = "kv://prov" }
                        }
                    }
                }
            }
        }
    };

    public static TableIdentifier TableId(string table = "orders") => new(Source, "sales", table);

    public static UcTableFacts Facts(string table = "orders")
    {
        var metadata = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), SourceType = "Databricks", SourceName = Source, SchemaName = "sales", TableName = table },
            Identifier = TableId(table),
            Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "amount" }]
        };
        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), null, ProtectionClass.Internal, ProtectionClass.Internal, null,
            new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = new(ProtectionClass.Internal, null),
                ["amount"] = new(ProtectionClass.Internal, null)
            },
            CatalogVisibilityMode.OnRequest);
        return new UcTableFacts(metadata, protection, false);
    }

    public static Consent Grant(
        DateTimeOffset now,
        string table = "orders",
        string grantee = GroupOid,
        ConsentEffect effect = ConsentEffect.Allow,
        TimeSpan? validFor = null) => new()
        {
            TableIdentifier = TableId(table),
            DataSource = Source,
            Effect = effect,
            TenantId = Tenant,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(grantee),
            GrantKind = PermissionGrantKind.Data,
            ValidFrom = now.AddDays(-1),
            ValidTo = now + (validFor ?? TimeSpan.FromDays(30))
        };

    public static UcPrincipalResolution Group(string name, string oid = GroupOid) =>
        UcPrincipalResolution.Resolved(new UcPrincipal(GranteeType.Group, name, oid));
}
