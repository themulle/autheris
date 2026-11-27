namespace Autheris.Tests.Unit.Databricks.Provisioning;

using Autheris.Application.Governance.Provisioning;
using Autheris.Application.Runtime.Hosting;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.Provisioning.UcProvisioningScenario;

/// <summary>
/// Issue #1122 (drift): a missing Autheris privilege is re-applied; foreign privileges on managed tables and schemas are reported
/// once while they stand and never removed; privileges that a consent, Autheris or a configured principal accounts for are not foreign.
/// </summary>
[Trait("Category", "Security")]
public sealed class UcProvisioningDriftTests
{
    private const string Table = "main.sales.orders";
    private const string Schema = "main.sales";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-27T10:00:00Z"));
    private readonly FakeUcStore _store = new();
    private readonly FakeUcClient _client = new();
    private readonly FakeUcContext _context = new();
    private readonly UcProvisioningState _state = new();
    private readonly StaticOptionsMonitor<GatewayOptions> _options = new(Options());

    public UcProvisioningDriftTests()
    {
        _context.Tables[TableId()] = Facts();
        _client.Principals[GroupOid] = Group("analysts");
        _client.CatalogAccess.Add("analysts");
        _store.Consents.Add(Grant(_time.GetUtcNow()));
    }

    private Task RunAsync() =>
        new UcProvisioningReconciler(_options, _store, _client, _context, NullLogger<UcProvisioningReconciler>.Instance, _time, _state)
            .ExecuteAsync(new ClusterJobContext(1, null!), CancellationToken.None);

    private async Task RunAfterIntervalAsync()
    {
        _time.Advance(UcProvisioningReconciler.DriftCheckInterval + TimeSpan.FromSeconds(1));
        await RunAsync();
    }

    private int DriftAudits(string decision) =>
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantProvisioningDrift && a.Decision == decision);

    [Fact]
    public async Task Missing_autheris_privilege_is_reapplied_and_confirmed()
    {
        await RunAsync();
        _client.Set(UcSecurableType.Table, Table, "analysts").Clear();

        await RunAfterIntervalAsync();

        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeTrue();
        DriftAudits("MISSING").ShouldBe(1);
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantProvisioned && a.Decision == "APPLIED").ShouldBe(3);
        _store.Ledger.Values.ShouldAllBe(e => e.State == UcLedgerState.Applied);
    }

    [Fact]
    public async Task Drift_is_read_once_per_interval()
    {
        await RunAsync();
        _client.Calls.Clear();

        await RunAsync();
        _client.Calls.ShouldNotContain(c => c.StartsWith("get:", StringComparison.Ordinal));

        await RunAfterIntervalAsync();
        _client.Calls.Count(c => c.StartsWith("get:", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public async Task Foreign_privilege_is_reported_once_kept_and_reported_again_after_it_came_back()
    {
        await RunAsync();
        _client.Set(UcSecurableType.Table, Table, "intruders").Add("MODIFY");

        await RunAfterIntervalAsync();
        await RunAfterIntervalAsync();
        _client.Holds(UcSecurableType.Table, Table, "intruders", "MODIFY").ShouldBeTrue();
        DriftAudits("FOREIGN").ShouldBe(1);

        _client.Set(UcSecurableType.Table, Table, "intruders").Clear();
        await RunAfterIntervalAsync();
        _client.Set(UcSecurableType.Table, Table, "intruders").Add("MODIFY");
        await RunAfterIntervalAsync();

        DriftAudits("FOREIGN").ShouldBe(2);
        _store.Audit.ShouldAllBe(a => !a.DetailsJson.Contains("intruders") && !a.TargetTable.Contains("intruders"));
    }

    [Fact]
    public async Task Foreign_privilege_on_a_managed_schema_is_reported()
    {
        await RunAsync();
        _client.Set(UcSecurableType.Schema, Schema, "intruders").Add("SELECT");

        await RunAfterIntervalAsync();

        _client.Holds(UcSecurableType.Schema, Schema, "intruders", "SELECT").ShouldBeTrue();
        DriftAudits("FOREIGN").ShouldBe(1);
    }

    [Fact]
    public async Task Privileges_accounted_for_are_never_foreign()
    {
        // A privilege held before Autheris that satisfies the consent, and the provisioning principal's own privilege.
        _client.Set(UcSecurableType.Table, Table, "analysts").Add(UcPrivileges.Select);
        _client.Set(UcSecurableType.Table, Table, "prov-sp").Add("MANAGE");
        await RunAsync();

        await RunAfterIntervalAsync();

        DriftAudits("FOREIGN").ShouldBe(0);
    }

    [Fact]
    public async Task Unconfirmed_rename_is_neither_repaired_nor_reported()
    {
        await RunAsync();

        // The group now resolves to another name, and the privilege is not visible under either name.
        _client.Principals[GroupOid] = Group("analysts-eu");
        _client.Set(UcSecurableType.Table, Table, "analysts").Clear();
        _client.Calls.Clear();
        await RunAfterIntervalAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("patch:", StringComparison.Ordinal));
        DriftAudits("MISSING").ShouldBe(0);
        DriftAudits("FOREIGN").ShouldBe(0);
    }

    [Fact]
    public async Task No_drift_check_in_dry_run()
    {
        await RunAsync();
        _options.CurrentValue = Options(DatabricksProvisioningMode.DryRun);
        _client.Calls.Clear();

        await RunAfterIntervalAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("get:", StringComparison.Ordinal));
    }
}
