using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Interceptors;
using Autheris.Infrastructure.Lineage;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Autheris.Benchmarks;

public static class SlaValidationBenchmark
{
    public static async Task RunAllSlaChecksAsync()
    {
        Console.WriteLine();
        Console.WriteLine("================================================================================");
        Console.WriteLine(" SLA Validation Suite: Casbin (<=0.5ms), Lineage (<=15ms), Complexity (<=2ms)");
        Console.WriteLine("================================================================================");

        // 1. SLA-06: Complexity calculation (AST Traversierung <= 2ms)
        RunComplexitySlaCheck();

        // 2. SLA-04: Lineage 10,000 nodes traversal (p99 <= 15ms)
        await RunLineageSlaCheckAsync();

        // 3. SLA-01 & SLA-02: Casbin ABAC evaluation with 50,000 rules (p50 <= 0.1ms, p99 <= 0.5ms)
        await RunCasbinSlaCheckAsync();
    }

    public static void RunComplexitySlaCheck()
    {
        Console.WriteLine("--- [SLA Check 1/3] QueryCostAnalyzerRule (AST Traversierung, Target: <= 2ms) ---");
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 10000, defaultListMultiplier: 10, maxResponseRows: 1000);

        var queryText = @"
            query DeepComplexQuery {
                invoices(first: 100) {
                    id
                    amount
                    email
                    customer {
                        id
                        name
                        email
                        iban
                        orders(first: 50) {
                            id
                            total
                            salary
                            items(first: 20) {
                                id
                                price
                                sensitive_note
                                product {
                                    id
                                    title
                                    sku
                                }
                            }
                        }
                    }
                }
                users(first: 200) {
                    id
                    name
                    email
                    ssn
                    creditcard
                }
            }";

        var document = Utf8GraphQLParser.Parse(queryText);
        var schema = SchemaBuilder.New()
            .AddQueryType<DummyQuery>()
            .Create();
        var mockContext = new DocumentValidatorContext();
        mockContext.Initialize(schema, default, document, 10, 10, 10, null!);

        // Warmup
        for (int i = 0; i < 1000; i++)
        {
            mockContext.Initialize(schema, default, document, 10, 10, 10, null!);
            rule.Validate(mockContext, document);
        }

        const int iterations = 10_000;
        var latencies = new double[iterations];
        var sw = new Stopwatch();

        for (int i = 0; i < iterations; i++)
        {
            mockContext.Initialize(schema, default, document, 10, 10, 10, null!);
            sw.Restart();
            rule.Validate(mockContext, document);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMicroseconds;
        }

        Array.Sort(latencies);
        double p50Ms = latencies[(int)(iterations * 0.50)] / 1000.0;
        double p99Ms = latencies[(int)(iterations * 0.99)] / 1000.0;
        double maxMs = latencies[iterations - 1] / 1000.0;

        Console.WriteLine($" Iterations : {iterations:N0}");
        Console.WriteLine($" P50 Latency: {p50Ms:F4} ms ({latencies[(int)(iterations * 0.50)]:F2} µs)");
        Console.WriteLine($" P99 Latency: {p99Ms:F4} ms ({latencies[(int)(iterations * 0.99)]:F2} µs)");
        Console.WriteLine($" Max Latency: {maxMs:F4} ms");
        Console.WriteLine($" SLA-06 Gate: P99 <= 2.0 ms -> {(p99Ms <= 2.0 ? "PASS [COMPLIANT]" : "FAIL")}");
        Console.WriteLine();
    }

    public static async Task RunLineageSlaCheckAsync()
    {
        Console.WriteLine("--- [SLA Check 2/3] Lineage Impact Analyzer (10,000 Nodes, Target: p99 <= 15ms) ---");
        var graphStore = new LineageGraphStore();

        var rootId = "perf.dbo.root";
        var nodes = new List<LineageNode>(10_000);
        var rootDownstream = new List<string>(10);
        for (int i = 1; i <= 10; i++) rootDownstream.Add($"perf.node.{i}");
        nodes.Add(new LineageNode(rootId, "root", LineageNodeType.Table, rootDownstream, "Perf Team", "perf@corp.local"));

        for (int i = 1; i < 10_000; i++)
        {
            var downstream = new List<string>(2);
            int child1 = (i * 2) + 1;
            int child2 = (i * 2) + 2;
            if (child1 < 10_000) downstream.Add($"perf.node.{child1}");
            if (child2 < 10_000) downstream.Add($"perf.node.{child2}");

            var type = (i % 50 == 0) ? LineageNodeType.Dashboard : LineageNodeType.Table;
            nodes.Add(new LineageNode($"perf.node.{i}", $"node_{i}", type, downstream, $"Team {i % 10}", $"team{i % 10}@corp.local"));
        }

        graphStore.UpdateGraph(nodes);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("perf", "dbo", "root"),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-PERF-USER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);

        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();
        ownershipRepo.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(true);

        var service = new LineageImpactAnalyzerService(consentRepo, ownershipRepo, graphStore, NullLogger<LineageImpactAnalyzerService>.Instance);

        var callerContext = new CallerSecurityContext(
            new Sid("S-1-5-21-PERF-USER"),
            [],
            ["Analyst"],
            new TenantId("perf-tenant"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        // Warmup
        for (int i = 0; i < 5; i++)
        {
            await service.CalculateConsentRevocationImpactAsync(new TenantId("perf-tenant"), consentId, callerContext);
        }

        const int iterations = 100;
        var latencies = new double[iterations];
        var sw = new Stopwatch();

        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            var report = await service.CalculateConsentRevocationImpactAsync(new TenantId("perf-tenant"), consentId, callerContext);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMicroseconds;
        }

        Array.Sort(latencies);
        double p50Ms = latencies[(int)(iterations * 0.50)] / 1000.0;
        double p99Ms = latencies[(int)(iterations * 0.99)] / 1000.0;
        double maxMs = latencies[iterations - 1] / 1000.0;

        Console.WriteLine($" Iterations : {iterations:N0} (traversing ~10,000 nodes each)");
        Console.WriteLine($" P50 Latency: {p50Ms:F2} ms");
        Console.WriteLine($" P99 Latency: {p99Ms:F2} ms");
        Console.WriteLine($" Max Latency: {maxMs:F2} ms");
        Console.WriteLine($" SLA-04 Gate: P99 <= 15.0 ms -> {(p99Ms <= 15.0 ? "PASS [COMPLIANT]" : "FAIL")}");
        Console.WriteLine();
    }

    public static async Task RunCasbinSlaCheckAsync()
    {
        Console.WriteLine("--- [SLA Check 3/3] Casbin ABAC Evaluation (50,000 Rules, Target: p50 <= 0.1ms, p99 <= 0.5ms) ---");
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-perf");

        Console.WriteLine(" Populating Casbin with 50,000 rules...");
        var swPop = Stopwatch.StartNew();

        // 50,000 rules
        for (int i = 1; i <= 50_000; i++)
        {
            var userSid = $"S-1-5-21-USER-{i:D5}";
            var table = $"domain_{i % 10}.dbo.table_{i % 500}";
            service.AddPolicy(tenant, userSid, table, "read", "true", "allow");
        }
        swPop.Stop();
        Console.WriteLine($" Populated 50,000 rules in {swPop.Elapsed.TotalSeconds:F2} s.");

        var testUser = new Sid("S-1-5-21-USER-25000"); // middle of the pack
        var testTable = new TableIdentifier("domain_0", "dbo", "table_0");

        var context = new SecurityEvaluationContext(
            testUser,
            [],
            tenant,
            testTable,
            ["col1"],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "read");

        // Warmup
        for (int i = 0; i < 2; i++)
        {
            await service.EvaluatePolicyAsync(context);
        }

        const int iterations = 20;
        var latencies = new double[iterations];
        var sw = new Stopwatch();

        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            var dec = await service.EvaluatePolicyAsync(context);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMicroseconds;
        }

        Array.Sort(latencies);
        double p50Ms = latencies[(int)(iterations * 0.50)] / 1000.0;
        double p99Ms = latencies[(int)(iterations * 0.99)] / 1000.0;
        double maxMs = latencies[iterations - 1] / 1000.0;

        Console.WriteLine($" Iterations : {iterations:N0}");
        Console.WriteLine($" P50 Latency: {p50Ms:F4} ms ({latencies[(int)(iterations * 0.50)]:F2} µs)");
        Console.WriteLine($" P99 Latency: {p99Ms:F4} ms ({latencies[(int)(iterations * 0.99)]:F2} µs)");
        Console.WriteLine($" Max Latency: {maxMs:F4} ms");
        Console.WriteLine($" SLA-02 Gate: P50 <= 0.1 ms -> {(p50Ms <= 0.1 ? "PASS [COMPLIANT]" : "FAIL")}");
        Console.WriteLine($" SLA-01 Gate: P99 <= 0.5 ms -> {(p99Ms <= 0.5 ? "PASS [COMPLIANT]" : "FAIL")}");
        Console.WriteLine();
    }

    public class DummyQuery
    {
        public string Test => "ok";
    }
}
