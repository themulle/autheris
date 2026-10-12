namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class VirtualFilterShortCircuitEvaluatorTests
{
    private readonly IVirtualFilterSnapshotProvider _snapshotProvider = Substitute.For<IVirtualFilterSnapshotProvider>();
    private readonly IVirtualFilterKeyProvider _keyProvider = Substitute.For<IVirtualFilterKeyProvider>();

    private readonly TenantId _tenantId = new("tenant-alpha");
    private readonly TableIdentifier _targetTable = new("web_crm", "public", "customers");
    private readonly TableMetadata _metadata = new()
    {
        Identifier = new TableIdentifier("web_crm", "public", "customers"),
        Table = new Table { SourceName = "web_crm", SchemaName = "public", TableName = "customers", DataSourceType = DataSourceType.HttpDeclarative },
        Columns = new[] { new TableColumn { ColumnName = "id", DataType = "int" } }
    };

    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
        new Claim(ClaimTypes.Role, "SalesUser")
    }));

    private VirtualFilterAccessProfile CreateProfile(FilterBinding binding)
    {
        return new VirtualFilterAccessProfile
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Name = "profile_sales",
            GranteeType = GranteeType.Role,
            RoleName = "SalesUser",
            Scope = "web_crm.*.*",
            Uncovered = UncoveredPolicy.Skip,
            Bindings = new[] { binding },
            Status = FilterApprovalStatus.Active
        };
    }

    private VirtualFilter CreateFilter(string name, string[] keyColumns)
    {
        return new VirtualFilter
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Name = name,
            Source = "mssql_corp",
            KeyColumns = keyColumns,
            Status = FilterApprovalStatus.Active
        };
    }

    [Fact]
    public async Task EvaluateAsync_SingleKey_PointLookup_Hit_ReturnsAllowed()
    {
        var binding = new FilterBinding
        {
            FilterName = "vf_customers",
            TargetPattern = "^web_crm\\.public\\.customers$",
            ColumnMap = new Dictionary<string, string> { ["customer_id"] = "id" }
        };

        var filter = CreateFilter("vf_customers", new[] { "client.customer_id" });
        var profile = CreateProfile(binding);

        _snapshotProvider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<VirtualFilterSnapshot>(new VirtualFilterSnapshot(1L, new[] { filter }, new[] { profile })));

        _keyProvider.GetAllowedKeysAsync(filter, _user, _tenantId, "client.customer_id", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "42", "47", "100" }));

        var evaluator = new VirtualFilterShortCircuitEvaluator(_snapshotProvider, _keyProvider);

        var queriedLookups = new Dictionary<string, string> { ["id"] = "47" };
        var result = await evaluator.EvaluateAsync(_targetTable, _metadata, queriedLookups, _user, _tenantId);

        result.IsHandled.ShouldBeTrue();
        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_SingleKey_PointLookup_Miss_ReturnsDenied()
    {
        var binding = new FilterBinding
        {
            FilterName = "vf_customers",
            TargetPattern = "^web_crm\\.public\\.customers$",
            ColumnMap = new Dictionary<string, string> { ["customer_id"] = "id" }
        };

        var filter = CreateFilter("vf_customers", new[] { "client.customer_id" });
        var profile = CreateProfile(binding);

        _snapshotProvider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<VirtualFilterSnapshot>(new VirtualFilterSnapshot(1L, new[] { filter }, new[] { profile })));

        _keyProvider.GetAllowedKeysAsync(filter, _user, _tenantId, "client.customer_id", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "42", "47", "100" }));

        var evaluator = new VirtualFilterShortCircuitEvaluator(_snapshotProvider, _keyProvider);

        var queriedLookups = new Dictionary<string, string> { ["id"] = "999" };
        var result = await evaluator.EvaluateAsync(_targetTable, _metadata, queriedLookups, _user, _tenantId);

        result.IsHandled.ShouldBeTrue();
        result.IsAllowed.ShouldBeFalse();
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldContain("999");
    }

    [Fact]
    public async Task EvaluateAsync_CompositeKey_PointLookup_Hit_ReturnsAllowed()
    {
        var binding = new FilterBinding
        {
            FilterName = "vf_multitenant",
            TargetPattern = "^web_crm\\.public\\.customers$",
            CompositeKeys = new[] { "tenant_id", "client_id" }
        };

        var filter = CreateFilter("vf_multitenant", new[] { "org.tenant_id", "org.client_id" });
        var profile = CreateProfile(binding);

        _snapshotProvider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<VirtualFilterSnapshot>(new VirtualFilterSnapshot(1L, new[] { filter }, new[] { profile })));

        _keyProvider.GetAllowedKeyTuplesAsync(filter, _user, _tenantId, filter.KeyColumns, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["tenant_id"] = "corp", ["client_id"] = "47" }
            }));

        var evaluator = new VirtualFilterShortCircuitEvaluator(_snapshotProvider, _keyProvider);

        var queriedLookups = new Dictionary<string, string>
        {
            ["tenant_id"] = "corp",
            ["client_id"] = "47"
        };
        var result = await evaluator.EvaluateAsync(_targetTable, _metadata, queriedLookups, _user, _tenantId);

        result.IsHandled.ShouldBeTrue();
        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_CompositeKey_PointLookup_Miss_ReturnsDenied()
    {
        var binding = new FilterBinding
        {
            FilterName = "vf_multitenant",
            TargetPattern = "^web_crm\\.public\\.customers$",
            CompositeKeys = new[] { "tenant_id", "client_id" }
        };

        var filter = CreateFilter("vf_multitenant", new[] { "org.tenant_id", "org.client_id" });
        var profile = CreateProfile(binding);

        _snapshotProvider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<VirtualFilterSnapshot>(new VirtualFilterSnapshot(1L, new[] { filter }, new[] { profile })));

        _keyProvider.GetAllowedKeyTuplesAsync(filter, _user, _tenantId, filter.KeyColumns, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["tenant_id"] = "corp", ["client_id"] = "47" }
            }));

        var evaluator = new VirtualFilterShortCircuitEvaluator(_snapshotProvider, _keyProvider);

        var queriedLookups = new Dictionary<string, string>
        {
            ["tenant_id"] = "corp",
            ["client_id"] = "99"
        };
        var result = await evaluator.EvaluateAsync(_targetTable, _metadata, queriedLookups, _user, _tenantId);

        result.IsHandled.ShouldBeTrue();
        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_ShortCircuitOnly_NonPointQuery_ReturnsDenied()
    {
        var binding = new FilterBinding
        {
            FilterName = "vf_customers",
            TargetPattern = "^web_crm\\.public\\.customers$",
            ColumnMap = new Dictionary<string, string> { ["customer_id"] = "id" },
            Strategy = VirtualFilterExecutionStrategy.ShortCircuitOnly
        };

        var filter = CreateFilter("vf_customers", new[] { "client.customer_id" });
        var profile = CreateProfile(binding);

        _snapshotProvider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<VirtualFilterSnapshot>(new VirtualFilterSnapshot(1L, new[] { filter }, new[] { profile })));

        var evaluator = new VirtualFilterShortCircuitEvaluator(_snapshotProvider, _keyProvider);

        // No key lookups in query (e.g. SELECT * FROM customers)
        var queriedLookups = new Dictionary<string, string>();
        var result = await evaluator.EvaluateAsync(_targetTable, _metadata, queriedLookups, _user, _tenantId);

        result.IsHandled.ShouldBeTrue();
        result.IsAllowed.ShouldBeFalse();
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldContain("ShortCircuitOnly");
    }

    [Fact]
    public async Task EvaluateAsync_KeyProviderThrows_FailsClosed()
    {
        var binding = new FilterBinding
        {
            FilterName = "vf_customers",
            TargetPattern = "^web_crm\\.public\\.customers$",
            ColumnMap = new Dictionary<string, string> { ["customer_id"] = "id" }
        };

        var filter = CreateFilter("vf_customers", new[] { "client.customer_id" });
        var profile = CreateProfile(binding);

        _snapshotProvider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<VirtualFilterSnapshot>(new VirtualFilterSnapshot(1L, new[] { filter }, new[] { profile })));

        _keyProvider.GetAllowedKeysAsync(filter, _user, _tenantId, "client.customer_id", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlySet<string>>(Task.FromException<IReadOnlySet<string>>(new GatewaySecurityException("MSSQL database unreachable", "ERR_DB_UNAVAILABLE"))));

        var evaluator = new VirtualFilterShortCircuitEvaluator(_snapshotProvider, _keyProvider);

        var queriedLookups = new Dictionary<string, string> { ["id"] = "47" };

        await Should.ThrowAsync<GatewaySecurityException>(() =>
            evaluator.EvaluateAsync(_targetTable, _metadata, queriedLookups, _user, _tenantId).AsTask());
    }
}
