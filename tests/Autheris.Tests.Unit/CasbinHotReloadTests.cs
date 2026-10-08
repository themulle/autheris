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
            if (t == tenant.Value) reloadedFired = true;
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

        try
        {
            _service.LoadPolicyFromFile(tenant, policyFile, watchFile: true);
        }
        catch (IOException ex) when (ex.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            // In constrained container environments (e.g. max_user_instances=128), skip file watching assertion
            return;
        }

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
            if (t == tenant.Value) reloadTcs.TrySetResult(true);
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

    [Fact]
    public async Task LoadPolicyFromText_GlobalReload_EmptyOrCommentOnly_RejectsAndPreservesPolicies_FailsClosed()
    {
        // Arrange
        var tenant = new TenantId("tenant-c1");
        var userSid = new Sid("S-1-5-21-user-c1");
        var targetTable = new TableIdentifier("sales", "dbo", "orders");

        var initialCsv = $"""
            p, {userSid.Value}, {tenant.Value}, {targetTable}, read, true, allow
            """;
        _service.LoadPolicyFromText(initialCsv);

        _service.HasPolicies(tenant).ShouldBeTrue();

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
        decisionBefore.IsAllowed.ShouldBeTrue();

        // Act & Assert: Loading empty or comment-only policy text must be rejected fail-closed
        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            _service.LoadPolicyFromText("# only comments\n# no rules");
        });
        ex.Message.ShouldContain("empty");

        // Last-known-good policies must remain active
        _service.HasPolicies(tenant).ShouldBeTrue();
        var decisionAfter = await _service.EvaluatePolicyAsync(context);
        decisionAfter.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluatePolicyAsync_DenyRule_WithMixedCaseTenant_IsEnforced()
    {
        // Arrange (C-3): Deny rule configured with uppercase tenant, evaluation request with lowercase
        var userSid = new Sid("S-1-5-21-user-c3");
        var targetTable = new TableIdentifier("sales", "dbo", "confidential");

        var csv = $"""
            p, {userSid.Value}, *, {targetTable}, read, true, allow
            p, {userSid.Value}, TENANT-C3, {targetTable}, read, true, deny
            """;
        _service.LoadPolicyFromText(csv);

        var context = new SecurityEvaluationContext(
            userSid,
            [],
            new TenantId("tenant-c3"),
            targetTable,
            [],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "TEST");

        // Act
        var decision = await _service.EvaluatePolicyAsync(context);

        // Assert: Deny must match regardless of casing
        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluatePolicyAsync_UnknownTenant_DoesNotMutateInternalEnforcers()
    {
        // Arrange (C-5): Service has no policies for unknown tenant
        var unknownTenant = new TenantId("unknown-tenant-x");
        var context = new SecurityEvaluationContext(
            new Sid("S-1-5-21-nobody"),
            [],
            unknownTenant,
            new TableIdentifier("sales", "dbo", "orders"),
            [],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "TEST");

        _service.HasPolicies(unknownTenant).ShouldBeFalse();

        // Act
        var decision = await _service.EvaluatePolicyAsync(context);
        decision.IsAllowed.ShouldBeFalse();

        // Assert: Querying unknown tenant should not register policies or enforcer
        _service.HasPolicies(unknownTenant).ShouldBeFalse();
    }

    [Fact]
    public async Task ConcurrentEvaluations_DuringReload_NeverObserveEmptyOrPartialState()
    {
        // Arrange (C-4): Multiple reader threads evaluate policy while writer threads continuously reload policies
        var tenant = new TenantId("tenant-concurrent");
        var userSid = new Sid("S-1-5-21-concurrent-user");
        var targetTable = new TableIdentifier("sales", "dbo", "orders");

        var policy1 = $"""
            p, {userSid.Value}, {tenant.Value}, {targetTable}, read, true, allow
            """;
        var policy2 = $"""
            p, {userSid.Value}, {tenant.Value}, {targetTable}, read, true, allow
            p, {userSid.Value}, *, {targetTable}, read, true, allow
            """;

        _service.LoadPolicyFromText(policy1);

        var context = new SecurityEvaluationContext(
            userSid,
            [],
            tenant,
            targetTable,
            [],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "TEST");

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Act: 10 parallel reader tasks
        var readers = Task.Run(() =>
        {
            return Parallel.ForAsync(0, 1000, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = cts.Token }, async (i, token) =>
            {
                try
                {
                    var decision = await _service.EvaluatePolicyAsync(context, token);
                    if (!decision.IsAllowed)
                    {
                        exceptions.Add(new InvalidOperationException($"Decision was unexpectedly denied at iteration {i}"));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        });

        // Concurrently reload policies in writer loop
        var writer = Task.Run(async () =>
        {
            int counter = 0;
            while (!cts.IsCancellationRequested && counter < 50)
            {
                var text = counter % 2 == 0 ? policy2 : policy1;
                _service.LoadPolicyFromText(text);
                counter++;
                await Task.Delay(10);
            }
        });

        await Task.WhenAll(readers, writer);

        // Assert: No reader ever observed a denied decision or intermediate corrupted state
        exceptions.ShouldBeEmpty();
    }
}


