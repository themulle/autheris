namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(tables.FirstOrDefault(t => t.Identifier == ci.Arg<TableIdentifier>())));
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
        IRebacEvaluator? rebac = null,
        ILogger<CatalogDiscoveryService>? logger = null)
        => new(
            tables,
            Substitute.For<IAuditLogRepository>(),
            Microsoft.Extensions.Options.Options.Create(options ?? Options(TenancyShape.Multi)),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(tables, consents, options ?? Options(TenancyShape.Multi), rebac),
            Substitute.For<IKeyVaultSecretProvider>(),
            Substitute.For<IOpenApiIngestionService>(),
            searchEngine,
            logger ?? NullLogger<CatalogDiscoveryService>.Instance);
}
