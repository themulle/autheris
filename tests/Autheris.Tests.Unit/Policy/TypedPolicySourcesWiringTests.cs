using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>
/// WP-X0l: every place that creates the access decision hands the typed policy producers to <see cref="TableAccessPolicy"/>. A site
/// without them would silently produce string-only decisions, which the compiler path denies for every filtered or masked table.
/// </summary>
public sealed class TypedPolicySourcesWiringTests
{
    private static readonly TenantId Tenant = new("tenant_a");
    private static readonly Sid UserSid = new("S-1-5-21-USER");
    private static readonly TableIdentifier Orders = new("sales", "main", "orders");

    private static TypedPolicySources Sources() => new(
        new RowPolicyFactory(new PolicyPredicateRenderer()),
        new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(Options.Create(new GatewayOptions()))));

    private static TableMetadata Meta() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "sales", SchemaName = "main", TableName = "orders", SourceType = "SQLite", IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "INTEGER" }, new TableColumn { ColumnName = "region", DataType = "TEXT" }]
    };

    private static Consent EuConsent() => new()
    {
        TableIdentifier = Orders,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" }]
    };

    private static IConsentRepository Consents()
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([EuConsent()]));
        return repo;
    }

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, UserSid.Value), new Claim("tenant_id", Tenant.Value)], "Bearer"));

    private static void AssertTyped(TableAccessDecision decision)
    {
        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldBe(decision.RowPolicy!.Project().Sql);
        decision.RowFilterParameters!.Values.ShouldContain("EU");
        decision.RequireTypedRowPolicy().ShouldBeSameAs(decision);
    }

    [Fact]
    public async Task UnifiedPolicyDecisionPoint_ProducesATypedDecision()
    {
        var pdp = new UnifiedPolicyDecisionPoint(Consents(), new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance, Options.Create(new GatewayOptions()),
            NullLogger<UnifiedPolicyDecisionPoint>.Instance, typedPolicies: Sources());
        var context = new SecurityPrincipalContext
        {
            UserSid = UserSid,
            TenantId = Tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        AssertTyped(await pdp.EvaluateAccessAsync(Orders, Meta(), context, null, CancellationToken.None));
    }

    [Fact]
    public async Task CrossDomainResolver_ProducesATypedDecision()
    {
        var resolver = new DefaultCrossDomainAccessResolver(Consents(), new ConsentResolutionService(), typedPolicies: Sources());

        AssertTyped(await resolver.ResolveAccessAsync(User(), Orders, Meta(), Tenant));
    }

    [Fact]
    public async Task ProcedureService_ProducesATypedDecision()
    {
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Meta());
        var service = new GovernedProcedureExecutionService(
            new InMemoryProcedureRegistry(), Substitute.For<IProcedureInvoker>(), Options.Create(new GatewayOptions()),
            Substitute.For<IAuditLogRepository>(), tables, Consents(), new ConsentResolutionService(), Substitute.For<IColumnMaskingProvider>(),
            typedPolicies: Sources());

        var result = await service.EvaluateTableAsync("sales", "main.orders", User(), UserSid, Tenant, consentBypassed: false, allowRowFilter: true, CancellationToken.None);

        result.ShouldNotBeNull();
        AssertTyped(result.Value.Decision);
    }

    [Fact]
    public async Task WebSqlRewriter_ProducesATypedDecision()
    {
        var rewriter = new GovernedSqlRewriter(Options.Create(new GatewayOptions()), consentResolution: new ConsentResolutionService(),
            consentRepository: Consents(), typedPolicies: Sources());

        AssertTyped(await Decide(rewriter.AccessPolicy()));
    }

    [Fact]
    public async Task FederatedService_ProducesATypedDecision()
    {
        var service = new FederatedDuckDbExecutionService(
            Options.Create(new GatewayOptions()), Substitute.For<IAuditLogRepository>(), Substitute.For<ICrossSourceQueryRouter>(),
            Substitute.For<ICrossSourcePlanner>(), Substitute.For<IFederatedStagingService>(), Substitute.For<IDuckDbOlapEngine>(),
            consentResolution: new ConsentResolutionService(), consentRepository: Consents(), typedPolicies: Sources());

        AssertTyped(await Decide(service.AccessPolicy()));
    }

    [Fact]
    public async Task GatewayExecutionService_ProducesATypedDecision()
    {
        var service = new GatewayExecutionService(
            Substitute.For<ITableMetadataRepository>(), Consents(), Substitute.For<IAuditLogRepository>(), new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance, Substitute.For<IColumnMaskingProvider>(),
            typedPolicies: Sources());

        AssertTyped(await Decide(service.AccessPolicy()));
    }

    private static Task<TableAccessDecision> Decide(TableAccessPolicy policy) =>
        policy.DecideAsync(new TableAccessQuery(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(), Meta()), CancellationToken.None);

    // ---- DI -------------------------------------------------------------------------------------------------------

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = $"Data Source=typed_wiring_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" }
        };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddGatewayInfrastructure(options);
        return services.BuildServiceProvider();
    }

    private static IEnumerable<object?> FieldsOf(object? service, Type type) =>
        service?.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(f => f.FieldType == type).Select(f => f.GetValue(service)) ?? [];

    [Fact]
    public void Registration_ProvidesTheTypedPolicySourcesOnce()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<TypedPolicySources>().ShouldBeSameAs(provider.GetRequiredService<TypedPolicySources>());
        provider.GetRequiredService<ColumnMaskPolicyFactory>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData(typeof(IGatewayExecutionService))]
    [InlineData(typeof(IProcedureExecutionService))]
    [InlineData(typeof(IUnifiedPolicyDecisionPoint))]
    [InlineData(typeof(IFederatedQueryExecutionService))]
    public async Task DecisionPoints_GetTheTypedPolicySources(Type serviceType)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService(serviceType);

        FieldsOf(service, typeof(TypedPolicySources)).ShouldHaveSingleItem().ShouldNotBeNull();
    }

    [Fact]
    public async Task WebSqlService_RewriterGetsTheTypedPolicySources()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var rewriter = FieldsOf(service, typeof(GovernedSqlRewriter)).Single();

        FieldsOf(rewriter, typeof(TypedPolicySources)).ShouldHaveSingleItem().ShouldNotBeNull();
    }

    [Fact]
    public async Task CrossDomainResolver_PolicyGetsTheTypedFactories()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ICrossDomainAccessResolver>();

        var policy = FieldsOf(resolver, typeof(TableAccessPolicy)).Single();

        FieldsOf(policy, typeof(RowPolicyFactory)).ShouldHaveSingleItem().ShouldNotBeNull();
        FieldsOf(policy, typeof(ColumnMaskPolicyFactory)).ShouldHaveSingleItem().ShouldNotBeNull();
    }
}
