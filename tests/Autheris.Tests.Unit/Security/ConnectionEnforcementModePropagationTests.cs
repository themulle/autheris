#pragma warning disable CA2012
namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConnectionEnforcementModePropagationTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-503");
    private static readonly TableIdentifier OrdersId = new("sales", "dbo", "orders");

    private static TableMetadata BuildTable(EnforcementMode tableMode = EnforcementMode.AutherisEnforced) => new()
    {
        Identifier = OrdersId,
        OwnerTenantId = Tenant.Value,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "orders",
            DataSourceType = DataSourceType.Sql,
            EnforcementMode = tableMode,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "ssn", DataType = "varchar" }
        ]
    };

    private static GatewayOptions OptionsWithConnectionMode(EnforcementMode mode) => new()
    {
        Tenancy = TenancyShape.Multi.Options(),
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales"] = new DataSourceConnectionOptions
                {
                    Provider = "Databricks",
                    ConnectionString = "Host=example",
                    EnforcementMode = mode
                }
            }
        }
    };

    [Fact]
    public void WithConnectionEnforcementMode_PropagatesDelegatedAndDefenceInDepth_AndPreservesExplicitTableMode()
    {
        var defaultTable = BuildTable(EnforcementMode.AutherisEnforced);

        var delegated = AccessDecisionPoint.WithConnectionEnforcementMode(defaultTable, OptionsWithConnectionMode(EnforcementMode.Delegated));
        delegated.EnforcementMode.ShouldBe(EnforcementMode.Delegated);
        delegated.IsDelegatedSource.ShouldBeTrue();

        var defenceInDepth = AccessDecisionPoint.WithConnectionEnforcementMode(defaultTable, OptionsWithConnectionMode(EnforcementMode.DelegatedDefenceInDepth));
        defenceInDepth.EnforcementMode.ShouldBe(EnforcementMode.DelegatedDefenceInDepth);
        defenceInDepth.IsDelegatedSource.ShouldBeFalse();

        var explicitDelegated = BuildTable(EnforcementMode.Delegated);
        var preserved = AccessDecisionPoint.WithConnectionEnforcementMode(explicitDelegated, OptionsWithConnectionMode(EnforcementMode.AutherisEnforced));
        preserved.EnforcementMode.ShouldBe(EnforcementMode.Delegated);
        preserved.IsDelegatedSource.ShouldBeTrue();
    }

    [Theory]
    [InlineData(EnforcementMode.Delegated, true)]
    [InlineData(EnforcementMode.DelegatedDefenceInDepth, false)]
    [InlineData(EnforcementMode.AutherisEnforced, false)]
    public async Task DecideAsync_PropagatesConnectionEnforcementMode_IntoPolicyEvaluation(EnforcementMode connectionMode, bool expectDelegated)
    {
        var table = BuildTable(EnforcementMode.AutherisEnforced);
        var now = DateTimeOffset.UtcNow;
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            TableIdentifier = OrdersId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1),
            RowFilters =
            [
                new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EMEA\"" }
            ]
        };

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([consent]));

        var opts = Options.Create(OptionsWithConnectionMode(connectionMode));
        var sources = new TypedPolicySources(
            new RowPolicyFactory(new PolicyPredicateRenderer()),
            new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(opts)));

        var pdp = new AccessDecisionPoint(
            opts,
            new ConsentResolutionService(),
            consents,
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            typedPolicies: sources);

        var subject = new SecurityPrincipalContext
        {
            UserSid = UserSid,
            TenantId = Tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        var decision = await pdp.DecideAsync(
            new AccessRequest(subject, table, AccessOperation.Read, AccessTransport.WebSql));

        decision.IsAllowed.ShouldBeTrue();
        if (expectDelegated)
        {
            decision.RowPolicy.ShouldBeNull();
            decision.CombinedRowFilterSql.ShouldBeNull();
        }
        else
        {
            decision.RowPolicy.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task EpochValidation_DelegatedConnection_BypassesStalenessMicroCache()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"3"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.IsConnected.Returns(true);
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var sensitivity = Substitute.For<ITableSensitivityLookup>();
        sensitivity.HasSensitiveDataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(false));

        var time = new FakeTimeProvider();
        using var service = new EpochValidationService(
            Options.Create(OptionsWithConnectionMode(EnforcementMode.Delegated)),
            Substitute.For<IEventBus>(),
            sensitivity,
            mux,
            timeProvider: time);

        (await service.AreEpochsValidAsync([new EpochCheck(OrdersId, 3, IsHighlySensitive: false, IsDelegatedSource: false)]))[OrdersId].ShouldBeTrue();
        time.Advance(TimeSpan.FromMilliseconds(50));
        (await service.AreEpochsValidAsync([new EpochCheck(OrdersId, 3, IsHighlySensitive: false, IsDelegatedSource: false)]))[OrdersId].ShouldBeTrue();

        // Connection-level EnforcementMode=Delegated bypasses the local staleness micro-cache, so Redis is queried twice.
        await db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }
}
