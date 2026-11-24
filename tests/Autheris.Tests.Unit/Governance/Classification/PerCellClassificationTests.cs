namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Protection;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "Security")]
public sealed class PerCellClassificationTests
{
    private static readonly TenantId Tenant = new("tenant-percell");
    private static readonly Sid UserSid = new("S-1-5-21-USER-PC");
    private static readonly TableIdentifier OrdersId = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static TableMetadata OrdersMetadata() => new()
    {
        Identifier = OrdersId,
        Table = new Table
        {
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "orders",
            SourceType = "PostgreSql",
            IsActive = true
        },
        Columns =
        [
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "INTEGER" },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "region", DataType = "TEXT" },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "ssn", DataType = "TEXT" }
        ],
        ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssn"] =
            [
                new ColumnClassification
                {
                    TableColumnId = "col-ssn",
                    Scope = ClassificationScopes.Global,
                    Code = ClassificationScopes.PiiCode,
                    Source = "manual",
                    AssignedBy = "owner",
                    AssignedAt = FixedNow.AddDays(-1)
                }
            ]
        }
    };

    private static Consent ScopedGrant(string regionValue)
    {
        var id = Guid.NewGuid();
        return new Consent
        {
            Id = id,
            TenantId = Tenant,
            TableIdentifier = OrdersId,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(30),
            RowFilters =
            [
                new ConsentRowFilter
                {
                    FilterGroup = 0,
                    ColumnName = "region",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = $"\"{regionValue}\""
                }
            ]
        };
    }

    private static async Task<(TableAccessDecision Decision, CompiledSql Compiled)> EvaluateAndCompileAsync(
        IReadOnlyList<Consent> consents,
        IClassificationRepository? classificationRepo = null,
        IUnmaskGrantResolver? unmask = null)
    {
        var meta = OrdersMetadata();
        var options = new GatewayOptions();
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var maskProvider = new SqlDataMaskingProvider(Options.Create(options), timeProvider: time);
        var maskFactory = new ColumnMaskPolicyFactory(maskProvider);
        var rowFactory = new RowPolicyFactory(new PolicyPredicateRenderer());
        var resolver = new ClassificationAccessResolver(maskProvider, classificationRepo, time);
        var compiler = new RowScopeCompiler(
            rowPolicyFactory: rowFactory,
            classificationResolver: resolver,
            timeProvider: time);

        var consentRepo = new TestInMemoryConsentRepository(consents, filterWindow: false);
        var resSvc = new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance);
        var policy = new TableAccessPolicy(
            consentRepo,
            resSvc,
            new TestNoOpConsentCache(),
            options,
            new NoOpMandatoryRowFilterResolver(),
            rowPolicyFactory: rowFactory,
            maskPolicyFactory: maskFactory,
            rowScopeCompiler: compiler,
            timeProvider: time,
            classificationResolver: resolver,
            unmaskResolver: unmask);

        var decision = await policy.DecideAsync(
            new TableAccessQuery(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(), meta, Claims: []),
            CancellationToken.None);

        var ordersIdentity = new TableIdentity("dbo", "orders");
        var catalogEntry = new TableCatalogEntry(
            ordersIdentity,
            meta.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(),
            meta.TenantColumnName,
            1L);
        var catalog = new InMemoryTableCatalog([catalogEntry], "dbo");
        var policyFactory = new GovernancePolicyFactory(
            new FixedCatalogProvider(catalog),
            new FixedMetaRepo([meta]),
            Options.Create(options));

        var sqlPolicy = await policyFactory.CreateAsync(
            new GovernedPolicyInput(
                "sales",
                Tenant,
                new Dictionary<TableIdentity, TableAccessDecision> { [ordersIdentity] = decision },
                new HashSet<TableIdentity>(),
                GovernedSubqueryStrategy.Correlated)
            {
                Metadata = new Dictionary<TableIdentity, TableMetadata>
                {
                    [ordersIdentity] = meta
                }
            },
            CancellationToken.None);

        var request = CompileRequestFactory.Create(
            sqlPolicy,
            TargetSqlDialect.PostgreSql,
            new WebSqlOptions(),
            isDml: false,
            enforcedMaxRows: 100,
            allowedFunctions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var compiled = new FastSqlEngine().Compile("SELECT id, ssn FROM dbo.orders".AsMemory(), request, CancellationToken.None);
        return (decision, compiled);
    }

    [Fact]
    public async Task RowScopedGrants_WithoutUnmaskGrant_KeepClassifiedColumnMaskedInEveryRow()
    {
        var (decision, compiled) = await EvaluateAndCompileAsync([ScopedGrant("EU"), ScopedGrant("US")]);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        ((ColumnMaskPolicy)decision.ColumnMasks!).Specs.ContainsKey("ssn").ShouldBeTrue();
        compiled.Parameters.Select(p => p.Value).ShouldContain("***");
    }

    [Fact]
    public async Task RowScopedGrants_WithUnmaskGrantOnTheColumn_ClearTheClassifiedColumn()
    {
        var unmask = new FixedUnmaskResolver(new UnmaskCoverage(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ssn" }));

        var (decision, _) = await EvaluateAndCompileAsync([ScopedGrant("EU"), ScopedGrant("US")], unmask: unmask);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task UnmaskGrantOnAnotherColumn_DoesNotClearTheClassifiedColumn()
    {
        var unmask = new FixedUnmaskResolver(new UnmaskCoverage(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "region" }));

        var (decision, _) = await EvaluateAndCompileAsync([ScopedGrant("EU"), ScopedGrant("US")], unmask: unmask);

        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task MultiGrantDifferentMaskCandidates_CombinedPolicySelectsStricterMaskSpec()
    {
        var resolver = new ClassificationAccessResolver(
            new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), new FakeClassificationSecretProvider(), timeProvider: new AdjustableClassificationTimeProvider(FixedNow)),
            timeProvider: new AdjustableClassificationTimeProvider(FixedNow));

        DataClassification Def(string code, short rank) => new()
        {
            Scope = Tenant.Value,
            Code = code,
            DisplayName = code,
            RestrictionRank = rank,
            IsActive = true,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        };
        ClassificationMaskRule Rule(string code, MaskFunction fn, string? paramsJson = null, string? keyRef = null) => new()
        {
            Scope = Tenant.Value,
            Code = code,
            MaskFunction = fn,
            ParamsJson = paramsJson,
            KeyRef = keyRef,
            CatalogText = CatalogTextVisibility.Visible,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        };

        var catalog = ClassificationAccessResolver.BuildCatalog(
            Tenant.Value,
            [Def("TAG_LIGHT", 20), Def("TAG_STRICT", 80)],
            [
                Rule("TAG_LIGHT", MaskFunction.Partial, "{\"prefix_chars\":1,\"suffix_chars\":1,\"mask_char\":\"*\"}"),
                Rule("TAG_STRICT", MaskFunction.Hash, keyRef: "test-hmac-key")
            ]);

        var metaLight = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "ssn", DataType = "TEXT" }],
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] =
                [
                    new ColumnClassification { TableColumnId = "c1", Scope = Tenant.Value, Code = "TAG_LIGHT", Source = "manual", AssignedBy = "a", AssignedAt = FixedNow },
                    new ColumnClassification { TableColumnId = "c1", Scope = Tenant.Value, Code = "TAG_STRICT", Source = "manual", AssignedBy = "a", AssignedAt = FixedNow }
                ]
            }
        };

        var g1 = ScopedGrant("EU");
        var g2 = ScopedGrant("US");

        var (perGrant, combined) = await resolver.ResolveForTableAsync(metaLight, [g1, g2], Tenant, null, catalog);

        perGrant[g1.Id]["ssn"].WinningMaskFunction.ShouldBe(MaskFunction.Hash);
        perGrant[g2.Id]["ssn"].WinningMaskFunction.ShouldBe(MaskFunction.Hash);
        combined["ssn"].WinningMaskFunction.ShouldBe(MaskFunction.Hash);
        combined["ssn"].MaskSpec!.Kind.ShouldBe(MaskKind.Hmac);
    }

    private sealed class FixedMetaRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
        public Task FlushPendingVersionChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct) => ValueTask.FromResult(catalog);
    }

    private sealed class FixedUnmaskResolver(UnmaskCoverage coverage) : IUnmaskGrantResolver
    {
        public ValueTask<UnmaskCoverage> ResolveAsync(
            TenantId tenant, IReadOnlyCollection<Sid> subjects, bool groupsComplete, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(coverage);
    }
}
