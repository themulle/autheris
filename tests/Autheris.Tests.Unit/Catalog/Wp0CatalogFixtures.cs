namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Autheris.Tests.Common.Fakes;

/// <summary>Shared fixtures of the WP-0 catalog disclosure tests (consent-based visibility, no tiers).</summary>
internal static class Wp0
{
    public static readonly TenantId TenantA = new("tenant-a");
    public static readonly TenantId TenantB = new("tenant-b");

    /// <summary>The tenant claim of a Single-mode caller (DT-A-3: the claims transformation stamps it; the catalog services read claims only).</summary>
    public const string SingleTenantClaim = "default-tenant";

    public static GatewayOptions Options(TenancyShape? mode = null, params string[] sharedTables) => new()
    {
        Tenancy = (mode ?? TenancyShape.Multi).Options(),
        Catalog = new DataCatalogOptions { SharedTables = sharedTables }
    };

    public static ClaimsPrincipal User(string sid, string? tenant, string[]? roles = null, string authType = "Bearer", params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new(ClaimTypes.NameIdentifier, sid) };
        if (tenant != null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(extra);
        if (!extra.Any(c => c.Type is Autheris.Domain.Identity.GroupResolutionClaims.StatusClaimType or Autheris.Domain.Identity.GroupResolutionClaims.MarkerClaimType))
        {
            claims.Add(Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker());
            claims.Add(Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authType));
    }

    public static RequestContext Context(ClaimsPrincipal user)
        => new(user, user.FindFirst("tenant_id") is { } c ? new TenantId(c.Value) : Autheris.Tests.Common.TestTenants.Default);

    public static TableMetadata Table(
        string domain,
        string name,
        string description = "",
        string[]? columns = null,
        string? baseUrl = null,
        bool active = true,
        IReadOnlyDictionary<string, MaskingRule>? masking = null)
        => new()
        {
            Identifier = new TableIdentifier(domain, "dbo", name),
            Table = new Table
            {
                SourceName = domain,
                SchemaName = "dbo",
                TableName = name,
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = active,
                Description = description,
                HttpEndpoint = baseUrl == null ? null : new HttpEndpointDescriptor { BaseUrl = baseUrl }
            },
            Columns = (columns ?? ["id"]).Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
        };

    public static Consent Allow(TableIdentifier table, string sid, TenantId tenant, params string[] columns) => new()
    {
        TableIdentifier = table,
        TenantId = tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(sid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = columns.Select(c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
    };

    public static ITableMetadataRepository Tables(params TableMetadata[] tables)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.StubAllTables(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(tables.FirstOrDefault(t => t.Identifier == ci.Arg<TableIdentifier>())));
        repo.GetTablesAsync(Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var wanted = ci.Arg<IReadOnlyCollection<TableIdentifier>>().ToHashSet();
                return Task.FromResult<IReadOnlyList<TableMetadata>>(tables.Where(t => wanted.Contains(t.Identifier)).ToList());
            });
        repo.GetTablesForRequestAsync(Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<TableIdentifier>>();
                Autheris.Application.Catalog.CatalogTableQuery.ValidateRequestIdentifiers(ids);
                var wanted = ids.ToHashSet();
                return Task.FromResult<IReadOnlyList<TableMetadata>>(tables.Where(t => wanted.Contains(t.Identifier)).ToList());
            });
        repo.QueryTablesAsync(Arg.Any<Autheris.Application.Catalog.CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.Arg<Autheris.Application.Catalog.CatalogTableQuery>();
                query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
                var limit = query.EffectiveLimit;
                var matched = tables
                    .Where(t => query.IncludeInactive || t.Table.IsActive)
                    .Where(t => domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
                    .Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
                    .Where(t => namePrefix is null || t.Identifier.TableName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(t => after is null || Autheris.Application.Catalog.CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
                    .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
                    .Take(limit + 1)
                    .ToList();
                var hasMore = matched.Count > limit;
                var items = hasMore ? matched.Take(limit).ToList() : matched;
                var nextAfter = hasMore ? Autheris.Application.Catalog.CatalogTableCursor.Encode(items[^1].Identifier) : null;
                return Task.FromResult(new Autheris.Application.Catalog.CatalogTablePage(items, nextAfter));
            });
        return repo;
    }

    public static IConsentRepository Consents(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var subjects = ci.ArgAt<IEnumerable<Sid>>(0).ToHashSet();
                var tenant = ci.ArgAt<TenantId?>(3);
                return Task.FromResult<IReadOnlyList<Consent>>(
                    consents.Where(c => c.GranteeSid is { } g && subjects.Contains(g) && (tenant == null || c.TenantId == tenant)).ToList());
            });
        repo.GetTablesWithConsentsForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<TableIdentifier>>(
                consents.Where(c => c.TenantId == ci.Arg<TenantId>()).Select(c => c.TableIdentifier).Distinct().ToList()));
        return repo;
    }

    public static CatalogDiscoveryService Service(
        ITableMetadataRepository tables,
        IConsentRepository consents,
        GatewayOptions? options = null,
        ICatalogSearchEngine? searchEngine = null,
        ILogger<CatalogDiscoveryService>? logger = null)
        => new(
            tables,
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(new InMemoryAuditLogRepository()),
            Microsoft.Extensions.Options.Options.Create(options ?? Options(TenancyShape.Multi)),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(tables, consents, options ?? Options(TenancyShape.Multi)),
            Substitute.For<IKeyVaultSecretProvider>(),
            Substitute.For<IOpenApiIngestionService>(),
            searchEngine,
            logger ?? NullLogger<CatalogDiscoveryService>.Instance,
            consentRepository: consents);
}
