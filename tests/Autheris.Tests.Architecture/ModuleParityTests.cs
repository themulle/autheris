namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

public sealed class ModuleParityTests
{
    private static readonly string[] ExpectedLakehouseSecurityTests =
    [
        // Pre-move Lakehouse security tests migrated to Autheris.Modules.Lakehouse.Tests
        "Autheris.Extensions.Tests.DeltaLakeGateOrderTests.NonDemo_ThrowsNotImplemented_WithoutReadingDeltaLog",
        "Autheris.Extensions.Tests.Lakehouse.IcebergTableDirectoryExt04Tests.GetUriTableDirectory_OnlyStripsKnownMetadataFiles",
        "Autheris.Tests.Unit.Api.IcebergRestCatalogTenantTests.RequireTenant_ResolvedTenant_IsReturnedUnchanged",
        "Autheris.Tests.Unit.Api.IcebergRestCatalogTenantTests.RequireTenant_UnresolvedTenant_ThrowsTenantUnresolved_NeverDefault",

        // Core security tests covering Iceberg/Lakehouse governance
        "Autheris.Tests.Unit.Governance.Issue143ScopeBlindPathTests.IcebergTable_RestrictedToken_SensitiveMasked",
        "Autheris.Tests.Unit.Governance.Issue143ScopeBlindPathTests.T_GOV143_02_Iceberg_RestrictedToken_NoRawVending_ForSensitiveColumns",
        "Autheris.Tests.Unit.Governance.Issue143ScopeBlindPathTests.T_GOV143_15_Iceberg_ScopesFromClaimsOverload_PlainScopeClaimIncluded_EmptyIsNull",
        "Autheris.Tests.Unit.Security.Batch4AdvisoriesSecurityTests.IcebergRestCatalog_EnforcesTransportGate",

        // WP-SIMPL-27 module contract, governance, and ALC security tests
        "Autheris.Modules.Lakehouse.Tests.LakehouseModuleContractTests.Iceberg_routes_require_authorization",
        "Autheris.Tests.Integration.LakehouseModuleAbsentSecurityTests.Iceberg_routes_404_when_absent",
        "Autheris.Modules.Lakehouse.Tests.LakehouseModuleContractTests.Lakehouse_config_without_module_fails_fast",
        "Autheris.Modules.Lakehouse.Tests.LakehouseModuleContractTests.Delta_executor_enforces_query_gate",
        "Autheris.Modules.Lakehouse.Tests.LakehouseGovernanceTests.Delta_query_applies_row_filter_and_masking",
        "Autheris.Modules.Lakehouse.Tests.LakehouseGovernanceTests.Delta_rejects_undeclared_columns",
        "Autheris.Modules.Lakehouse.Tests.LakehouseGovernanceTests.Iceberg_load_omits_undeclared_columns",
        "Autheris.Modules.Lakehouse.Tests.LakehouseGovernanceTests.Iceberg_load_and_credential_vending_decide_via_access_decision_point",
        "Autheris.Modules.Lakehouse.Tests.LakehouseGovernanceTests.Delta_joins_with_sql_server_source_via_federation",
        "Autheris.Modules.Lakehouse.Tests.LakehouseGovernanceTests.Delta_query_is_audited",
        "Autheris.Tests.Unit.Hosting.ModuleAssemblyLoadContextTests.Private_managed_dependency_resolves_from_module_folder",
        "Autheris.Tests.Unit.Hosting.ModuleAssemblyLoadContextTests.Private_native_library_resolves_from_runtimes_folder",
        "Autheris.Tests.Unit.Hosting.ModuleAssemblyLoadContextTests.Shared_host_assembly_Parquet_resolves_from_default_context"
    ];

    private static readonly string[] ExpectedEventsSecurityTests =
    [
        // Pre-move CDC delivery tests (names and namespaces unchanged; the assembly changed or the class stays in Unit with the module reference)
        "Autheris.Tests.Unit.Streaming.Harness.Post_Subscription_WhenSecretVaultUnsupported_ReturnsFailClosed",
        "Autheris.Tests.Unit.Streaming.CapturingLogger.Allowed_subject_is_delivered_positive_control",
        "Autheris.Tests.Unit.Streaming.CapturingLogger.Drops_events_for_locked_inactive_and_foreign_realm_subjects",

        // WP-SIMPL-29 events module: module contract and governance
        "Autheris.Modules.Events.Tests.EventsModuleTests.Module_routes_require_authorization",
        "Autheris.Modules.Events.Tests.EventsModuleTests.Cdc_admin_routes_keep_governance_admin_policy",
        "Autheris.Modules.Events.Tests.EventsModuleTests.Event_delivery_uses_core_webhook_delivery_client",
        "Autheris.Modules.Events.Tests.EventsModuleTests.Webhook_signature_secret_not_logged",
        "Autheris.Modules.Events.Tests.EventsModuleTests.Webhook_signing_secret_not_in_logs_audit_or_responses",
        "Autheris.Modules.Events.Tests.EventsModuleTests.Events_module_has_no_data_source_or_governance_connection_reference",
        "Autheris.Modules.Events.Tests.EventsModuleTests.Events_module_loads_under_the_service_guard",
        "Autheris.Modules.Events.Tests.EventsModuleGovernanceTests.Cdc_stream_applies_row_filter_and_masking_when_loaded_as_module",
        "Autheris.Modules.Events.Tests.EventsModuleGovernanceTests.Cdc_webhook_delivery_applies_row_filter_and_masking_when_loaded_as_module",

        // Host guards the module relies on
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Events_module_hosted_services_allowed_only_from_module_assembly",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Module_replacing_hosted_service_or_stream_enforcer_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Module_replacing_secret_protector_or_key_store_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Webhook_client_chain_has_ssrf_handler_when_events_module_loaded",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Module_httpclient_without_ssrf_handler_fails_startup",
        "Autheris.Tests.Unit.Hosting.EventsModuleConfigGuardTests.Removed_cdc_webhooks_and_openlineage_keys_fail_with_new_path_message",
        "Autheris.Tests.Unit.Hosting.EventsModuleConfigGuardTests.Webhook_subscriptions_without_events_module_fail_startup",
        "Autheris.Tests.Architecture.PackageBoundaryArchitectureTests.Events_module_types_do_not_bypass_access_decision_point",

        // Integration (GitHub Actions)
        "Autheris.Tests.Integration.Modules.EventsModuleAbsentTests.Cdc_stream_routes_return_404_without_module",
        "Autheris.Tests.Integration.Modules.EventsModuleAbsentTests.Events_config_section_fails_fast_without_module",
        "Autheris.Tests.Integration.Modules.EventsModuleAbsentTests.Webhook_subscriptions_without_events_module_fail_fast",
        "Autheris.Tests.Integration.Modules.EventsModuleAbsentTests.Consent_webhook_dispatch_works_without_events_module",
        "Autheris.Tests.Integration.Modules.EventsModuleAbsentTests.Cdc_ingest_requires_auth_and_audits_under_module",
        "Autheris.Tests.Integration.Security.EventsModuleParityTests.Cdc_rest_and_graphql_subscription_apply_identical_rls_and_masking_with_events_module_loaded",
        "Autheris.Tests.Integration.Security.EventsModuleParityTests.Cdc_graphql_subscription_unchanged_without_events_module"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Events_security_tests_migrated()
    {
        var (_, securityTests) = TestInventoryScanner.ScanRepository();

        var missing = ExpectedEventsSecurityTests
            .Where(t => !securityTests.Contains(t))
            .ToList();

        missing.ShouldBeEmpty(
            "Every events module security test must exist and carry [Trait(\"Category\", \"Security\")]:\n" +
            string.Join("\n", missing));

        RepositoryScan.EnumerateFiles("modules/Autheris.Modules.Events.Tests", "*.cs").ToList().Count.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lakehouse_security_tests_migrated()
    {
        var (_, securityTests) = TestInventoryScanner.ScanRepository();

        var missing = ExpectedLakehouseSecurityTests
            .Where(t => !securityTests.Contains(t))
            .ToList();

        missing.ShouldBeEmpty(
            "SEC-SIMPL-48/49: Every Lakehouse security test must exist and carry [Trait(\"Category\", \"Security\")]:\n" +
            string.Join("\n", missing));

        var moduleTestFiles = RepositoryScan.EnumerateFiles("modules/Autheris.Modules.Lakehouse.Tests", "*.cs").ToList();
        moduleTestFiles.Count.ShouldBeGreaterThanOrEqualTo(13);
    }
}
