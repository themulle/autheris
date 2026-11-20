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

    private static readonly string[] ExpectedDbtSecurityTests =
    [
        // Pre-move dbt security tests (renamed into Autheris.Modules.Dbt.Tests) and the WP-SIMPL-28 PR 3b additions
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_CannotLowerMinGroupSize_RatchetsWithMax",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_Change_AuditedWithSourceDbt",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_DryRun_NoWrite",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_FlatKeys_SetsTableValues",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_GovernanceOverlay_StillWins",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_InvalidValue_WarnsAndKeepsStored",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_KeyRemoved_KeepsValues",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_NestedAutherisObject_SetsTableValues",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_NoAuditWriter_KeepsStoredValues",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_NoStanding_RejectsMinGroupAndNonPersonal",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_PiiColumn_ForcesNonPersonalFalse",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_StoredMaskedColumn_ForcesNonPersonalFalse",
        "Autheris.Modules.Dbt.Tests.DbtCatalogMinGroupSizeIngestionTests.Ingest_StoredSensitiveColumn_ForcesNonPersonalFalse",
        "Autheris.Modules.Dbt.Tests.DbtCompositeRelationTests.Ingest_RelationToRejectedTenantByUniqueId_Dropped_ReasonDoesNotNameForeignNode",
        "Autheris.Modules.Dbt.Tests.DbtCompositeRelationTests.Ingest_UniqueIdNotInModels_NoNameFallback",
        "Autheris.Modules.Dbt.Tests.DbtCompositeRelationTests.Lookup_NameOnlyRelation_TwoModelsSameName_ReportsAmbiguousModel",
        "Autheris.Modules.Dbt.Tests.DbtCompositeRelationTests.Lookup_UniqueIdNotInModels_NoNameFallback",
        "Autheris.Modules.Dbt.Tests.DbtCompositeRelationTests.Parse_RelationCountAndIdentifierLimits_SkipWithWarning",
        "Autheris.Modules.Dbt.Tests.DbtMetaIndexesTests.InvalidEntry_DropsDeclaration_LogsNoValues",
        "Autheris.Modules.Dbt.Tests.DbtRoleGranteeSkipTests.Security_DbtAndOpenMetadata_RoleGrantee_SkippedNeverMapped_Dbt",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_AttachedNodeIndexedButMissingFromDependsOn_SkipsUnknownModel",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_AttachedNodeNotInIndex_SkipsUnknownModel",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_KeyTestWithTwoDependencies_SkipsAmbiguousAttachment",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_PendingRelations_CountedAtCollection_LimitHolds",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_RefMatchingTwoPackages_NoNarrowing_SkipsAmbiguousReference",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_RelationshipsCombination_ForeignNamespace_UnsupportedForm",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_RelationshipsCombination_TooWide_Skips",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_SameTableNameInTwoDatabases_ResolvesBySourceName",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_SelfRelationInference_OnlyWhenSingleDependsOn",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_SourceRelationshipsTest_ParentFirstInDependsOn_ChildIsNotParent",
        "Autheris.Modules.Dbt.Tests.DbtSourceRelationTests.Parse_TwoCandidateChildren_SkipsAmbiguousReference",
        "Autheris.Modules.Dbt.Tests.DbtSyncEndpointCreationTests.Sync_AuditDetailsContainCreatedNamesWithoutSecrets",
        "Autheris.Modules.Dbt.Tests.DbtSyncEndpointCreationTests.Sync_AuditDetailsOnPartialFailure_ContainCreatedNamesWithoutColumnsOrSecrets",
        "Autheris.Modules.Dbt.Tests.DbtSyncEndpointCreationTests.Sync_NonAdmin_Returns403AndServiceNotCalled",
        "Autheris.Modules.Dbt.Tests.DbtSyncEndpointCreationTests.Sync_ServiceThrows_AuditDetailsAreStillWritten",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.DbtArtifactStreamingParser_DoesNotReferenceSystemIoFile",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.ExtractSyntheticSamples_MoreThan100Rows_Truncated",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.ExtractSyntheticSamples_NestedObjectValue_Dropped",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.ExtractSyntheticSamples_OversizedString_Skipped",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.ExtractSyntheticSamples_SamplesAliasIgnored",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.ExtractSyntheticSamples_StringPath_IgnoredAndNoFileRead",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.ExtractSyntheticSamples_UnknownKeys_Dropped",
        "Autheris.Modules.Dbt.Tests.DbtSyntheticSamplesSecurityTests.ParseManifest_StringPathSample_YieldsNoSamples",
        "Autheris.Modules.Dbt.Tests.DbtTests.DbtMetadataIngestionService_UnqualifiedImportLookup_OnlyCallersExactTableIsCandidate",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.Alias_CollisionBetweenTwoNodes_RejectsBoth_OrderIndependent",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.Alias_EqualToExistingEntryOfOtherOrigin_IsRejected",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.Alias_NullOrEmpty_FallsBackButInvalid_Rejects",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.Alias_OntoEntryWithoutUniqueId_IsRejected",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.NoAlias_EntryOfOtherUniqueId_IsRejected",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.PhysicalName_DoesNotCollapseTwoSourcesOntoOneCatalogEntry_ReportsCollision",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.Relation_ByNameFallback_Ambiguous_IsSkipped",
        "Autheris.Modules.Dbt.Tests.FakeLineageGraphStore.Warning_ContainsOnlySanitizedIdentifiers",
        "Autheris.Modules.Dbt.Tests.GoldenQueryMetaImportTests.Entries_are_stored_under_the_table_owner_tenant",
        "Autheris.Modules.Dbt.Tests.GoldenQueryMetaImportTests.Fragments_directives_and_deep_nesting_rejected",
        "Autheris.Modules.Dbt.Tests.GoldenQueryMetaImportTests.Invalid_graphql_and_over_complex_queries_rejected",
        "Autheris.Modules.Dbt.Tests.GoldenQueryMetaImportTests.Query_over_other_dataset_rejected",
        "Autheris.Modules.Dbt.Tests.GoldenQueryMetaImportTests.Text_is_sanitised_before_storage",
        "Autheris.Modules.Dbt.Tests.GoldenQueryMetaImportTests.Warning_contains_no_values",
        "Autheris.Modules.Dbt.Tests.Harness.Import_CrossSourceCompositeRelation_NotExecutable_UntilApproved",
        "Autheris.Modules.Dbt.Tests.Harness.Import_MaliciousColumnNames_NeverReachSql_AndWarningsAreSanitized",
        "Autheris.Modules.Dbt.Tests.Harness.Import_TwoForeignKeysSameParent_NoLastWinsSwap",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_DryRun_PerformsNoRepositoryWrites",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_ExceedsTableOrColumnCap_SkipsExcess",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_ExistingEntry_IsActiveSensitivityMasksConsentsUnchanged",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_InvalidColumnName_SkipsWholeTable",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_InvalidSourceObjectKind_BecomesTableWithBoundedWarningAndDoesNotFailTheSync",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_LogsAndAuditSanitizeControlCharsInNames",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_MappingTargetMissingFromConnections_SkipsAndCreatesNothing",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_MetadataUnavailable_NeverCreates",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_NewTable_IsInactiveNormalAndHasNoMasksOrConsents",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_NonClusterAdminCaller_CannotSetKindOnNewOrExistingTables",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_OtherTenantModel_RejectedAndNotCreated",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_QuotedIdentifierWithSpaceOrDot_SkipsWithInvalidIdentifier",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_SkippedItemsAreCappedWithTruncatedFlag",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_TableNameWithNewlineSuffix_SkipsWithInvalidIdentifier",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_UnicodeDigitOrLetterInIdentifier_SkipsWithInvalidIdentifier",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_UnmappedDatabase_NoFallbackToDefaultConnection",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_UnmappedDatabase_SkipsWithNoDataSourceMapping",
        "Autheris.Modules.Dbt.Tests.Harness.Sync_UpsertRaceExistingRowAppears_DoesNotOverwriteGovernance",
        "Autheris.Modules.Dbt.Tests.HostileTelemetryProvider.Quote_and_newline_in_name_cannot_inject_yaml",
        "Autheris.Modules.Dbt.Tests.DbtWebhookReceiverTests.Cluster_dedup_entries_expire_after_ttl_and_reject_across_instances",
        "Autheris.Modules.Dbt.Tests.DbtWebhookReceiverTests.Dedup_ttl_not_shorter_than_replay_window",
        "Autheris.Modules.Dbt.Tests.DbtWebhookReceiverTests.Malformed_payload_message_is_fixed",
        "Autheris.Modules.Dbt.Tests.DbtWebhookReceiverTests.Missing_store_rejects_with_store_unavailable",
        "Autheris.Modules.Dbt.Tests.DbtWebhookReceiverTests.Store_unavailable_rejects_in_every_mode",
        "Autheris.Modules.Dbt.Tests.Rig.Import_UnsetTenant_IsForbidden_BeforeAnySideEffect",
        "Autheris.Modules.Dbt.Tests.Rig.ManifestImport_ModelNameWithDotOrQuote_Rejected_NothingPersisted",
        "Autheris.Modules.Dbt.Tests.Rig.ManifestImport_MultiMode_TenantBCannotOverwriteTenantAVirtualTable",
        "Autheris.Modules.Dbt.Tests.Rig.ManifestImport_MultiMode_VirtualTableIsNeverTheBaseTable",
        "Autheris.Modules.Dbt.Tests.Rig.ManifestImport_OneInvalidTemplate_NothingPersisted",
        "Autheris.Modules.Dbt.Tests.Rig.ManifestImport_UnresolvedOrForeignBaseTable_GenericErrorNoNameEcho",
        "Autheris.Modules.Dbt.Tests.Rig.SemanticStore_MultiMode_ClaimlessImport_403",
        "Autheris.Modules.Dbt.Tests.Rig.SemanticStore_NoSid_EmptyInBothModes",
        "Autheris.Modules.Dbt.Tests.Rig.SemanticStore_NoSid_Multi_NoAccess",
        "Autheris.Modules.Dbt.Tests.Rig.UserSchemaNamedLikeTenantHash_NotTreatedAsVirtualView",
        "Autheris.Modules.Dbt.Tests.Rig.VirtualTableSchema_DerivedFromTenantIdOnly",
        "Autheris.Modules.Dbt.Tests.DbtModuleContractTests.Dbt_routes_require_authorization",
        "Autheris.Modules.Dbt.Tests.DbtModuleContractTests.Dbt_module_maps_no_anonymous_route",
        "Autheris.Modules.Dbt.Tests.DbtModuleContractTests.Dbt_endpoint_audit_levels_preserved",
        "Autheris.Modules.Dbt.Tests.DbtModuleContractTests.Dbt_body_limits_preserved",
        "Autheris.Modules.Dbt.Tests.DbtModuleContractTests.Dbt_module_registers_owner_identity_verifier",
        "Autheris.Modules.Dbt.Tests.DbtModuleContractTests.Dbt_module_loads_under_the_service_guard",
        "Autheris.Modules.Dbt.Tests.DbtModuleContractTests.Dbt_config_without_module_fails_fast",
        "Autheris.Tests.Integration.DbtModuleAbsentTests.Dbt_routes_404_when_absent",
        "Autheris.Tests.Integration.DbtModuleAbsentTests.Dbt_config_without_module_fails_fast",
        "Autheris.Tests.Integration.DbtModuleAbsentTests.Dbt_cloud_webhook_returns_404_without_module_before_body_read",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Unsigned_request_returns_401_and_never_reaches_handler",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Invalid_signature_never_reaches_handler",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Authorization_header_hmac_is_accepted",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Authorization_bearer_value_is_not_treated_as_signature",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Body_over_2_mb_returns_413",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Signature_bypass_flag_honored_only_when_set",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Missing_and_invalid_signature_return_identical_401_body",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Handler_not_resolved_before_signature_verified",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Sha256_prefix_in_authorization_header_rejected",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Environment_secret_fallback_is_ignored",
        "Autheris.Tests.Unit.Api.DbtCloudWebhookEndpointTests.Secret_and_signature_never_logged",
        "Autheris.Tests.Unit.Api.FakeDbtWebhookReceiver.Dbt_MissingSignature_RejectsBeforeBufferingBody",
        "Autheris.Tests.Unit.Api.FakeDbtWebhookReceiver.Dbt_BodyExceeds2Mb_ReturnsPayloadTooLarge",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Second_dbt_webhook_handler_fails_startup"
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

    private static readonly string[] ExpectedLineageSecurityTests =
    [
        // Pre-move OpenLineage security tests migrated to Autheris.Modules.Lineage.Tests (names and namespaces unchanged)
        "Autheris.Extensions.Tests.CapturingHandler.Push_OtherTenantNodes_AreExcluded",
        "Autheris.Extensions.Tests.CapturingHandler.Push_UntenantedNodes_ExcludedForNonLegacyTenant",
        "Autheris.Extensions.Tests.CapturingHandler.Push_UntenantedNodes_IncludedForLegacyTenant",
        "Autheris.Extensions.Tests.CapturingHandler.Push_UntenantedNodes_BelongToTheConfiguredDefaultTenantOnly",
        "Autheris.Extensions.Tests.CapturingHandler.Push_UntenantedNodes_ExcludedForEveryTenantWithoutDefaultTenant",
        "Autheris.Extensions.Tests.CapturingHandler.Push_DownstreamReferenceToOtherTenant_DoesNotMakeNodeAnInput",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Bearer_key_not_sent_over_http",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Bearer_key_allowed_over_loopback_in_development",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Endpoint_with_userinfo_rejected",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Log_contains_no_query_string",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Http_endpoint_refused_outside_development",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Unset_endpoint_disables_push",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Validation_failure_disables_client_with_one_startup_error",
        "Autheris.Tests.Unit.Lineage.RecordingLogger.Https_endpoint_outside_development_sends_bearer_key",
        "Autheris.Tests.Unit.Security.OpenLineageSyncTenantTests.Sync_UsesRealmBoundTenant_NotTenantIdClaim",
        "Autheris.Tests.Unit.Security.OpenLineageSyncTenantTests.Sync_EntraTidCaller_PushesTidTenant",
        "Autheris.Tests.Unit.Security.OpenLineageSyncTenantTests.Sync_DataOwnerOnly_Returns403",
        "Autheris.Tests.Unit.Security.OpenLineageSyncTenantTests.Sync_MalformedTenantClaim_DoesNotReturn500",

        // WP-SIMPL-29 lineage module contract, SSRF, secret redaction, and boundary tests
        "Autheris.Modules.Lineage.Tests.LineageModuleTests.OpenLineage_client_uses_secure_outbound_handlers",
        "Autheris.Modules.Lineage.Tests.LineageModuleTests.OpenLineage_endpoint_to_private_address_is_blocked",
        "Autheris.Modules.Lineage.Tests.LineageModuleTests.Lineage_sync_route_requires_governance_admin_and_audits",
        "Autheris.Modules.Lineage.Tests.LineageModuleTests.OpenLineage_api_key_not_in_logs_or_config_dump",
        "Autheris.Modules.Lineage.Tests.LineageModuleTests.Lineage_module_loads_under_the_service_guard",
        "Autheris.Modules.Lineage.Tests.LineageModuleTests.Lineage_module_has_no_data_source_or_governance_connection_reference",

        // Host guards
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Module_httpclient_without_ssrf_handler_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Lineage_client_chain_has_ssrf_handler_when_lineage_module_loaded",
        "Autheris.Tests.Unit.Hosting.EventsModuleConfigGuardTests.Removed_cdc_webhooks_and_openlineage_keys_fail_with_new_path_message",
        "Autheris.Tests.Unit.Hosting.EventsModuleConfigGuardTests.Lineage_keys_pass_the_early_pass_only_when_the_lineage_module_is_enabled",
        "Autheris.Tests.Unit.Hosting.ModuleConfigurationTests.Lineage_section_without_lineage_module_fails_fast",

        // Integration (GitHub Actions)
        "Autheris.Tests.Integration.Modules.LineageModuleAbsentTests.Lineage_routes_return_404_without_module",
        "Autheris.Tests.Integration.Modules.LineageModuleAbsentTests.Lineage_config_section_fails_fast_without_module",
        "Autheris.Tests.Integration.Modules.LineageModuleAbsentTests.Gdpr_report_route_still_served_without_lineage_module"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_security_tests_migrated()
    {
        var (_, securityTests) = TestInventoryScanner.ScanRepository();

        var missing = ExpectedDbtSecurityTests
            .Where(t => !securityTests.Contains(t))
            .ToList();

        missing.ShouldBeEmpty(
            "SEC-SIMPL-48: Every dbt security test must exist and carry [Trait(\"Category\", \"Security\")]:\n" +
            string.Join("\n", missing));

        var moduleTestFiles = RepositoryScan.EnumerateFiles("modules/Autheris.Modules.Dbt.Tests", "*.cs").ToList();
        moduleTestFiles.Count.ShouldBeGreaterThanOrEqualTo(15);
    }

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

    private static readonly string[] ExpectedOpenMetadataSecurityTests =
    [
        "Autheris.Modules.OpenMetadata.Tests.Fixture.RestrictedSync_DoesNotRevokeOtherTenantsSyncConsents",
        "Autheris.Modules.OpenMetadata.Tests.Fixture.RestrictedSync_OnlyUpsertsTablesOfTheCallerTenant",
        "Autheris.Modules.OpenMetadata.Tests.Fixture.SanitizedWarnings_AreCodesWithCounts_WithoutTableNamesOrExceptionText",
        "Autheris.Modules.OpenMetadata.Tests.Fixture.UnrestrictedSync_TouchesEveryTenant",
        "Autheris.Modules.OpenMetadata.Tests.OmFixture.PERS_04_Sync_ConcurrentlyRevokedConsent_IsNotRecreated",
        "Autheris.Modules.OpenMetadata.Tests.OmFixture.PERS_04_Sync_ExpiredSyncConsent_IsRecreated_NeverExtended",
        "Autheris.Modules.OpenMetadata.Tests.OmFixture.PERS_04_Sync_RevokeReturnedFalse_DoesNotCreate",
        "Autheris.Modules.OpenMetadata.Tests.OmFixture.RoleAllowPolicy_NeverCreatesAConsent_RolesDoNotGrantDataAccess",
        "Autheris.Modules.OpenMetadata.Tests.OmFixture.Security_DbtAndOpenMetadata_RoleGrantee_SkippedNeverMapped_OpenMetadata",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataWebhookCollisionTests.TableOfAnotherDatabaseWithTheSameIdentity_IsNotMergedByTheWebhook",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataWebhookTests.Missing_store_rejects_as_unavailable",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataWebhookTests.Oversized_payload_is_rejected",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataWebhookTests.Replay_across_two_service_instances_sharing_one_store_is_rejected",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataWebhookTests.Replayed_event_is_rejected",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataWebhookTests.Skew_check_uses_injected_time_provider",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataWebhookTests.Store_unavailable_rejects_without_local_fallback",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataModuleContractTests.OpenMetadata_module_maps_no_routes",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataModuleContractTests.OpenMetadata_module_registers_ports_with_implementation_types",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataModuleContractTests.OpenMetadata_http_client_uses_secure_outbound_handlers",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataModuleContractTests.OpenMetadata_module_registers_no_hosted_service",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataModuleContractTests.OpenMetadata_module_registers_only_its_typed_http_client",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataModuleContractTests.OpenMetadata_module_loads_under_the_service_guard",
        "Autheris.Modules.OpenMetadata.Tests.OpenMetadataModuleContractTests.OpenMetadata_config_without_module_fails_fast",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Handler_absent_returns_404_before_header_or_body_read",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Missing_header_returns_401_even_when_bypass_set",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Empty_primary_header_falls_back_to_om_signature_header",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Body_not_read_before_signature_header_present",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Handler_not_resolved_before_signature_verified",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Blank_secret_rejects_all_webhooks",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Missing_and_invalid_signature_return_identical_401_body",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Signature_bypass_flag_honored_only_when_set",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Body_over_2_mb_returns_413",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Secret_and_signature_never_logged",
        "Autheris.Tests.Unit.Api.OpenMetadataWebhookEndpointTests.Route_keeps_anonymous_2mb_limit_and_full_audit",
        "Autheris.Tests.Unit.Api.FakeDbtWebhookReceiver.OpenMetadata_MissingSignature_RejectsBeforeBufferingBody",
        "Autheris.Tests.Unit.Api.FakeDbtWebhookReceiver.OpenMetadata_BodyExceeds2Mb_ReturnsPayloadTooLarge",
        "Autheris.Tests.Unit.Catalog.OpenMetadataWebhookSignatureTests.Valid_hmac_with_and_without_sha256_prefix_accepted",
        "Autheris.Tests.Unit.Catalog.OpenMetadataWebhookSignatureTests.Non_hex_or_wrong_length_signature_rejected",
        "Autheris.Tests.Unit.Catalog.OpenMetadataWebhookSignatureTests.Blank_secret_returns_false",
        "Autheris.Tests.Unit.Catalog.OpenMetadataWebhookSignatureTests.Literal_bypassed_signature_is_rejected_when_bypass_off",
        "Autheris.Tests.Unit.Catalog.OpenMetadataWebhookSignatureTests.Exception_returns_false_without_secret_in_log",
        "Autheris.Tests.Unit.GraphQL.SyncOpenMetadataSanitisationTests.Availability_checked_after_role_and_tenant_check",
        "Autheris.Tests.Unit.GraphQL.SyncOpenMetadataSanitisationTests.Non_admin_without_module_gets_forbidden_not_unavailable",
        "Autheris.Tests.Unit.GraphQL.SyncOpenMetadataSanitisationTests.SyncDataCatalog_without_client_does_not_fail_before_authentication",
        "Autheris.Tests.Unit.GraphQL.SyncOpenMetadataSanitisationTests.Unavailable_error_has_fixed_message",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Module_httpclient_without_ssrf_handler_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.OpenMetadata_port_from_other_module_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Second_openmetadata_webhook_handler_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.Catalog_enabled_without_openmetadata_module_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.OpenMetadata_port_with_factory_registration_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.OpenMetadata_port_without_loaded_module_fails_startup",
        "Autheris.Tests.Unit.Hosting.ModuleLoaderSecurityTests.OpenMetadata_module_client_without_ssrf_handler_fails_startup",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleAbsentTests.OpenMetadata_webhook_returns_404_without_module_before_body_read",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleAbsentTests.OpenMetadata_config_without_module_fails_fast",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleAbsentTests.Legacy_insecure_switches_fail_fast_without_module",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleAbsentTests.Catalog_enabled_without_module_fails_fast",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleAbsentTests.Non_admin_sync_without_module_returns_forbidden",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleSecurityTests.Unsigned_webhook_returns_401_when_enabled",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleSecurityTests.Signed_webhook_reaches_handler_when_enabled",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleSecurityTests.Replayed_event_is_rejected_when_enabled",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleSecurityTests.Invalid_signature_returns_identical_401_body_when_enabled",
        "Autheris.Tests.Integration.Modules.OpenMetadataModuleSecurityTests.Body_over_2_mb_returns_413_when_enabled",
        "Autheris.Tests.Architecture.ModuleBoundaryArchitectureTests.OpenMetadata_webhook_handler_referenced_only_by_core_webhook_endpoint",
        "Autheris.Tests.Architecture.ModuleBoundaryArchitectureTests.Signature_verifier_uses_fixed_time_compare"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_security_tests_migrated()
    {
        var (_, securityTests) = TestInventoryScanner.ScanRepository();

        var missing = ExpectedOpenMetadataSecurityTests
            .Where(t => !securityTests.Contains(t))
            .ToList();

        missing.ShouldBeEmpty(
            "Every OpenMetadata security test must exist and carry [Trait(\"Category\", \"Security\")]:\n" +
            string.Join("\n", missing));

        var moduleTestFiles = RepositoryScan.EnumerateFiles("modules/Autheris.Modules.OpenMetadata.Tests", "*.cs").ToList();
        moduleTestFiles.Count.ShouldBeGreaterThanOrEqualTo(8);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lineage_security_tests_migrated()
    {
        var (_, securityTests) = TestInventoryScanner.ScanRepository();

        var missing = ExpectedLineageSecurityTests
            .Where(t => !securityTests.Contains(t))
            .ToList();

        missing.ShouldBeEmpty(
            "Every lineage module security test must exist and carry [Trait(\"Category\", \"Security\")]:\n" +
            string.Join("\n", missing));

        RepositoryScan.EnumerateFiles("modules/Autheris.Modules.Lineage.Tests", "*.cs").ToList().Count.ShouldBeGreaterThanOrEqualTo(4);
    }
}
