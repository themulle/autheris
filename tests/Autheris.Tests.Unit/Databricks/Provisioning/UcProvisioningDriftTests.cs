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
/// Issue #1122 (drift): a missing Autheris privilege is re-applied and audited as a repair; foreign privileges on managed tables are
/// reported once and removed only with DriftMode = Revert; schemas and the provisioning principal are never reverted.
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

    [Fact]
    public async Task Missing_autheris_privilege_is_reapplied_and_audited_as_repair()
    {
        await RunAsync();
        _client.Set(Autheris.Application.Governance.Provisioning.UcSecurableType.Table, Table, "analysts").Clear();

        await RunAfterIntervalAsync();

        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeTrue();
        _store.Audit.ShouldContain(a => a.EventType == AuditEventTypes.GrantProvisioningDrift && a.Decision == "REPAIRED");
        _store.Ledger.Values.ShouldAllBe(e => e.State == UcLedgerState.Applied);
    }

    [Fact]
    public async Task Drift_is_checked_at_most_once_per_interval()
    {
        await RunAsync();
        _client.Calls.Clear();

        await RunAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("get:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Foreign_privilege_is_reported_once_and_kept_in_report_mode()
    {
        await RunAsync();
        _client.Set(UcSecurableType.Table, Table, "intruders").Add("MODIFY");

        await RunAfterIntervalAsync();
        await RunAfterIntervalAsync();

        _client.Holds(UcSecurableType.Table, Table, "intruders", "MODIFY").ShouldBeTrue();
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantProvisioningDrift && a.Decision == "FOREIGN").ShouldBe(1);
        _store.Audit.ShouldAllBe(a => !a.DetailsJson.Contains("intruders"));
    }

    [Fact]
    public async Task Revert_removes_foreign_table_privileges_but_never_schema_or_own_privileges()
    {
        _options.CurrentValue = Options(drift: DatabricksDriftMode.Revert);
        await RunAsync();
        _client.Set(UcSecurableType.Table, Table, "intruders").Add("SELECT");
        _client.Set(UcSecurableType.Table, Table, "prov-sp").Add("MANAGE");
        _client.Set(UcSecurableType.Schema, Schema, "intruders").Add("USE_SCHEMA");

        await RunAfterIntervalAsync();

        _client.Holds(UcSecurableType.Table, Table, "intruders", "SELECT").ShouldBeFalse();
        _client.Holds(UcSecurableType.Table, Table, "prov-sp", "MANAGE").ShouldBeTrue();
        _client.Holds(UcSecurableType.Schema, Schema, "intruders", "USE_SCHEMA").ShouldBeTrue();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeTrue();
        _store.Audit.ShouldContain(a => a.EventType == AuditEventTypes.GrantProvisioningDrift && a.Decision == "REVERTED");
    }

    [Fact]
    public async Task No_drift_check_in_dry_run()
    {
        _options.CurrentValue = Options(DatabricksProvisioningMode.DryRun);
        await RunAfterIntervalAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("get:", StringComparison.Ordinal));
    }
}
