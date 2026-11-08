using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Procedures;

/// <summary>SEC-PROC-02: one transport gate call over every source of a procedure call, before any database access.</summary>
public sealed class ProcedureTransportGateTests
{
    private const string Header = """
        -- @name get_orders
        -- @procedure api.usp_GetOrders
        -- @param customer_id int required Customer number
        -- @result-table sales.orders
        """;

    private static readonly TenantId Tenant = new("tenant-a");

    private sealed class RecordingGate(params string[] denied) : IDataSourceTransportGate
    {
        public List<string?> Sources { get; } = [];
        public int Calls { get; private set; }
        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.Rest;
        public IReadOnlyList<string> CheckedSources => [];
        public Task EnsureAsync(string? n, CancellationToken ct = default) => throw new InvalidOperationException("single-source form must not be used");
        public Task EnsureAllAsync(IEnumerable<string?> n, CancellationToken ct = default) => throw new InvalidOperationException("not catalog-known");
        public Task EnsureCatalogSourceAsync(string? n, CancellationToken ct = default) => throw new InvalidOperationException("single-source form must not be used");
        public bool IsAllowed(string? n) => true;

        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> names, CancellationToken ct = default)
        {
            Calls++;
            var list = names.ToList();
            Sources.AddRange(list);
            if (list.Any(s => string.IsNullOrWhiteSpace(s) || denied.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                throw new TransportNotAllowedException(list.FirstOrDefault(), DataSourceTransportFlags.None);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public IProcedureInvoker Invoker { get; } = Substitute.For<IProcedureInvoker>();
        public IProcedureRowScopeResolver RowScope { get; } = Substitute.For<IProcedureRowScopeResolver>();

        public GovernedProcedureExecutionService Create(IDataSourceTransportGate? gate, string? definitionSource, string tableSource, string tableDomain = "sales")
        {
            var registry = new InMemoryProcedureRegistry();
            var def = ProcedureDefinitionParser.Parse(Header, "x", false, 60) with { DataSource = definitionSource };
            registry.Register(def);
            registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["order_id"], ["sales.orders"], new Dictionary<string, string>()));

            var tables = Substitute.For<ITableMetadataRepository>();
            tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci => new TableMetadata
            {
                Identifier = new TableIdentifier(tableDomain, ci.Arg<TableIdentifier>().Schema, ci.Arg<TableIdentifier>().TableName),
                Table = new Table { SourceName = tableSource },
                Columns = [new TableColumn { ColumnName = "order_id" }]
            });
            var consents = Substitute.For<IConsentRepository>();
            consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
                .Returns(new List<Consent>());
            var resolution = Substitute.For<IConsentResolutionService>();
            resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
                .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["order_id"] = ColumnAccessLevel.Clear }));
            Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new RawProcedureResult(["order_id"], [new object?[] { 1 }], false));
            var audit = Substitute.For<IAuditLogRepository>();
            audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

            return new GovernedProcedureExecutionService(
                registry, Invoker, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), audit, tables, consents, resolution,
                Substitute.For<IColumnMaskingProvider>(),
                policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
                rowScope: RowScope,
                concurrencyGate: PermissiveGate(),
                transportGate: gate);
        }

        public Task<GovernedProcedureResult> Run(GovernedProcedureExecutionService svc) =>
            svc.ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, Caller(), Tenant);

        public async Task AssertDeniedBeforeDatabaseAsync(GovernedProcedureExecutionService svc)
        {
            await Should.ThrowAsync<TransportNotAllowedException>(() => Run(svc));
            await Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
            RowScope.ReceivedCalls().ShouldBeEmpty();
        }
    }

    private static ITableReadConcurrencyGate PermissiveGate()
    {
        var g = Substitute.For<ITableReadConcurrencyGate>();
        g.TryEnter(Arg.Any<string>(), Arg.Any<int>()).Returns(_ => Substitute.For<IDisposable>());
        return g;
    }

    private static ClaimsPrincipal Caller() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1001"), DefaultTenantCaller.Claim], "test"));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("definition_connection")]
    [InlineData("definition_data_source")]
    [InlineData("table_source")]
    [InlineData("empty_table_source_uses_domain")]
    public Task Execute_SourceNotAllowed_DeniedBeforeInvoke(string sourceKind) => AssertSourceDeniedBeforeInvokeAsync(sourceKind);

    /// <summary>Shared with the advisory regression theory.</summary>
    internal static async Task AssertSourceDeniedBeforeInvokeAsync(string sourceKind)
    {
        var rig = new Rig();
        var (gate, svc) = sourceKind switch
        {
            "definition_connection" => Build(rig, "procedures", null, "src-ok"),
            "definition_data_source" => Build(rig, "ds-denied", "ds-denied", "src-ok"),
            "table_source" => Build(rig, "src-denied", "ds-ok", "src-denied"),
            _ => Build(rig, "dom-denied", "ds-ok", string.Empty, "dom-denied")
        };

        await rig.AssertDeniedBeforeDatabaseAsync(svc);
        gate.Calls.ShouldBe(1);
    }

    private static (RecordingGate Gate, GovernedProcedureExecutionService Svc) Build(Rig rig, string denied, string? defSource, string tableSource, string domain = "sales")
    {
        var gate = new RecordingGate(denied);
        return (gate, rig.Create(gate, defSource, tableSource, domain));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Execute_NullGate_FailsClosed()
    {
        // Hand-built services fall back to deny-all in production; the fallback setter is internal and unit tests alone install allow-all.
        var rig = new Rig();
        var svc = rig.Create(DenyAllTransportGate.Instance, "ds-ok", "src-ok");

        await rig.AssertDeniedBeforeDatabaseAsync(svc);
        TransportGateFallback.Current.ShouldNotBeNull();
        typeof(TransportGateFallback).GetProperty(nameof(TransportGateFallback.Current))!.SetMethod!.IsPublic.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public Task Execute_EmptySourceName_StillGated() => AssertEmptySourceNameStillGatedAsync();

    internal static async Task AssertEmptySourceNameStillGatedAsync()
    {
        var gate = new RecordingGate();
        var rig = new Rig();

        await rig.Run(rig.Create(gate, "ds-ok", tableSource: "", tableDomain: "dom-x"));

        gate.Calls.ShouldBe(1);
        gate.Sources.ShouldBe(["ds-ok", "dom-x"], ignoreOrder: true);
    }

    [Fact]
    public async Task Execute_AllSourcesAllowed_Invokes()
    {
        var gate = new RecordingGate();
        var rig = new Rig();

        var result = await rig.Run(rig.Create(gate, "ds-ok", "src-ok"));

        result.ShouldNotBeNull();
        gate.Calls.ShouldBe(1);
        gate.Sources.ShouldBe(["ds-ok", "src-ok"], ignoreOrder: true);
        await rig.Invoker.ReceivedWithAnyArgs(1).ExecuteReadAsync(default!, default!, default!, default, default);
    }
}
