namespace Autheris.Tests.Unit.VirtualFilters;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Sql;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Virtual filters, phase 6: the audit entry of an access names the applied filters.</summary>
public sealed class VirtualFilterAuditTests
{
    private sealed class FixedOutcome(MandatoryFilterOutcome outcome) : IMandatoryRowFilterResolver
    {
        public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default) => ValueTask.FromResult(outcome);
    }

    private static readonly MandatoryFilterOutcome Applied = new(false, null, "1 = 1", ["nicht_ausgelieferte_krane"]);

    [Fact]
    public async Task TableQuery_AuditNamesTheVirtualFilters()
    {
        var table = MandatoryRowFilterResolverTests.Table("fms", "air1", "id", "client_id");
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(table);
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<System.DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var audit = Substitute.For<IAuditLogRepository>();
        var service = new GatewayExecutionService(metadata, consents, audit, resolution, Substitute.For<IConsentCacheService>(), Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(new GatewayOptions()), dataSourceExecutors: [], mandatoryFilters: new FixedOutcome(Applied));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LWE-DAVID"), new Claim("tenant_id", "tenant_lwe")], "Test"));

        await Should.ThrowAsync<System.Exception>(() => service.ExecuteTableQueryAsync(user, table.Identifier, 10, 0));   // no executor registered

        await audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "TABLE_QUERY" && e.DetailsJson.Contains("\"virtual_filters\":[\"nicht_ausgelieferte_krane\"]")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebSqlQuery_AuditNamesTheVirtualFiltersPerTable()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var (service, _, _) = WebSqlTwoPartNameDataSourceTests.CreateServiceWithCommand(audit, new FixedOutcome(Applied), WebSqlTwoPartNameDataSourceTests.CraneId);

        await service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest("SELECT id FROM lwetem_prod.md.crane"),
            WebSqlTwoPartNameDataSourceTests.CreateUser(), new TenantId(WebSqlTwoPartNameDataSourceTests.Tenant));

        await audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "WEBSQL_QUERY" && e.DetailsJson.Contains("nicht_ausgelieferte_krane")),
            Arg.Any<CancellationToken>());
    }
}
