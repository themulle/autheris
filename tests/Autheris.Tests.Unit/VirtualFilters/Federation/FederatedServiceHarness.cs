namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TrinoSqlEngine;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// The real WebSQL cross-source pipeline for service-level tests: consent policy (consent bypass off), Virtual Filter
/// resolver over a fixed snapshot, router, planner, staging with counting connector stubs and a real DuckDB engine.
/// </summary>
internal static class FederatedServiceHarness
{
    internal const string AliceSid = "S-1-5-21-VF-ALICE";

    internal static Consent Allow(TableIdentifier table, params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = table,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(AliceSid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = filters
    };

    internal static IConsentRepository Consents(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.TableIdentifier == ci.ArgAt<TableIdentifier>(1)).ToList()));
        return repo;
    }

    /// <summary>A profile of Alice with one binding of <paramref name="filterName"/> (cross-source, key client_id -> id).</summary>
    internal static VirtualFilterSnapshot AliceSnapshot(string scope, string targetPattern, string filterName = "vf_customers")
    {
        var filter = VirtualFilterModelTests.DavidFilter(filterName) with { TenantId = Tenant };
        var profile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "alice",
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(AliceSid),
            Scope = scope,
            Uncovered = UncoveredPolicy.Deny,
            Bindings =
            [
                new FilterBinding
                {
                    FilterName = filterName,
                    TargetPattern = targetPattern,
                    ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" }
                }
            ]
        };
        return new VirtualFilterSnapshot(1, [filter], [profile]);
    }

    /// <summary>
    /// The service under test. <see cref="KeySetProvider"/> is the key set source of the federation planner (WI-6):
    /// tests configure the authorized keys per filter name and assert which filters were loaded.
    /// </summary>
    internal sealed class Pipeline
    {
        public required FederatedDuckDbExecutionService Service { get; init; }
        public required IAuditLogRepository Audit { get; init; }
        public required FakeKeySetProvider KeySetProvider { get; init; }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RunAsync(string sql) => (await RunFullAsync(sql)).Rows;

        public Task<GovernedSqlResult> RunFullAsync(string sql) => RunAsAsync(User(AliceSid), sql);

        public Task<GovernedSqlResult> RunAsAsync(System.Security.Claims.ClaimsPrincipal user, string sql) =>
            Service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql, Transport: "WebSql"), user, Tenant);
    }

    internal static Pipeline Build(
        IReadOnlyList<(TableMetadata Table, StubTableConnector Stub)> tables,
        IConsentRepository? consents,
        VirtualFilterSnapshot? snapshot = null,
        FakeKeySetProvider? keySets = null,
        bool withPlanner = true,
        int? timeoutSeconds = null,
        int? maxStagedRowsPerTable = null,
        VirtualFilterFederationOptions? federation = null,
        List<(LogLevel Level, string Message)>? logs = null,
        bool consentBypass = false,
        IMandatoryRowFilterResolver? resolverOverride = null)
    {
        var repository = Substitute.For<ITableMetadataRepository>();
        foreach (var (table, _) in tables)
        {
            repository.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        }

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = tables[0].Table.Identifier.Domain,
                AllowedDataSources = tables.Select(t => t.Table.Identifier.Domain).Distinct().ToList(),
                CrossSource = new CrossSourceOptions { Enabled = true, AllowedTransports = ["WebSql"], TimeoutSeconds = timeoutSeconds ?? 0, MaxStagedRowsPerTable = maxStagedRowsPerTable ?? 0 }
            },
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", MaxTempDirectorySize = "0B" },
            VirtualFilters = new VirtualFilterOptions { Federation = federation ?? new VirtualFilterFederationOptions() },
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = consentBypass }
        };

        var resolver = new MandatoryRowFilterResolver(
            new MandatoryRowFilterResolverTests.FixedSnapshot(snapshot ?? new VirtualFilterSnapshot(1, [], [])),
            new MandatoryRowFilterResolverTests.PlaceholderPredicates());

        var audit = Substitute.For<IAuditLogRepository>();
        var keySetProvider = keySets ?? new FakeKeySetProvider();

        var service = new FederatedDuckDbExecutionService(
            Options.Create(options),
            audit,
            new CrossSourceQueryRouter(repository, options.WebSql.CrossSource, options.WebSql),
            new CrossSourcePlanner(FastSqlEngine.Default),
            Staging(StubRegistry.For(tables.ToArray()), options, audit, logs == null ? null : new ListLogger<FederatedStagingService>(logs)),
            new DuckDbOlapEngine(options.DuckDbOlap, NullLogger<DuckDbOlapEngine>.Instance),
            sqlEngine: FastSqlEngine.Default,
            consentResolution: consents == null ? null : new ConsentResolutionService(),
            consentRepository: consents,
            mandatoryFilters: resolverOverride ?? resolver,
            logger: logs == null ? null : new ListLogger<FederatedDuckDbExecutionService>(logs),
            vfPlanner: withPlanner ? new VirtualFilterFederationPlanner(keySetProvider, Options.Create(options)) : null);

        return new Pipeline { Service = service, Audit = audit, KeySetProvider = keySetProvider };
    }
}
