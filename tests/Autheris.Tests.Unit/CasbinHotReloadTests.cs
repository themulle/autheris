namespace Autheris.Tests.Unit;

using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class CasbinHotReloadTests : IDisposable
{
    private readonly CasbinEnforcementService _service = new();
    private readonly string _tempDir;

    public CasbinHotReloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casbin_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task LoadPolicyFromText_ShouldLoadAndEvaluateCsvPolicies()
    {
        // Arrange
        var tenant = new TenantId("tenant-csv");
        var userSid = new Sid("S-1-5-21-user-csv");
        var targetTable = new TableIdentifier("sales", "dbo", "orders");

        var csv = $"""
            p, {userSid.Value}, {tenant.Value}, {targetTable}, read, true, allow
            """;

        // Act
        _service.LoadPolicyFromText(tenant, csv);

        var context = new SecurityEvaluationContext(
            userSid,
            [],
            tenant,
            targetTable,
            [],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "TEST");

        var decision = await _service.EvaluatePolicyAsync(context);

        // Assert
        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ReloadPoliciesAsync_ShouldClearCacheAndBumpEpoch()
    {
        // Arrange
        var tenant = new TenantId("tenant-epoch");
        var userSid = new Sid("S-1-5-21-user-epoch");
        var targetTable = new TableIdentifier("sales", "dbo", "orders");

        _service.AddPolicy(tenant, userSid.Value, targetTable.ToString(), "read");
        var epochBefore = _service.CurrentEpoch;

        bool reloadedFired = false;
        _service.OnPolicyReloaded += t =>
        {
            if (t == tenant) reloadedFired = true;
        };

        // Act
        await _service.ReloadPoliciesAsync(tenant);

        // Assert
        _service.CurrentEpoch.ShouldBeGreaterThan(epochBefore);
        reloadedFired.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadPolicyFromFile_ShouldHotReloadPoliciesWhenFileChanges()
    {
        // Arrange
        var tenant = new TenantId("tenant-filewatch");
        var userSid = new Sid("S-1-5-21-filewatch-user");
        var targetTable = new TableIdentifier("finance", "dbo", "payroll");
        var policyFile = Path.Combine(_tempDir, "policy.csv");

        // Initially: deny
        File.WriteAllText(policyFile, $"""
            # initial empty policy
            """);

        _service.LoadPolicyFromFile(tenant, policyFile, watchFile: true);

        var context = new SecurityEvaluationContext(
            userSid,
            [],
            tenant,
            targetTable,
            [],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "TEST");

        var decisionBefore = await _service.EvaluatePolicyAsync(context);
        decisionBefore.IsAllowed.ShouldBeFalse();

        var reloadTcs = new TaskCompletionSource<bool>();
        _service.OnPolicyReloaded += t =>
        {
            if (t == tenant) reloadTcs.TrySetResult(true);
        };

        // Act: Update policy file on disk (simulate Kubernetes ConfigMap update)
        File.WriteAllText(policyFile, $"""
            p, {userSid.Value}, {tenant.Value}, {targetTable}, read, true, allow
            """);

        // Wait for FileSystemWatcher debounce event
        var reloaded = await Task.WhenAny(reloadTcs.Task, Task.Delay(3000));
        reloaded.ShouldBe(reloadTcs.Task);


        var decisionAfter = await _service.EvaluatePolicyAsync(context);
        decisionAfter.IsAllowed.ShouldBeTrue();
    }
}
