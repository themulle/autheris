namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Profiling;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>Issue #127: shared fixtures for the scope-aware column level tests (catalog, analysis, parity).</summary>
internal static class ScopeLevelTestKit
{
    public const string Tenant = "tenant-a";
    public const string UserSid = "S-1-USER";
    public const string ScopeA = "scope-a";
    public const string ScopeB = "scope-b";

    /// <summary>Sentinels: any occurrence in a serialized response proves that a profile value leaked.</summary>
    public const string SentinelOne = "SENTINEL_ALLOWED_7f3a";
    public const string SentinelTwo = "SENTINEL_ALLOWED_9b2c";

    public static readonly TableIdentifier Orders = new("sales", "public", "orders");
    public static readonly string[] SentinelValues = [SentinelOne, SentinelTwo];

    /// <summary>The column under test (<c>grade</c>) carries sentinel allowed values; <c>id</c> is the plain column.</summary>
    public static TableMetadata CreateTable(
        DatasetVisibility visibility = DatasetVisibility.Hidden,
        bool releaseProfileToCatalogViewers = false,
        bool gradeSensitive = false,
        IReadOnlyDictionary<string, MaskingRule>? maskingRules = null) => new()
    {
        Identifier = Orders,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "public",
            TableName = "orders",
            DisplayName = "orders",
            Visibility = visibility
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn
            {
                ColumnName = "grade",
                DataType = "varchar",
                IsSensitive = gradeSensitive,
                Meta = new Dictionary<string, string> { ["accepted_values"] = JsonSerializer.Serialize(SentinelValues) }
            }
        ],
        ColumnMaskingRules = maskingRules ?? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase),
        PrimaryKeyColumns = ["id"],
        ReleaseProfileToCatalogViewers = releaseProfileToCatalogViewers,
        // Row filters (parity shapes) must not strip the profile values the tests look for.
        ProfilePublicationPolicy = DatasetProfilePublicationPolicy.FullCoarsenedProfile,
        EstimatedRowCount = 5000
    };

    public static ConsentColumnRule Rule(string column, ColumnAccessLevel level, params string[] scopes) =>
        new() { ColumnName = column, AccessLevel = level, ApplicableScopes = scopes };

    public static Consent Allow(params ConsentColumnRule[] rules) => Allow(Tenant, rules);

    public static Consent Allow(string tenant, params ConsentColumnRule[] rules) => new()
    {
        TableIdentifier = Orders,
        TenantId = new TenantId(tenant),
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(UserSid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = rules
    };

    /// <summary>Deny-effect consent; without rules it is an unconditional table Deny.</summary>
    public static Consent DenyEffect(params ConsentColumnRule[] rules) => new()
    {
        TableIdentifier = Orders,
        TenantId = new TenantId(Tenant),
        Effect = ConsentEffect.Deny,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(UserSid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = rules
    };

    public static ConsentRowFilter RowFilter(string value) => new()
    {
        ColumnName = "id",
        Operator = "EQ",
        ValueType = "int",
        ValueJson = value
    };

    /// <summary>Principal of <see cref="UserSid"/>; scopes arrive in one claim of <paramref name="scopeClaimType"/>.</summary>
    public static ClaimsPrincipal Principal(
        string? scopeClaimType = null,
        string? scopes = null,
        string tenant = Tenant,
        string authenticationType = "MCP",
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, UserSid),
            new("tenant_id", tenant)
        };

        if (scopeClaimType != null && scopes != null)
        {
            claims.Add(new Claim(scopeClaimType, scopes));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    public static ClaimsPrincipal WithScopes(params string[] scopes) =>
        scopes.Length == 0 ? Principal() : Principal("scp", string.Join(' ', scopes));

    public static IConsentRepository ConsentRepository(IReadOnlyList<Consent> consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(consents));
        return repo;
    }

    public static ITableMetadataRepository MetadataRepository(TableMetadata table)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        return repo;
    }

    public static McpDatasetCatalog CreateCatalog(
        TableMetadata table,
        IReadOnlyList<Consent> consents,
        IConsentResolutionService? resolver = null,
        ILogger<McpDatasetCatalog>? logger = null,
        GatewayOptions? gatewayOptions = null)
    {
        var profileService = new ColumnValueProfileService();
        return new McpDatasetCatalog(
            MetadataRepository(table),
            ConsentRepository(consents),
            Substitute.For<IGatewayExecutionService>(),
            auditLogRepository: Substitute.For<IAuditLogRepository>(),
            options: Options.Create(gatewayOptions ?? new GatewayOptions()),
            profileService: profileService,
            syntheticRowGenerator: new SyntheticSampleRowGenerator(profileService),
            consentResolution: resolver,
            logger: logger);
    }

    public static IConsentResolutionService ResolverReturning(Func<TableAccessDecision?> decision)
    {
        var resolver = Substitute.For<IConsentResolutionService>();
        resolver.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(), Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ => decision()!);
        return resolver;
    }

    public static string Json(object value) => JsonSerializer.Serialize(value);

    public sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception != null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }
}
