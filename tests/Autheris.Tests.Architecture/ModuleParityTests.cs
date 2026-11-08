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
