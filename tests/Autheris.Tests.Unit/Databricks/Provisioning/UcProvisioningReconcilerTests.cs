namespace Autheris.Tests.Unit.Databricks.Provisioning;

using Autheris.Application.Governance.Provisioning;
using Autheris.Application.Runtime.Hosting;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.Provisioning.UcProvisioningScenario;

/// <summary>
/// Issue #1122: the level-triggered reconciler. Revokes run before grants and are never dropped; a privilege is recorded in the
/// ledger before it is added; "provisioned" is reported only after a confirming read; planning failures change nothing.
/// </summary>
[Trait("Category", "Security")]
public sealed class UcProvisioningReconcilerTests
{
    private const string Table = "main.sales.orders";
    private const string Schema = "main.sales";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-27T10:00:00Z"));
    private readonly FakeUcStore _store = new();
    private readonly FakeUcClient _client = new();
    private readonly FakeUcContext _context = new();
    private readonly StaticOptionsMonitor<GatewayOptions> _options = new(Options());
    private readonly UcProvisioningState _state = new();

    public UcProvisioningReconcilerTests()
    {
        _context.Tables[TableId()] = Facts();
        _context.Tables[TableId("customers")] = Facts("customers");
        _client.Principals[GroupOid] = Group("analysts");
        _client.CatalogAccess.Add("analysts");
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private Task RunAsync(long fencingToken = 1) =>
        new UcProvisioningReconciler(_options, _store, _client, _context, NullLogger<UcProvisioningReconciler>.Instance, _time, _state)
            .ExecuteAsync(new ClusterJobContext(fencingToken, null!), CancellationToken.None);

    [Fact]
    public async Task Approved_consent_is_granted_recorded_verified_and_reported_provisioned()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);

        await RunAsync();

        _client.Holds(UcSecurableType.Schema, Schema, "analysts", UcPrivileges.UseSchema).ShouldBeTrue();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeTrue();
        _store.Ledger.Values.ShouldAllBe(e => e.State == UcLedgerState.Applied && e.VerifiedAt == Now);
        _store.Ledger.Count.ShouldBe(2);
        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Provisioned);
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantProvisioned).ShouldBe(2);
        _store.Audit.ShouldAllBe(a => a.TenantId == Tenant && a.DetailsJson.Contains(consent.Id.ToString()));

        // The intent row is written before the add: the first ledger write of each privilege is "Pending".
        _store.Writes.First(w => w.EndsWith(Table, StringComparison.Ordinal)).ShouldBe("save:Pending:" + Table);
        _client.Calls.FindIndex(c => c.StartsWith("patch:" + Schema + ":", StringComparison.Ordinal))
            .ShouldBeLessThan(_client.Calls.FindIndex(c => c.StartsWith("patch:" + Table, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Provisioned_and_failed_transitions_are_announced_once()
    {
        _client.CatalogAccess.Clear();
        var consent = Grant(Now);
        _store.Consents.Add(consent);

        await RunAsync();
        await RunAsync();
        _client.CatalogAccess.Add("analysts");
        await RunAsync();
        await RunAsync();

        _store.Events.Select(e => (e.ConsentId, e.Provisioned)).ShouldBe([(consent.Id, false), (consent.Id, true)]);
        _store.Events.ShouldAllBe(e => e.Table.Equals(consent.TableIdentifier));
    }

    [Fact]
    public async Task Second_cycle_without_changes_calls_nothing_in_unity_catalog()
    {
        _store.Consents.Add(Grant(Now));
        await RunAsync();
        _client.Calls.Clear();
        _store.Writes.Clear();

        await RunAsync();

        _client.Calls.ShouldAllBe(c => c.StartsWith("resolve:", StringComparison.Ordinal));
        _store.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shared_privilege_is_revoked_only_when_no_consent_wants_it()
    {
        var first = Grant(Now);
        var second = Grant(Now);
        _store.Consents.AddRange([first, second]);
        await RunAsync();

        _store.Consents.Remove(first);
        await RunAsync();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeTrue();
        _store.FeedbackOf(first).State.ShouldBe(UcGrantState.Revoked);
        _store.FeedbackOf(second).State.ShouldBe(UcGrantState.Provisioned);

        _store.Consents.Remove(second);
        await RunAsync();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeFalse();
        _client.Holds(UcSecurableType.Schema, Schema, "analysts", UcPrivileges.UseSchema).ShouldBeFalse();
        _store.Ledger.ShouldBeEmpty();
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantDeprovisioned).ShouldBe(2);
    }

    [Fact]
    public async Task Renewal_keeps_the_privileges_without_any_change_call()
    {
        var original = Grant(Now);
        _store.Consents.Add(original);
        await RunAsync();
        _client.Calls.Clear();

        var renewed = Grant(Now, validFor: TimeSpan.FromDays(90));
        _store.Consents.Remove(original);
        _store.Consents.Add(renewed);
        await RunAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("patch:", StringComparison.Ordinal));
        _store.FeedbackOf(renewed).State.ShouldBe(UcGrantState.Provisioned);
        _store.FeedbackOf(original).State.ShouldBe(UcGrantState.Revoked);
    }

    [Fact]
    public async Task Revokes_wait_while_the_consent_set_changes_between_two_reads()
    {
        var original = Grant(Now);
        _store.Consents.Add(original);
        await RunAsync();

        // The first read of the cycle misses both consents of a renewal; the second read sees the new one.
        var renewed = Grant(Now, validFor: TimeSpan.FromDays(90));
        _store.Consents.Remove(original);
        var reads = 0;
        _store.BeforeList = _ =>
        {
            if (++reads == 2)
            {
                _store.Consents.Add(renewed);
            }
        };
        _client.Calls.Clear();

        await RunAsync();

        reads.ShouldBe(2);
        _client.Calls.ShouldNotContain(c => c.StartsWith("patch:", StringComparison.Ordinal));
        _store.Ledger.Count.ShouldBe(2);

        _store.BeforeList = null;
        await RunAsync();
        _client.Calls.ShouldNotContain(c => c.Contains("-SELECT", StringComparison.Ordinal));
        _store.FeedbackOf(renewed).State.ShouldBe(UcGrantState.Provisioned);
    }

    [Fact]
    public async Task Every_write_carries_the_cycle_fencing_token()
    {
        _store.Consents.Add(Grant(Now));

        await RunAsync(fencingToken: 42);

        _store.LastToken.ShouldBe(42);
    }

    [Fact]
    public async Task Expired_consent_is_revoked_in_the_next_cycle()
    {
        var consent = Grant(Now, validFor: TimeSpan.FromMinutes(1));
        _store.Consents.Add(consent);
        await RunAsync();

        _time.Advance(TimeSpan.FromMinutes(2));
        await RunAsync();

        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeFalse();
        _store.Ledger.ShouldBeEmpty();
        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Revoked);
    }

    [Fact]
    public async Task Revoke_during_an_outage_stays_pending_becomes_overdue_and_completes_later()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();
        _store.Consents.Remove(consent);
        _client.FailUpdates = UcErrorCodes.Unavailable;

        for (var cycle = 0; cycle < UcProvisioningReconciler.OverdueRevokeAttempts; cycle++)
        {
            await RunAsync();
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        _store.Ledger.Values.ShouldAllBe(e => e.State == UcLedgerState.RevokePending && e.LastErrorCode == UcErrorCodes.Unavailable);
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantProvisioningFailed && a.Decision == "REVOKE_OVERDUE").ShouldBe(2);
        UcProvisioningMetrics.OverdueRevokes.ShouldBe(2);

        _client.FailUpdates = null;
        await RunAsync();
        _store.Ledger.ShouldBeEmpty();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_add_keeps_the_intent_row_so_the_privilege_stays_tracked()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        _client.FailUpdates = UcErrorCodes.Unavailable;

        await RunAsync();

        _store.Ledger.Count.ShouldBe(2);
        _store.Ledger.Values.ShouldAllBe(e => e.State == UcLedgerState.Pending && e.Attempts == 1 && e.NextAttemptAt > Now);
        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Pending);
        _store.FeedbackOf(consent).ReasonCode.ShouldBe(UcErrorCodes.Unavailable);

        // The consent ends during the outage: the tracked rows are revoked once Unity Catalog answers again.
        _store.Consents.Remove(consent);
        _client.FailUpdates = null;
        await RunAsync();
        _store.Ledger.ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_that_never_shows_up_fails_after_the_maximum_attempts_and_is_audited_once()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        _client.IgnoreUpdates = true;

        for (var cycle = 0; cycle < UcProvisioningReconciler.MaxGrantAttempts + 2; cycle++)
        {
            await RunAsync();
            _time.Advance(TimeSpan.FromHours(2));
        }

        _store.Ledger.Values.ShouldAllBe(e => e.State == UcLedgerState.Failed);
        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Failed);
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantProvisioningFailed && a.Decision == "FAILED").ShouldBe(2);
    }

    [Fact]
    public async Task Dry_run_changes_nothing_and_reports_the_plan()
    {
        _options.CurrentValue = Options(DatabricksProvisioningMode.DryRun);
        var consent = Grant(Now);
        _store.Consents.Add(consent);

        await RunAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("patch:", StringComparison.Ordinal));
        _store.Ledger.ShouldBeEmpty();
        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Pending);
        _store.FeedbackOf(consent).ReasonCode.ShouldBe(UcProvisioningReconciler.DryRunReason);
    }

    [Fact]
    public async Task Mode_off_revokes_every_applied_privilege()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();

        _options.CurrentValue = Options(DatabricksProvisioningMode.Off);
        await RunAsync();

        _store.Ledger.ShouldBeEmpty();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeFalse();
        _store.FeedbackOf(consent).ReasonCode.ShouldBe(UcProvisioningReconciler.ProvisioningOffReason);
    }

    [Fact]
    public async Task Privilege_held_before_autheris_satisfies_the_consent_and_is_never_revoked()
    {
        _client.Set(UcSecurableType.Table, Table, "analysts").Add(UcPrivileges.Select);
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();

        _store.Ledger.Keys.ShouldNotContain(k => k.Key.SecurableType == UcSecurableType.Table);
        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Provisioned);

        _store.Consents.Remove(consent);
        await RunAsync();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeTrue();
    }

    [Fact]
    public async Task Deny_on_the_table_makes_the_allow_unsupported_and_revokes_what_was_applied()
    {
        var allow = Grant(Now);
        _store.Consents.Add(allow);
        await RunAsync();

        _store.Consents.Add(Grant(Now, grantee: OtherOid, effect: ConsentEffect.Deny));
        await RunAsync();

        _store.FeedbackOf(allow).State.ShouldBe(UcGrantState.Unsupported);
        _store.FeedbackOf(allow).ReasonCode.ShouldBe(UcReasonCodes.DenyPresent);
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeFalse();
    }

    [Fact]
    public async Task Pattern_deny_covering_the_table_makes_the_allow_unsupported()
    {
        var allow = Grant(Now);
        _store.Consents.Add(allow);
        _context.PatternConsents.Add(new Consent
        {
            ObjectPattern = Source + ".sales.*",
            DataSource = Source,
            Effect = ConsentEffect.Deny,
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = new Autheris.Domain.Common.Sid(OtherOid),
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(1)
        });

        await RunAsync();

        _store.FeedbackOf(allow).ReasonCode.ShouldBe(UcReasonCodes.DenyPresent);
        _store.Ledger.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, UcReasonCodes.PrincipalUnresolved)]
    [InlineData(true, UcReasonCodes.PrincipalAmbiguous)]
    public async Task Unresolved_or_ambiguous_principal_is_never_granted(bool ambiguous, string reason)
    {
        _client.Principals[GroupOid] = ambiguous ? UcPrincipalResolution.Ambiguous : UcPrincipalResolution.NotFound;
        var consent = Grant(Now);
        _store.Consents.Add(consent);

        await RunAsync();

        _store.FeedbackOf(consent).ReasonCode.ShouldBe(reason);
        _client.Calls.ShouldNotContain(c => c.StartsWith("patch:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Planning_failure_changes_nothing()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();
        _client.Calls.Clear();
        _store.Writes.Clear();

        _store.Consents.Remove(consent);
        _store.Consents.Add(Grant(Now, table: "customers"));
        _context.Fail = new InvalidOperationException("catalog store down");
        await RunAsync();

        _client.Calls.ShouldBeEmpty();
        _store.Writes.ShouldBeEmpty();
        _store.Ledger.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Missing_catalog_access_is_reported_failed_not_provisioned()
    {
        _client.CatalogAccess.Clear();
        var consent = Grant(Now);
        _store.Consents.Add(consent);

        await RunAsync();

        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Failed);
        _store.FeedbackOf(consent).ReasonCode.ShouldBe(UcProvisioningReconciler.CatalogAccessMissingReason);

        _client.CatalogAccess.Add("analysts");
        await RunAsync();
        _store.FeedbackOf(consent).State.ShouldBe(UcGrantState.Provisioned);
    }

    [Fact]
    public async Task Renamed_group_is_recorded_as_drift_without_a_new_grant()
    {
        _store.Consents.Add(Grant(Now));
        await RunAsync();
        _client.Calls.Clear();

        _client.Principals[GroupOid] = Group("analysts-eu");
        _client.Rename("analysts", "analysts-eu");
        await RunAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("patch:", StringComparison.Ordinal));
        _store.Ledger.Values.ShouldAllBe(e => e.Principal == "analysts-eu");
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantProvisioningDrift && a.Decision == "PRINCIPAL_RENAMED").ShouldBe(2);
    }

    [Fact]
    public async Task Consents_of_other_tenants_or_sources_are_never_provisioned()
    {
        _store.Consents.Add(new Consent
        {
            TableIdentifier = TableId(),
            Effect = ConsentEffect.Allow,
            TenantId = new Autheris.Domain.Common.TenantId("beta"),
            GranteeType = GranteeType.Group,
            GranteeSid = new Autheris.Domain.Common.Sid(GroupOid),
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(1)
        });

        await RunAsync();

        _client.Calls.ShouldBeEmpty();
        _store.Ledger.ShouldBeEmpty();
        _store.Feedback.ShouldBeEmpty();
    }

    [Fact]
    public async Task Renamed_group_whose_consent_ends_is_revoked_under_its_current_name()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();

        _client.Principals[GroupOid] = Group("analysts-eu");
        _client.Rename("analysts", "analysts-eu");
        _store.Consents.Remove(consent);
        await RunAsync();

        _client.Holds(UcSecurableType.Table, Table, "analysts-eu", UcPrivileges.Select).ShouldBeFalse();
        _store.Ledger.ShouldBeEmpty();
    }

    [Fact]
    public async Task Not_found_without_a_missing_securable_is_not_a_removal()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();
        _store.Consents.Remove(consent);
        _client.FailUpdates = UcErrorCodes.NotFound;
        _client.FailRemote = "PRINCIPAL_DOES_NOT_EXIST";

        await RunAsync();

        _store.Ledger.Count.ShouldBe(2);
        _store.Ledger.Values.ShouldAllBe(e => e.State == UcLedgerState.RevokePending);
        _store.Audit.ShouldNotContain(a => a.EventType == AuditEventTypes.GrantDeprovisioned);
    }

    [Fact]
    public async Task Securable_reported_gone_ends_its_ledger_rows()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();
        _store.Consents.Remove(consent);
        _client.FailUpdates = UcErrorCodes.NotFound;
        _client.FailRemote = "TABLE_DOES_NOT_EXIST";

        await RunAsync();

        _store.Ledger.ShouldBeEmpty();
        _store.Audit.Count(a => a.EventType == AuditEventTypes.GrantDeprovisioned && a.Decision == "SECURABLE_GONE").ShouldBe(2);
    }

    [Fact]
    public async Task One_bad_principal_does_not_block_the_revokes_of_the_others()
    {
        _client.Principals[OtherOid] = Group("auditors", OtherOid);
        _client.CatalogAccess.Add("auditors");
        var first = Grant(Now);
        var second = Grant(Now, grantee: OtherOid);
        _store.Consents.AddRange([first, second]);
        await RunAsync();

        _store.Consents.Clear();
        _client.FailPrincipal = "auditors";
        await RunAsync();

        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeFalse();
        _client.Holds(UcSecurableType.Table, Table, "auditors", UcPrivileges.Select).ShouldBeTrue();
        _store.Ledger.Values.ShouldAllBe(e => e.PrincipalExternalId == OtherOid && e.State == UcLedgerState.RevokePending);
        _store.FeedbackOf(first).ReasonCode.ShouldBe(UcProvisioningReconciler.RevokePendingReason);
    }

    [Fact]
    public async Task Dry_run_still_revokes_what_autheris_applied()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();

        _options.CurrentValue = Options(DatabricksProvisioningMode.DryRun);
        _store.Consents.Remove(consent);
        await RunAsync();

        _store.Ledger.ShouldBeEmpty();
        _client.Holds(UcSecurableType.Table, Table, "analysts", UcPrivileges.Select).ShouldBeFalse();
    }

    [Fact]
    public async Task Virtual_filter_profiles_make_consents_unsupported()
    {
        _context.VirtualFilters = true;
        var consent = Grant(Now);
        _store.Consents.Add(consent);

        await RunAsync();

        _store.FeedbackOf(consent).ReasonCode.ShouldBe(UcReasonCodes.VirtualFilter);
        _store.Ledger.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failing_principal_lookup_holds_back_only_that_principal()
    {
        var consent = Grant(Now);
        _store.Consents.Add(consent);
        await RunAsync();

        _client.ResolveFailure = UcErrorCodes.RateLimited;
        _store.Consents.Remove(consent);
        var replacement = Grant(Now);
        _store.Consents.Add(replacement);
        _client.Calls.Clear();
        await RunAsync();

        _client.Calls.ShouldNotContain(c => c.StartsWith("patch:", StringComparison.Ordinal));
        _store.Ledger.Count.ShouldBe(2);
        _store.FeedbackOf(replacement).ReasonCode.ShouldBe(UcErrorCodes.RateLimited);
    }

    [Fact]
    public async Task Ledger_rows_of_a_source_without_provisioning_section_count_as_overdue()
    {
        _store.Ledger[("legacy", new UcPrivilegeKey(UcSecurableType.Table, "main.sales.x", GroupOid, UcPrivileges.Select))] = new UcLedgerEntry
        {
            DataSource = "legacy",
            SecurableType = UcSecurableType.Table,
            FullName = "main.sales.x",
            Principal = "analysts",
            PrincipalExternalId = GroupOid,
            Privilege = UcPrivileges.Select,
            State = UcLedgerState.Applied
        };

        await RunAsync();

        UcProvisioningMetrics.OverdueRevokes.ShouldBe(1);
        _store.Ledger.Count.ShouldBe(1);
    }
}
