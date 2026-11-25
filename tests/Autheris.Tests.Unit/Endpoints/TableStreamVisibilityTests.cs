namespace Autheris.Tests.Unit.Endpoints;

using Autheris.Application.Governance.Ownership;
using Autheris.Application.Policy.Pdp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class TableStreamVisibilityTests
{
    private sealed class SingleTableMetadataRepo(TableMetadata? table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier identifier, CancellationToken ct = default) =>
            Task.FromResult(table is not null && table.Identifier == identifier ? table : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>(table is null ? [] : [table]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier tableId,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FixedAllowPdp(TableIdentifier tableId) : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>(), null, true));

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyConsentRepo : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, Autheris.Domain.Security.ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) =>
            Task.FromResult(new Autheris.Application.Governance.Paging.ConsentPage([], null));
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, Autheris.Domain.Security.ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopSqlExecutionService : IGovernedSqlExecutionService
    {
        public Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request, ClaimsPrincipal caller, TenantId tenantId,
            Func<DbDataReader, CancellationToken, Task> onResultAsync, CancellationToken ct = default)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(int));
            dt.Rows.Add(1);
            return onResultAsync(dt.CreateDataReader(), ct);
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
            GovernedSqlQueryRequest request, ClaimsPrincipal caller, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ValidateAsync(
            string sql, ClaimsPrincipal caller, TenantId tenantId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => rawValue;
    }

    private sealed class FixedOwnershipRepo(string ownerSid) : IContainerOwnership
    {
        public Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerOwnerEntry>>([]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid userSid, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(userSid.Value, ownerSid, StringComparison.Ordinal));
    }

    private static TableMetadata CreateHiddenTable(bool isActive = true) => new()
    {
        Identifier = new TableIdentifier("sales", "dbo", "secret_orders"),
        Table = new Table
        {
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "secret_orders",
            SourceType = "PostgreSQL",
            IsActive = isActive,
            DiscoveryHidden = true,
        },
        PrimaryKeyColumns = ["id"],
        Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
    };

    private static DefaultHttpContext CreateHttpContext(ClaimsPrincipal principal, string path, IServiceCollection? extraServices = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.User = principal;

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        if (extraServices is not null)
        {
            foreach (var descriptor in extraServices)
            {
                ((IList<ServiceDescriptor>)services).Add(descriptor);
            }
        }
        context.RequestServices = services.BuildServiceProvider();
        return context;
    }

    private static async Task<int> InvokeStreamAsync(
        TableMetadata table,
        ClaimsPrincipal principal,
        string path,
        IServiceCollection? extraServices = null)
    {
        var context = CreateHttpContext(principal, path, extraServices);
        var repo = new SingleTableMetadataRepo(table);
        var pdp = new FixedAllowPdp(table.Identifier);
        var planner = new GovernedTableReadPlanner();
        var sqlExecution = new NoopSqlExecutionService();
        var encoder = new KeysetCursorEncoder("visibility-tests-signing-key-0123456789ab");
        var masking = new NoopMaskingProvider();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "secret_orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        return context.Response.StatusCode;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Bulk_and_stream_match_single_row_visibility_for_non_admin()
    {
        var hiddenTable = CreateHiddenTable(isActive: true);
        var nonAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "alice"), new Claim("sid", "S-1-alice"), new Claim("tenant", "default")], "Bearer"));

        var extra = new ServiceCollection();
        var repo = new SingleTableMetadataRepo(hiddenTable);
        var consents = new EmptyConsentRepo();
        extra.AddSingleton<ICatalogVisibilityService>(new CatalogVisibilityService(
            repo, consents, Options.Create(new GatewayOptions()), NullLogger<CatalogVisibilityService>.Instance));

        var visibilityService = extra.BuildServiceProvider().GetRequiredService<ICatalogVisibilityService>();
        var singleRow = await visibilityService.FindVisibleAsync(
            new CatalogCaller(nonAdmin, new TenantId("default"), CatalogChannel.Rest),
            hiddenTable.Identifier);
        singleRow.ShouldBeNull();

        (await InvokeStreamAsync(hiddenTable, nonAdmin, "/api/v1/data/sales/dbo/secret_orders/stream", extra))
            .ShouldBe(StatusCodes.Status404NotFound);
        (await InvokeStreamAsync(hiddenTable, nonAdmin, "/api/v1/data/sales/dbo/secret_orders/bulk", extra))
            .ShouldBe(StatusCodes.Status404NotFound);

        // Missing caller identity must fail closed with 404 (never fall back to "anonymous")
        var noIdentity = new ClaimsPrincipal(new ClaimsIdentity());
        (await InvokeStreamAsync(CreateHiddenTable(isActive: true) with
        {
            Table = new Table
            {
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "secret_orders",
                SourceType = "PostgreSQL",
                IsActive = true,
            }
        }, noIdentity, "/api/v1/data/sales/dbo/secret_orders/stream"))
            .ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Role_named_admin_without_admin_right_sees_no_hidden_table()
    {
        var fakeAdmin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "mallory"),
            new Claim("sid", "S-1-mallory"),
            new Claim("tenant", "default"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("role", "admin")
        ], "Bearer"));

        var hiddenTable = CreateHiddenTable(isActive: true);
        (await InvokeStreamAsync(hiddenTable, fakeAdmin, "/api/v1/data/sales/dbo/secret_orders/stream"))
            .ShouldBe(StatusCodes.Status404NotFound);
        (await InvokeStreamAsync(hiddenTable, fakeAdmin, "/api/v1/data/sales/dbo/secret_orders/bulk"))
            .ShouldBe(StatusCodes.Status404NotFound);

        var canonicalClusterAdmin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "sysadmin"),
            new Claim("sid", "S-1-cluster-admin"),
            new Claim("tenant", "default"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        ], "Bearer"));

        var inactiveTable = CreateHiddenTable(isActive: false);
        (await InvokeStreamAsync(inactiveTable, fakeAdmin, "/api/v1/data/sales/dbo/secret_orders/stream"))
            .ShouldBe(StatusCodes.Status404NotFound);
        (await InvokeStreamAsync(inactiveTable, canonicalClusterAdmin, "/api/v1/data/sales/dbo/secret_orders/stream"))
            .ShouldBe(StatusCodes.Status404NotFound);

        (await InvokeStreamAsync(hiddenTable, canonicalClusterAdmin, "/api/v1/data/sales/dbo/secret_orders/stream"))
            .ShouldBe(StatusCodes.Status200OK);

        var ownerServices = new ServiceCollection();
        ownerServices.AddSingleton<IContainerOwnership>(new FixedOwnershipRepo("S-1-owner"));
        var owner = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "owner"), new Claim(ClaimTypes.PrimarySid, "S-1-owner"), new Claim("tenant", "default")], "Bearer"));
        (await InvokeStreamAsync(hiddenTable, owner, "/api/v1/data/sales/dbo/secret_orders/bulk", ownerServices))
            .ShouldBe(StatusCodes.Status200OK);
    }
}
