namespace TrinoSqlEngine.Tests.Security;

/// <summary>Where the control behind a legacy security ID lives, and so what "the same or a stricter outcome on the AST path" means for it.</summary>
internal enum LegacyIdKind
{
    /// <summary>A control of the SQL compiler path: at least one AST test (<c>Compile</c>) in this assembly guards it.</summary>
    Compiler,

    /// <summary>A control of the gateway, the application services or configuration. It has no AST-path form; the owner suite keeps it.</summary>
    Application,

    /// <summary>The string is not a security control (a ticket key, test data).</summary>
    NotAControl
}

/// <summary>One legacy ID (<c>SEC-</c>, <c>SQ-</c>, <c>SQL-</c>, <c>SR15-</c>) with the tests that keep it guarded.</summary>
/// <param name="Id">The ID as written in the tests.</param>
/// <param name="Kind">See <see cref="LegacyIdKind"/>.</param>
/// <param name="Summary">One line: what the ID protects.</param>
/// <param name="AstTests"><c>Class.Method</c> of tests in this assembly that assert the outcome on the AST path (reflection-checked).</param>
/// <param name="Owners">Test files (relative to <c>tests/</c>) that carry the ID today; the ID must appear in each (typo check).</param>
/// <param name="ElsewhereAstTests">AST-path tests of other test projects, as <c>relative/path.cs#Method</c> (text-checked).</param>
/// <param name="Note">A difference to legacy that X1 has to know about.</param>
internal sealed record LegacyIdEntry(
    string Id,
    LegacyIdKind Kind,
    string Summary,
    string[] AstTests,
    string[] Owners,
    string[]? ElsewhereAstTests = null,
    string? Note = null);

/// <summary>A Phase 3 ID (<c>SEC-ADG-nn</c>), the work packages that own it, and the evidence that exists on this branch.</summary>
/// <param name="Evidence"><c>relative/path.cs::token</c> pairs (relative to <c>tests/</c>); the token must occur in the file.</param>
internal sealed record Phase3Entry(string Id, string Summary, string[] WorkPackages, string[] Evidence);

/// <summary>
/// WP-X0v, G7: the traceability catalog (plan section 9.3, B6). <see cref="SecurityRegressionTraceabilityTests"/> extracts every
/// legacy ID from <c>tests/**/*.cs</c> and fails when one is missing here, when a Compiler entry has no AST test, or when a test
/// named here does not exist. It is the list the cutover PR (WP-X1) is reviewed against: legacy may only be deleted when every
/// Compiler row is green on the AST path.
/// </summary>
internal static class SecurityRegressionCatalog
{
    private const string Ast = nameof(AstSecurityRegressionTests);
    private const string SqReview = "TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs";
    private const string Remediation = "TrinoSqlEngine.Tests/SecurityRemediationTests.cs";
    private const string Vf = "Autheris.Tests.Unit/VirtualFilters/";
    private const string VfFed = "Autheris.Tests.Unit/VirtualFilters/Federation/";

    private static string[] T(params string[] methods) => methods.Select(m => $"{Ast}.{m}").ToArray();

    public static IReadOnlyList<LegacyIdEntry> Legacy { get; } = new LegacyIdEntry[]
    {
        // ---- SQ: SecurityReview20261002 (parser, lexer and rewriter differentials) -----------------------------------------
        new("SQ-01", LegacyIdKind.Compiler, "string literal lexer differential: E'...' and backslashes",
            T(nameof(AstSecurityRegressionTests.SQ01_BackslashInAStringLiteral_IsRejected),
              nameof(AstSecurityRegressionTests.SQ01_EscapedStringTypeConstructor_IsRejected),
              nameof(AstSecurityRegressionTests.SQ01_StandardEscapedSingleQuote_IsAllowedAndBound)), new[] { SqReview }),
        new("SQ-02", LegacyIdKind.Compiler, "comment lexer differential, nested block comments, dollar quoting",
            T(nameof(AstSecurityRegressionTests.SQ02_NestedBlockCommentsAndAllComments_AreRejected),
              nameof(AstSecurityRegressionTests.SQ02_DollarQuotedStrings_AreRejected)), new[] { SqReview }),
        new("SQ-03", LegacyIdKind.Compiler, "whole-row reference in DML bypassing masking",
            T(nameof(AstSecurityRegressionTests.SQ03_WholeRowReferenceInDml_IsRejected_WhenTheTableHasMaskedColumns),
              nameof(AstSecurityRegressionTests.SQ03_DmlWithoutMaskedColumns_StillCompiles)), new[] { SqReview }),
        new("SQ-04", LegacyIdKind.Compiler, "cross-schema policy confusion via a short table name",
            T(nameof(AstSecurityRegressionTests.SQ04_ATableInAnotherSchema_NeverResolvesToTheCatalogTableOfTheSameShortName)), new[] { SqReview }),
        new("SQ-05", LegacyIdKind.Compiler, "dialect rewriting: simple alias, root row cap as OFFSET/FETCH",
            T(nameof(AstSecurityRegressionTests.SQ05_TableAlias_IsTheSimpleName_NeverSchemaQualified),
              nameof(AstSecurityRegressionTests.SQ05_SqlServer_RootRowCap_IsOffsetFetch_NotLimit)), new[] { SqReview }),
        new("SQ-06", LegacyIdKind.Compiler, "sensitive function denylist (PostgreSQL, SQL Server)",
            T(nameof(AstSecurityRegressionTests.SQ06_SensitiveBuiltinFunctions_AreRejected_OnEveryDialect)), new[] { SqReview, Remediation }),
        new("SQ-07", LegacyIdKind.Compiler, "INSERT into a table with a consent row filter",
            T(nameof(AstSecurityRegressionTests.SQ07_InsertIntoATableWithAConsentRowFilter_IsRejected)), new[] { SqReview }),
        new("SQ-08", LegacyIdKind.Compiler, "native thread stack protection and nesting limits",
            T(nameof(AstSecurityRegressionTests.SQ08_DeepNesting_IsRejectedBeforeTheStackRunsOut),
              nameof(AstSecurityRegressionTests.SQ08_VeryDeepNesting_FailsClosedWithATypedError_OnTheAstPath)), new[] { SqReview, Remediation }),
        new("SQ-09", LegacyIdKind.Compiler, "multi-part table names (linked servers, catalogs)",
            T(nameof(AstSecurityRegressionTests.SQ09_FourPartNames_AreRejected),
              nameof(AstSecurityRegressionTests.SQ09_ThreePartName_NeverEmitsTheForeignCatalog_AndIsSecuredAsTheCatalogTable)), new[] { SqReview, "Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs" },
            Note: "Compile ignores the catalog part of a three-part name (it resolves to the cataloged table and secures it); legacy rejected a mismatch in the WebSQL service. X1 must keep that service check."),
        new("SQ-10", LegacyIdKind.Compiler, "non-ASCII case folding is not applied to identifiers",
            T(nameof(AstSecurityRegressionTests.SQ10_NonAsciiLookalikes_DoNotResolveToACatalogTable)), new[] { SqReview }),
        new("SQ-11", LegacyIdKind.Compiler, "dots in quoted identifiers",
            T(nameof(AstSecurityRegressionTests.SQ11_DotsInQuotedIdentifiers_AreRejected)), new[] { SqReview, Remediation, "Autheris.Tests.Unit/Round4ParserGatewayTests.cs" }),
        new("SQ-13", LegacyIdKind.Compiler, "time-travel queries",
            T(nameof(AstSecurityRegressionTests.SQ13_TimeTravelQueries_AreRejected)), new[] { SqReview, Remediation }),
        new("SQ-14", LegacyIdKind.Compiler, "a pooled parser carries no state to the next compile",
            T(nameof(AstSecurityRegressionTests.SQ14_PooledParsers_DoNotLeakStateAcrossCompiles)), new[] { SqReview }),
        new("SQ-15", LegacyIdKind.Application, "unlimited DML affected rows is a DANGER configuration entry",
            Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Round4ParserGatewayTests.cs" }),

        // ---- SQL: the SQL review series -----------------------------------------------------------------------------------------
        new("SQL-1", LegacyIdKind.Compiler, "bracket lexer differential (SQL Server, SQLite) and DML on the tenant column",
            T(nameof(AstSecurityRegressionTests.SQL1_BracketsAndBracketLiterals_AreRejected),
              nameof(AstSecurityRegressionTests.SQL1_TenantColumnOfTheCatalog_CannotBeUpdated_AndAnInsertMustCarryTheBoundTenant)),
            new[] { "TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs", "Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs" }),
        new("SQL-2", LegacyIdKind.Compiler, "two spellings of one table must not share or overwrite a policy",
            T(nameof(AstSecurityRegressionTests.SQL2_TwoSpellingsOfOneTable_AreBothSecured_NeverOnePolicyForTwoRelations)),
            new[] { "Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs" }),
        new("SQL-3", LegacyIdKind.Compiler, "safe token emission of cast types and EXTRACT fields",
            T(nameof(AstSecurityRegressionTests.SQL3_QuotedOrUnknownCastTypesAndExtractFields_AreRejected)), new[] { "TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs" }),
        new("SQL-4", LegacyIdKind.Compiler, "HMAC masked columns compile on the AST path",
            Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Security/AstCompilerHmacSql4Tests.cs" },
            ElsewhereAstTests: new[] { "Autheris.Tests.Unit/Security/AstCompilerHmacSql4Tests.cs#AstCompiler_HmacMaskedColumn_IsRewritten" }),
        new("SQL-5", LegacyIdKind.Compiler, "the catalog part of a three-part name",
            T(nameof(AstSecurityRegressionTests.SQL5_ACatalogPart_IsNeverEmitted_AndEveryTableReferenceIsSecured)),
            new[] { "Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs", "Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs" },
            Note: "See SQ-09: the AST path does not reject a mismatched catalog, it never emits it."),
        new("SQL-6", LegacyIdKind.Compiler, "join guardrail: masked columns in joins, no false positive",
            T(nameof(AstSecurityRegressionTests.SQL6_MaskedColumnInAJoin_IsRejected_ButAnUnrelatedJoinIsNotAFalsePositive)),
            new[] { "Autheris.Tests.Unit/Security/Sql6JoinGuardrailTests.cs" }),
        new("SQL-7", LegacyIdKind.Compiler, "the enforced row cap never ends up in a DML subquery",
            T(nameof(AstSecurityRegressionTests.SQL7_DmlSubquery_HasNoRowCap_AndTheRootSelectKeepsIt)),
            new[] { "TrinoSqlEngine.Tests/DmlLimitAndAliasSql78Tests.cs", "Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs" }),
        new("SQL-8", LegacyIdKind.Compiler, "the alias of a rewritten table keeps the quoting of the written name",
            T(nameof(AstSecurityRegressionTests.SQL8_QuotedTableName_KeepsAQuotedAlias),
              nameof(AstSecurityRegressionTests.SQL8_UnquotedTableName_KeepsAnUnquotedStyleAlias)), new[] { "TrinoSqlEngine.Tests/DmlLimitAndAliasSql78Tests.cs" }),

        // ---- SEC and SR15: gateway, virtual filter and identity controls (no AST-path form) -----------------------------------
        new("SEC-1", LegacyIdKind.Application, "access decisions are keyed by the source-qualified table name", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs", VfFed + "FederatedDecisionKeyTests.cs" }),
        new("SEC-2", LegacyIdKind.Application, "row filters of HTTP tables are enforced by the gateway", Array.Empty<string>(), new[] { VfFed + "FederatedStagingRowFilterRegressionTests.cs" }),
        new("SEC-4", LegacyIdKind.Application, "key query runs the complete structured filter definition", Array.Empty<string>(), new[] { VfFed + "StructuredFilterKeyQueryBuilderTests.cs", VfFed + "SqlVirtualFilterKeySetProviderTests.cs" }),
        new("SEC-5", LegacyIdKind.Application, "key query runs the complete structured filter definition", Array.Empty<string>(), new[] { VfFed + "StructuredFilterKeyQueryBuilderTests.cs", VfFed + "SqlVirtualFilterKeySetProviderTests.cs" }),
        new("SEC-6", LegacyIdKind.Application, "key query runs the complete structured filter definition", Array.Empty<string>(), new[] { VfFed + "StructuredFilterKeyQueryBuilderTests.cs", VfFed + "SqlVirtualFilterKeySetProviderTests.cs" }),
        new("SEC-7", LegacyIdKind.Application, "regex target patterns match the full name", Array.Empty<string>(), new[] { Vf + "TargetPatternMatcherTests.cs", Vf + "MandatoryRowFilterResolverCrossSourceTests.cs" }),
        new("SEC-8", LegacyIdKind.Application, "regex target patterns match the full name", Array.Empty<string>(), new[] { Vf + "TargetPatternMatcherTests.cs", Vf + "MandatoryRowFilterResolverCrossSourceTests.cs" }),
        new("SEC-10", LegacyIdKind.Application, "single-flight key loading is per cache key", Array.Empty<string>(), new[] { VfFed + "VirtualFilterKeySetCacheTests.cs", VfFed + "SqlVirtualFilterKeySetProviderTests.cs" }),
        new("SEC-11", LegacyIdKind.Application, "a key source failure never quotes data values", Array.Empty<string>(), new[] { VfFed + "WebSqlGatewayDenialMappingTests.cs", VfFed + "SqlVirtualFilterKeySetProviderTests.cs" }),
        new("SEC-12", LegacyIdKind.Application, "the request timeout bounds key loading", Array.Empty<string>(), new[] { VfFed + "VirtualFilterFederationPlannerTests.cs" }),
        new("SEC-13", LegacyIdKind.Application, "a miss does not return rows where a hit is denied", Array.Empty<string>(), new[] { VfFed + "FederatedStagingRowFilterRegressionTests.cs", VfFed + "GovernedConnectorReaderVirtualFilterTests.cs" }),
        new("SEC-14", LegacyIdKind.Application, "a connector cannot supply its own enforcement", Array.Empty<string>(), new[] { VfFed + "GovernedConnectorReaderVirtualFilterTests.cs" }),
        new("SEC-15", LegacyIdKind.Application, "pushdown capability derives from the endpoint descriptor only", Array.Empty<string>(), new[] { VfFed + "HttpKeyPushdownCapabilityTests.cs" }),
        new("SEC-17", LegacyIdKind.Application, "an oversized upstream body aborts while it is read", Array.Empty<string>(), new[] { VfFed + "ResponseSizeLimitingHandlerTests.cs" }),
        new("SEC-18", LegacyIdKind.Application, "key values compare as typed canonical values", Array.Empty<string>(), new[] { VfFed + "VirtualFilterKeyCanonicalizerTests.cs" }),
        new("SEC-19", LegacyIdKind.Application, "consent bypass without consent services still resolves virtual filters", Array.Empty<string>(), new[] { VfFed + "FederatedVirtualFilterEnforcementTests.cs" }),
        new("SEC-20", LegacyIdKind.Application, "decision identity includes the snapshot generation", Array.Empty<string>(), new[] { Vf + "TableAccessDecisionVirtualFilterTests.cs" }),
        new("SEC-94", LegacyIdKind.Application, "issue #94 key definitions, schema shape verification and read ordering (SEC-94-nn of its plan)", Array.Empty<string>(),
            new[]
            {
                "Autheris.Tests.Architecture/ArchitectureNoSourceKeyCatalogQueriesTests.cs",
                "Autheris.Tests.Integration/CatalogKeyDefinitionContractTestsBase.cs",
                "Autheris.Tests.Integration/CatalogKeysEndpointAuthIntegrationTests.cs",
                "Autheris.Tests.Integration/PostgreSqlGovernanceLeastPrivilegeTests.cs",
                "Autheris.Tests.Integration/PostgreSqlGovernancePostConditionTests.cs",
                "Autheris.Tests.Integration/PostgreSqlGovernanceSchemaShapeTests.cs",
                "Autheris.Tests.Integration/SqlServerGovernanceLeastPrivilegeTests.cs",
                "Autheris.Tests.Integration/SqlServerGovernancePostConditionTests.cs",
                "Autheris.Tests.Integration/SqlServerGovernanceSchemaShapeTests.cs",
                "Autheris.Tests.Unit/Catalog/CatalogGovernanceRatchetTests.cs",
                "Autheris.Tests.Unit/Catalog/CatalogIngestionUnavailableTableTests.cs",
                "Autheris.Tests.Unit/Catalog/CatalogKeysEndpointTests.cs",
                "Autheris.Tests.Unit/Catalog/DbtKeyConstraintTests.cs",
                "Autheris.Tests.Unit/Catalog/OpenApiIngestionKeyExtensionTests.cs",
                "Autheris.Tests.Unit/Catalog/OpenMetadataKeyConstraintTests.cs",
                "Autheris.Tests.Unit/Governance/GovernanceSchemaCatalogReaderTests.cs",
                "Autheris.Tests.Unit/Governance/GovernanceSchemaShapeVerifierTests.cs",
                "Autheris.Tests.Unit/Governance/GovernanceSchemaVerificationTests.cs",
                "Autheris.Tests.Unit/Governance/SqliteCatalogKeyDefinitionContractTests.cs",
                "Autheris.Tests.Unit/Governance/TableKeyDefinitionValidatorTests.cs",
                "Autheris.Tests.Unit/OData/ODataCsdlKeyVisibilityTests.cs",
                "Autheris.Tests.Unit/OData/ODataNextLinkTests.cs",
                "Autheris.Tests.Unit/WebSqlTimeoutProblemDetailsTests.cs"
            },
            Note: "Gateway, catalog and governance-storage controls; no SQL compiler path. The plan ids SEC-94-01..15 share this row."),
        new("SEC-06", LegacyIdKind.Application, "client tier is not granted by a substring of the key", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/ClientTierResolverSecurityTests.cs" }),
        new("SEC-111", LegacyIdKind.Application, "PLAN-111 review findings (SEC-111-NN): dataset tags and catalog visibility tiers fail closed, owner rules, 404-before-403 order, no tier on the data path, audited changes", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Catalog/DatasetCatalogAdminServiceTests.cs" }),
        new("SEC-112", LegacyIdKind.Application, "PLAN-DATA-112 review findings (SEC-112-NN): governed aggregation privacy hardening", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Analytics/AggregationPrivacyHardeningTests.cs", "Autheris.Tests.Unit/Analytics/DifferentialPrivacyNoiseAndGraphQlMetricsTests.cs" }),
        new("SEC-113", LegacyIdKind.Application, "PLAN-GOV-113 review findings (SEC-113-NN): consent decisions only by persons, grant policy before the step, guarded status updates", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/ConsentRequests/ConsentDecisionGuardTests.cs", "Autheris.Tests.Unit/ConsentRequests/ConsentDecisionStatusGuardContractBase.cs" }),
        new("SEC-115", LegacyIdKind.Application, "PLAN-SEC-115 review findings (SEC-115-NN): read-path switches fail closed (REST data family, MCP tool groups, resources, prompts, invoke_api composition), no oracle, validated configuration", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/OptionsBinding/ReadPathSwitchOptionsValidatorTests.cs", "Autheris.Tests.Unit/Mcp/McpToolGroupsCompletenessTests.cs", "Autheris.Tests.Unit/Mcp/ReadPathSwitchesTests.cs", "Autheris.Tests.Unit/Mcp/McpToolSwitchTests.cs", "Autheris.Tests.Unit/Mcp/McpResourceSwitchTests.cs", "Autheris.Tests.Unit/Mcp/ApiDispatcherSwitchTests.cs", "Autheris.Tests.Unit/Api/DisabledReadPathObserverMiddlewareTests.cs", "Autheris.Tests.Integration/ReadPathSwitchIntegrationTests.cs" }),
        new("SEC-131", LegacyIdKind.Application, "PLAN-FED-131 review findings (SEC-131-NN): federated failure audit without driver text", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/VirtualFilters/Federation/FederatedFailureAuditHygieneTests.cs" }),
        new("SEC-007", LegacyIdKind.NotAControl, "test data of an ingress/egress test (not a finding ID)", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/IngressEgressTests.cs" }),
        new("SEC-882", LegacyIdKind.NotAControl, "a ticket key in ITSM client tests (not a finding ID)", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/ItsmClientsAndRecertificationTests.cs", "Autheris.Extensions.Tests/ItsmClientTests.cs" }),
        new("SR15-06", LegacyIdKind.Application, "four-eyes approval of virtual filters and access profiles", Array.Empty<string>(), new[] { Vf + "VirtualFilterFourEyesApprovalTests.cs" }),
        new("SR15-07", LegacyIdKind.Application, "forced filter sync needs two roles", Array.Empty<string>(), new[] { Vf + "VirtualFilterEndpointsTests.cs" }),
        new("SR15-11", LegacyIdKind.Application, "basic-auth lockout keys in multi-key operations", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Security/Api13BasicAuthLockoutAtomicTests.cs" }),
        new("SR15-29", LegacyIdKind.Application, "OAuth advertisement without a tenant id", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Security/GatewayMcpOAuthTests.cs" }),
        new("SR15-31", LegacyIdKind.Application, "OData entity set of another tenant", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/ODataEndpointsTests.cs" }),
        new("SR15-32", LegacyIdKind.Application, "denied or masked column in filter and orderBy", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/GatewayExecutionRequestValidationTests.cs" }),
        new("SR15-38", LegacyIdKind.Application, "unknown basic-auth users cost a PBKDF2 round", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/BasicAuthOptimizationTests.cs" }),
        new("SR15-39", LegacyIdKind.Application, "MCP advertises the agent scope", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Security/GatewayMcpOAuthTests.cs" }),
        new("SR15-41", LegacyIdKind.Application, "degraded epoch store fails closed on sensitive tables", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Security/EpochRedisFailureInf1Tests.cs" }),
        new("SR15-43", LegacyIdKind.Application, "WebSQL short-name catalog fallback to the default domain", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Sql/GovernedSqlExecutionMultiSchemaShortNameTests.cs" },
            Note: "WebSQL service behavior; the AST path resolves short names through the catalog default schema (SQ-04)."),
        new("SR15-47", LegacyIdKind.Application, "dynamic status reporting of filter states", Array.Empty<string>(), new[] { Vf + "VirtualFilterEndpointsTests.cs" }),
        new("SR15-49", LegacyIdKind.Application, "error messages do not leak internal database details", Array.Empty<string>(), new[] { Vf + "VirtualFilterEndpointsTests.cs" }),
        new("SR15-52", LegacyIdKind.Application, "a user-specific consent deny beats an unconstrained allow", Array.Empty<string>(), new[] { "Autheris.Tests.Unit/Security/McpResourceColumnFilterMcp1Tests.cs" })
    };

    /// <summary>The Phase 3 findings: which work packages own them and what already guards them on this branch.</summary>
    public static IReadOnlyList<Phase3Entry> Phase3 { get; } = new Phase3Entry[]
    {
        new("SEC-ADG-01", "plan cache key, tenant/policy values rebound", new[] { "WP-A8", "WP-X1", "WP-B5" },
            new[] { "TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs::SEC-ADG-01", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::Cache_Rebinds_TenantValueOfTheCurrentRequest" }),
        new("SEC-ADG-02", "consumers of the string producers (G10)", new[] { "WP-D1", "WP-D2", "WP-B5", "WP-E2", "WP-X1" }, new[] { "Autheris.Tests.SqlCompilerGate/Parity/ConsumerParityTests.cs::SEC-ADG-02", "Autheris.Tests.SqlCompilerGate/Parity/DuplicateParameterTests.cs::SEC-ADG-02", "Autheris.Tests.SqlCompilerGate/Parity/RendererInvariantTests.cs::SEC-ADG-02", "Autheris.Tests.Architecture/GovernancePolicyArchitectureTests.cs::SEC-ADG-02" }),
        new("SEC-ADG-03", "Oracle BindByName", new[] { "WP-F1", "WP-F3", "WP-F4" },
            new[] { "TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs::SEC-ADG-03", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::Binder_Oracle_EnablesAndVerifiesBindByName" }),
        new("SEC-ADG-04", "byte-exact tenant comparison", new[] { "WP-A1", "WP-D1", "WP-F2", "WP-C1", "WP-B5" },
            new[] { "TrinoSqlEngine.Tests/Compiler/TenantPredicateStructureTests.cs::TenantPredicateFactory", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::TenantComparison_IsByteExact" }),
        new("SEC-ADG-05", "compile DoS: depth, stack, time", new[] { "WP-A2", "WP-A4", "WP-A8" },
            new[] { "TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs::SEC-ADG-05", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::ExecutionStack_Exhaustion_FailsClosed" }),
        new("SEC-ADG-06", "check option for policy-referenced columns", new[] { "WP-A7" },
            new[] { "TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs::SecAdg06", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::Dml_PolicyColumnAssignment_IsRejected" }),
        new("SEC-ADG-07", "gateway and database resolve names alike", new[] { "WP-A4", "WP-A7", "WP-F2" },
            new[] { "Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs::SEC-ADG-07", "TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs::QuotedCteVsUnquotedPhysical_CaseVariants_AlwaysSecured" }),
        new("SEC-ADG-08", "MERGE: closed clause set, unique-key side channel", new[] { "WP-A7", "WP-A2" },
            new[] { "TrinoSqlEngine.Tests/Compiler/DmlCoverageTests.cs::SEC-ADG-08", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::Verifier_RejectsAnUnknownMergeClause" }),
        new("SEC-ADG-09", "Databricks REST client SSRF controls", new[] { "WP-C3", "WP-C5" }, new[] { "Autheris.Tests.Unit/Databricks/DatabricksStartupValidationTests.cs::SEC-ADG-09", "Autheris.Tests.SqlCompilerGate/WorkflowPolicyTests.cs::SEC-ADG-09" }),
        new("SEC-ADG-10", "${...} variable substitution", new[] { "WP-C1", "WP-C4" },
            new[] { "TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs::Databricks_IdentifierWithDollarOrBrace_Rejected", "TrinoSqlEngine.Tests/Compiler/CompileLimitsTests.cs::Databricks_VariableSubstitutionGuard_IsForcedEvenWhenTheCallerPassesNone" }),
        new("SEC-ADG-11", "policy subqueries are tenant-scoped", new[] { "WP-A5", "WP-D3" },
            new[] { "TrinoSqlEngine.Tests/Compiler/PolicyExpressionParserTests.cs::SEC-ADG-11", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::PolicySubquery" }),
        new("SEC-ADG-12", "policy value producers (inline consent values)", new[] { "WP-D1" }, new[] { "Autheris.Tests.Architecture/RowPolicyArchitectureTests.cs::SEC-ADG-12" }),
        new("SEC-ADG-13", "CI gate sufficiency (mutants per PR, flake policy, cache poisoning)", new[] { "WP-B3", "WP-B4", "WP-B5", "WP-E2", "WP-E3", "WP-X1" },
            new[] { "TrinoSqlEngine.Tests/Security/SecurityMutantGateTests.cs::Mutant_IsKilled", "TrinoSqlEngine.Tests/Security/SecurityTestPolicyTests.cs::SecurityTests_AreNeverRetried" }),
        new("SEC-ADG-14", "session context initializer fails closed for unknown providers", new[] { "WP-F2", "WP-D4" },
            new[] { "Autheris.Tests.Unit/Security/FailClosedDialectDefaultsTests.cs::SEC-ADG-14" }),
        new("SEC-ADG-15", "HMAC key as a bound parameter", new[] { "WP-A6", "WP-D2", "WP-F6" }, new[] { "Autheris.Tests.Integration/AstCompilerSqliteExecutionTests.cs::Sqlite_HmacMask_UsesGatewayUdf", "Autheris.Tests.Unit/Security/AstCompilerHmacSql4Tests.cs::AstCompiler_HmacMaskedColumn_IsRewritten", "TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs::DBMS_CRYPTO" }),
        new("SEC-ADG-16", "side channels: errors and predicate order", new[] { "WP-A3", "WP-X1", "WP-B5" },
            new[] { "TrinoSqlEngine.Tests/Compiler/ColumnBindTypeTests.cs::SEC-ADG-16", "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::ErrorSanitizer_NeverEchoesTheProviderMessage" }),
        new("SEC-ADG-17", "Oracle empty string is NULL", new[] { "WP-F3", "WP-A5" },
            new[] { "TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs::SEC-ADG-17" }),
        new("SEC-ADG-18", "LIKE semantics in policy predicates", new[] { "WP-A3", "WP-A5" },
            new[] { "TrinoSqlEngine.Tests/Compiler/PolicyExpressionParserTests.cs::SEC-ADG-18" }),
        new("SEC-ADG-19", "client parameters cannot alias internal markers", new[] { "WP-A2", "WP-X1" },
            new[] { "TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs::SEC-ADG-19" }),
        new("SEC-ADG-20", "policy parse cache key", new[] { "WP-A5" },
            new[] { "TrinoSqlEngine.Tests/Compiler/PolicyExpressionParserTests.cs::SEC-ADG-20" }),
        new("SEC-ADG-21", "federated DuckDB plan with inline literals (transitional)", new[] { "WP-A2", "WP-X3" }, new[] { "Autheris.Tests.Unit/Security/DuckDbBoundParameterTests.cs::DuckDbBoundParameterTests", "Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs::FederatedPlan_BindsParameters" }),
        new("SEC-ADG-22", "table functions and passthrough", new[] { "WP-A4", "WP-A8" },
            new[] { "TrinoSqlEngine.Tests/Compiler/FunctionAllowlistTests.cs::FunctionAllowlist" }),
        new("SEC-ADG-23", "audit digest of the executed statement", new[] { "WP-A8", "WP-X1" }, new[] { "TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs::CompiledSqlDigest" }),
        new("SEC-ADG-24", "supply chain (Oracle, Spark/Delta)", new[] { "WP-F1", "WP-C4", "WP-E2" },
            new[] { "Autheris.Tests.Architecture/OracleDriverArchitectureTests.cs::SEC-ADG-24", "Autheris.Tests.Integration/AstCompilerDatabricksSparkDmlExecutionTests.cs::SEC-ADG-24" }),
        new("SEC-ADG-25", "typed and string policy must not drift", new[] { "WP-D1" }, new[] { "Autheris.Tests.Architecture/TypedAccessDecisionArchitectureTests.cs::SEC-ADG-25", "Autheris.Tests.Unit/Policy/TypedAccessDecisionTests.cs::SEC-ADG-25" }),
        new("SEC-ADG-26", "policy migration lint", new[] { "WP-D1", "WP-X1" }, new[] { "Autheris.Tests.Unit/Policy/Lint/PolicyPredicateLintTests.cs::SEC-ADG-26" }),
        new("SEC-ADG-27", "bind and IN-list limits", new[] { "WP-A1" },
            new[] { "TrinoSqlEngine.Tests/Security/SecurityProbes.cs::InListLimit_Oracle" }),
        new("SEC-ADG-28", "fail fast on the removed WebSql:SqlRewriterEngine key", new[] { "WP-X1" }, Array.Empty<string>()),
        new("SEC-ADG-29", "token guard profiles", new[] { "WP-A8", "WP-C1", "WP-F3" },
            new[] { "TrinoSqlEngine.Tests/Compiler/CompileLimitsTests.cs::TokenGuards" })
    };
}
