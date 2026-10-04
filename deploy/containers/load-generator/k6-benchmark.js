import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Counter, Rate } from 'k6/metrics';

// --- Custom Metrics for Benchmark Evaluation ---
const latencySimple = new Trend('latency_simple_queries', true);
const latencyCrossDb = new Trend('latency_cross_database_queries', true);
const latencyComplex = new Trend('latency_complex_queries', true);
const latencyMutations = new Trend('latency_mutations', true);
const latencyInvalid = new Trend('latency_invalid_requests', true);
const latencyLakehouse = new Trend('latency_lakehouse_iceberg_queries', true);

const rateLimitExceededErrors = new Counter('errors_rate_limit_exceeded');
const forbiddenErrors = new Counter('errors_forbidden');
const queryTooComplexErrors = new Counter('errors_query_too_complex');
const responseTooLargeErrors = new Counter('errors_response_too_large');
const internalServerErrors = new Counter('errors_internal_server_error');
const successRate = new Rate('rate_successful_requests');
const idempotentHits = new Counter('mutations_idempotent_hits');

// --- Configuration from Environment Variables ---
const TARGET_URL = __ENV.TARGET_URL || 'http://reverse-proxy:8080/graphql';
const VUS = parseInt(__ENV.VUS || '50', 10);
const DURATION_STEADY = __ENV.DURATION_STEADY || '3m';
const DURATION_RAMP = __ENV.DURATION_RAMP || '20s';
const PACING_SLEEP = __ENV.PACING_SLEEP === 'true';
const LAKEHOUSE_ENABLED = __ENV.ENABLE_LAKEHOUSE === 'true';
const SQLSERVER_ENABLED = __ENV.ENABLE_SQLSERVER !== 'false';
const ENTERPRISE_ENABLED = __ENV.ENABLE_ENTERPRISE === 'true';

export const options = {
  scenarios: {
    governance_benchmark: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: DURATION_RAMP, target: VUS },         // Ramp-up
        { duration: DURATION_STEADY, target: VUS },       // Steady State
        { duration: DURATION_RAMP, target: 0 },           // Ramp-down
      ],
      gracefulRampDown: '10s',
    },
  },
  thresholds: {
    // 95% of valid simple queries under 150ms
    'latency_simple_queries': ['p(95)<150'],
    // 95% of cross-database queries under 250ms
    'latency_cross_database_queries': ['p(95)<250'],
    // 95% of complex nested queries under 450ms
    'latency_complex_queries': ['p(95)<450'],
    // 95% of Lakehouse Iceberg S3 queries under 350ms
    'latency_lakehouse_iceberg_queries': ['p(95)<350'],
    // System should maintain high availability
    'errors_internal_server_error': ['count<5'],
  },
  summaryTrendStats: ['min', 'med', 'avg', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],
};

const ROLES = ['Finance', 'Auditor', 'Manager', 'Admin'];

// Generate deterministic random integer in range [min, max]
function randInt(min, max) {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

// Generate unique UUID v4 string
function generateUuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
    const r = Math.random() * 16 | 0;
    const v = c === 'x' ? r : (r & 0x3 | 0x8);
    return v.toString(16);
  });
}

function parseGraphQLResponse(res, customTrend) {
  customTrend.add(res.timings.duration);

  let isSuccess = false;
  if (res.status === 200) {
    try {
      const body = JSON.parse(res.body);
      if (body.errors && body.errors.length > 0) {
        for (const err of body.errors) {
          const code = (err.extensions && err.extensions.code) || '';
          if (code === 'RATE_LIMIT_EXCEEDED') rateLimitExceededErrors.add(1);
          else if (code === 'FORBIDDEN') forbiddenErrors.add(1);
          else if (code === 'QUERY_TOO_COMPLEX') queryTooComplexErrors.add(1);
          else if (code === 'RESPONSE_TOO_LARGE') responseTooLargeErrors.add(1);
          else if (code === 'INTERNAL_SERVER_ERROR') internalServerErrors.add(1);
        }
      } else {
        isSuccess = true;
      }
    } catch (e) {
      internalServerErrors.add(1);
    }
  } else if (res.status === 429) {
    rateLimitExceededErrors.add(1);
  } else if (res.status === 403) {
    forbiddenErrors.add(1);
  } else if (res.status >= 500) {
    internalServerErrors.add(1);
  }

  successRate.add(isSuccess);
  return isSuccess;
}

export default function () {
  const vuId = __VU;
  const role = ROLES[vuId % ROLES.length];
  const randSelector = Math.random() * 100;

  const baseHeaders = {
    'Content-Type': 'application/json',
    'GraphQL-Preflight': '1',
    'X-Benchmark-Role': role,
  };

  // --------------------------------------------------------------------------
  // 1a. 40% Simple Paginierter Query (PostgreSQL Finance: RLS & Masking)
  // --------------------------------------------------------------------------
  // 1a. Simple Paged Query (PostgreSQL Finance: RLS & Masking) / Lakehouse Iceberg S3
  // --------------------------------------------------------------------------
  if (randSelector < 40) {
    if (LAKEHOUSE_ENABLED && Math.random() < 0.3) {
      const first = randInt(5, 20);
      const payload = JSON.stringify({
        query: `
          query LakehouseOrders($first: Int) {
            table(domain: "lakehouse", name: "orders", schema: "dbo", first: $first) {
              tableName
              totalCount
              jsonRows
            }
          }
        `,
        variables: { first },
      });

      const res = http.post(TARGET_URL, payload, { headers: baseHeaders });
      check(res, {
        'lakehouse query status is 200': (r) => r.status === 200,
        'lakehouse query returns jsonRows': (r) => r.body.includes('jsonRows'),
      });
      parseGraphQLResponse(res, latencyLakehouse);
    } else {
      const first = randInt(10, 50);
      const after = randInt(0, 500);

      const payload = JSON.stringify({
        query: `
          query SimpleInvoices($first: Int, $after: Int) {
            table(domain: "finance", name: "invoices", schema: "public", first: $first, after: $after) {
              tableName
              totalCount
              jsonRows
            }
          }
        `,
        variables: { first, after },
      });

      const res = http.post(TARGET_URL, payload, { headers: baseHeaders });
      check(res, {
        'simple query status is 200': (r) => r.status === 200,
        'simple query returns jsonRows': (r) => r.body.includes('jsonRows'),
      });
      parseGraphQLResponse(res, latencySimple);
    }
  }

  // --------------------------------------------------------------------------
  // 1b. 25% Cross-Database Query (PostgreSQL Finance + SQLite HR [+ SQL Server CRM])
  // --------------------------------------------------------------------------
  else if (randSelector < 65) {
    const first = randInt(10, 30);
    const after = randInt(0, 100);

    const query = SQLSERVER_ENABLED
      ? `
        query CrossDatabaseTriDb($first: Int, $after: Int) {
          postgresInvoices: table(domain: "finance", name: "invoices", schema: "public", first: $first, after: $after) {
            tableName
            totalCount
            jsonRows
          }
          sqliteEmployees: table(domain: "hr", name: "hr_table_1", schema: "dbo", first: $first, after: $after) {
            tableName
            totalCount
            jsonRows
          }
          sqlServerOrders: table(domain: "crm", name: "orders", schema: "dbo", first: $first, after: $after) {
            tableName
            totalCount
            jsonRows
          }
        }
      `
      : `
        query CrossDatabaseBiDb($first: Int, $after: Int) {
          postgresInvoices: table(domain: "finance", name: "invoices", schema: "public", first: $first, after: $after) {
            tableName
            totalCount
            jsonRows
          }
          sqliteEmployees: table(domain: "hr", name: "hr_table_1", schema: "dbo", first: $first, after: $after) {
            tableName
            totalCount
            jsonRows
          }
        }
      `;

    const payload = JSON.stringify({ query, variables: { first, after } });
    const res = http.post(TARGET_URL, payload, { headers: baseHeaders });
    const checks = {
      'cross-db query status is 200': (r) => r.status === 200,
      'cross-db postgres data present': (r) => r.body.includes('postgresInvoices'),
      'cross-db sqlite data present': (r) => r.body.includes('sqliteEmployees'),
    };
    if (SQLSERVER_ENABLED) {
      checks['cross-db sqlserver data present'] = (r) => r.body.includes('sqlServerOrders');
    }
    check(res, checks);
    parseGraphQLResponse(res, latencyCrossDb);
  }

  // --------------------------------------------------------------------------
  // 2. 15% Komplexer verschachtelter Query (DataLoader, Relationen & Masking)
  // --------------------------------------------------------------------------
  else if (randSelector < 80) {
    const queryChoice = Math.random();

    if (queryChoice < 0.5) {
      // Nested Items DataLoader Query
      const first = randInt(5, 15);
      const payload = JSON.stringify({
        query: `
          query InvoicesWithItems($first: Int) {
            finance {
              invoicesWithItems(first: $first) {
                id
                amount
                vendor
                email
                items {
                  id
                  productName
                  price
                  sensitiveNote
                }
              }
            }
          }
        `,
        variables: { first },
      });

      const res = http.post(TARGET_URL, payload, { headers: baseHeaders });
      check(res, {
        'complex query status is 200': (r) => r.status === 200,
        'complex items present': (r) => r.body.includes('invoicesWithItems'),
      });
      parseGraphQLResponse(res, latencyComplex);
    } else {
      // Dynamic Catalog Query with Active Consents
      const payload = JSON.stringify({
        query: `
          query GetCatalog {
            catalog {
              domain
              schema
              tableName
              displayName
              sensitivity
              columns
            }
          }
        `,
      });

      const res = http.post(TARGET_URL, payload, { headers: baseHeaders });
      check(res, {
        'catalog query status is 200': (r) => r.status === 200,
        'catalog lists tables': (r) => r.body.includes('catalog'),
      });
      parseGraphQLResponse(res, latencyComplex);
    }
  }

  // --------------------------------------------------------------------------
  // 3. 10% Mutationen (Consent Request & Idempotency Key Replay)
  // --------------------------------------------------------------------------
  else if (randSelector < 90) {
    const idempotencyKey = `idem-${vuId}-${randInt(1, 1000)}`;

    const reqPayload = JSON.stringify({
      query: `
        mutation RequestAccess($domain: String!, $schema: String!, $tableName: String!, $justification: String!, $durationDays: Int!, $idempotencyKey: String) {
          requestTableAccess(
            domain: $domain
            schema: $schema
            tableName: $tableName
            justification: $justification
            durationDays: $durationDays
            idempotencyKey: $idempotencyKey
          ) {
            requestId
            status
            message
          }
        }
      `,
      variables: {
        domain: 'finance',
        schema: 'public',
        tableName: 'invoices',
        justification: `Audit and reconciliation batch ${randInt(100, 999)}`,
        durationDays: 30,
        idempotencyKey: idempotencyKey,
      },
    });

    // 1st Attempt: Original request
    const res1 = http.post(TARGET_URL, reqPayload, { headers: baseHeaders });
    check(res1, {
      'mutation 1st attempt 200': (r) => r.status === 200,
      'mutation request created': (r) => r.body.includes('requestTableAccess'),
    });
    parseGraphQLResponse(res1, latencyMutations);

    // 2nd Attempt: Instant re-submission with identical Idempotency-Key
    // Must return cached payload instantly without re-processing!
    const res2 = http.post(TARGET_URL, reqPayload, { headers: baseHeaders });
    if (res2.status === 200 && res2.body.includes('requestTableAccess')) {
      idempotentHits.add(1);
    }
    parseGraphQLResponse(res2, latencyMutations);
  }

  // --------------------------------------------------------------------------
  // 4. 10% Ungültige / Übergroße Requests (Rate Limit, Complexity, Security)
  // --------------------------------------------------------------------------
  else {
    const testCase = Math.random();

    if (testCase < 0.33) {
      // 4a. Response Size Cap Exceeded (MaxResponseRows)
      const payload = JSON.stringify({
        query: `
          query HugeResponse {
            table(domain: "finance", name: "invoices", schema: "public", first: 99999) {
              tableName
              totalCount
              jsonRows
            }
          }
        `,
      });
      const res = http.post(TARGET_URL, payload, { headers: baseHeaders });
      parseGraphQLResponse(res, latencyInvalid);
    } else if (testCase < 0.66) {
      // 4b. Zero-Trust Blocked Subject (Hard Deny)
      const blockedHeaders = Object.assign({}, baseHeaders, {
        'X-Benchmark-Role': 'Blocked',
      });
      const payload = JSON.stringify({
        query: `
          query BlockedAccess {
            table(domain: "finance", name: "invoices", schema: "public", first: 10) {
              tableName
              jsonRows
            }
          }
        `,
      });
      const res = http.post(TARGET_URL, payload, { headers: blockedHeaders });
      check(res, {
        'blocked user forbidden': (r) => r.status === 200 && r.body.includes('FORBIDDEN'),
      });
      parseGraphQLResponse(res, latencyInvalid);
    } else {
      // 4c. Non-existent table enumeration probe
      const payload = JSON.stringify({
        query: `
          query ProbingSecretTable {
            table(domain: "shadow", name: "secret_vault", schema: "dbo", first: 10) {
              tableName
              jsonRows
            }
          }
        `,
      });
      const res = http.post(TARGET_URL, payload, { headers: baseHeaders });
      parseGraphQLResponse(res, latencyInvalid);
    }
  }

  // Realistic human pacing / think time between queries (skipped during high-throughput stress testing)
  if (PACING_SLEEP) {
    sleep(0.05 + Math.random() * 0.15);
  }
}

// Generate structured summary reports (JSON & CSV)
function getPercentiles(trend) {
  if (!trend || !trend.values) return { p50: 0, p90: 0, p95: 0, p99: 0 };
  const v = trend.values;
  const p50 = v['med'] !== undefined ? v['med'] : (v['p(50)'] !== undefined ? v['p(50)'] : (v['avg'] || 0));
  const p90 = v['p(90)'] !== undefined ? v['p(90)'] : p50;
  const p95 = v['p(95)'] !== undefined ? v['p(95)'] : p90;
  const p99 = v['p(99)'] !== undefined ? v['p(99)'] : p95;
  return {
    p50: Math.round(p50 * 100) / 100,
    p90: Math.round(p90 * 100) / 100,
    p95: Math.round(p95 * 100) / 100,
    p99: Math.round(p99 * 100) / 100,
  };
}

// Generate structured summary reports (JSON & CSV)
export function handleSummary(data) {
  const metrics = data.metrics;

  const summaryObj = {
    timestamp: new Date().toISOString(),
    vus_max: VUS,
    duration_steady: DURATION_STEADY,
    total_requests: metrics.http_reqs ? metrics.http_reqs.values.count : 0,
    rps: metrics.http_reqs ? Math.round(metrics.http_reqs.values.rate * 100) / 100 : 0,
    success_rate_percent: metrics.rate_successful_requests ? Math.round(metrics.rate_successful_requests.values.rate * 10000) / 100 : 0,
    latencies_ms: {
      simple_queries: getPercentiles(metrics.latency_simple_queries),
      cross_database_queries: getPercentiles(metrics.latency_cross_database_queries),
      complex_queries: getPercentiles(metrics.latency_complex_queries),
      mutations: getPercentiles(metrics.latency_mutations),
      invalid_requests: getPercentiles(metrics.latency_invalid_requests),
    },
    error_counts: {
      rate_limit_exceeded: metrics.errors_rate_limit_exceeded ? metrics.errors_rate_limit_exceeded.values.count : 0,
      forbidden: metrics.errors_forbidden ? metrics.errors_forbidden.values.count : 0,
      query_too_complex: metrics.errors_query_too_complex ? metrics.errors_query_too_complex.values.count : 0,
      response_too_large: metrics.errors_response_too_large ? metrics.errors_response_too_large.values.count : 0,
      internal_server_error: metrics.errors_internal_server_error ? metrics.errors_internal_server_error.values.count : 0,
    },
    idempotent_replays: metrics.mutations_idempotent_hits ? metrics.mutations_idempotent_hits.values.count : 0,
  };

  const csvRows = [
    'MetricCategory,MetricName,Value,Unit',
    `Throughput,RPS,${summaryObj.rps},req/s`,
    `Throughput,TotalRequests,${summaryObj.total_requests},requests`,
    `Throughput,SuccessRate,${summaryObj.success_rate_percent},%`,
    `LatencySimple,p50,${summaryObj.latencies_ms.simple_queries.p50},ms`,
    `LatencySimple,p90,${summaryObj.latencies_ms.simple_queries.p90},ms`,
    `LatencySimple,p95,${summaryObj.latencies_ms.simple_queries.p95},ms`,
    `LatencySimple,p99,${summaryObj.latencies_ms.simple_queries.p99},ms`,
    `LatencyCrossDb,p50,${summaryObj.latencies_ms.cross_database_queries.p50},ms`,
    `LatencyCrossDb,p90,${summaryObj.latencies_ms.cross_database_queries.p90},ms`,
    `LatencyCrossDb,p95,${summaryObj.latencies_ms.cross_database_queries.p95},ms`,
    `LatencyCrossDb,p99,${summaryObj.latencies_ms.cross_database_queries.p99},ms`,
    `LatencyComplex,p50,${summaryObj.latencies_ms.complex_queries.p50},ms`,
    `LatencyComplex,p90,${summaryObj.latencies_ms.complex_queries.p90},ms`,
    `LatencyComplex,p95,${summaryObj.latencies_ms.complex_queries.p95},ms`,
    `LatencyComplex,p99,${summaryObj.latencies_ms.complex_queries.p99},ms`,
    `LatencyMutations,p50,${summaryObj.latencies_ms.mutations.p50},ms`,
    `LatencyMutations,p90,${summaryObj.latencies_ms.mutations.p90},ms`,
    `LatencyMutations,p95,${summaryObj.latencies_ms.mutations.p95},ms`,
    `LatencyMutations,p99,${summaryObj.latencies_ms.mutations.p99},ms`,
    `Errors,RATE_LIMIT_EXCEEDED,${summaryObj.error_counts.rate_limit_exceeded},count`,
    `Errors,FORBIDDEN,${summaryObj.error_counts.forbidden},count`,
    `Errors,QUERY_TOO_COMPLEX,${summaryObj.error_counts.query_too_complex},count`,
    `Errors,RESPONSE_TOO_LARGE,${summaryObj.error_counts.response_too_large},count`,
    `Errors,INTERNAL_SERVER_ERROR,${summaryObj.error_counts.internal_server_error},count`,
    `Idempotency,IdempotentHits,${summaryObj.idempotent_replays},count`,
  ];

  return {
    '/results/benchmark-summary.json': JSON.stringify(summaryObj, null, 2),
    '/results/benchmark-summary.csv': csvRows.join('\n'),
    stdout: `\n=== GqlGateway Benchmark Completed ===\nRPS: ${summaryObj.rps} | Total Requests: ${summaryObj.total_requests} | Success Rate: ${summaryObj.success_rate_percent}%\nSimple Queries Latency: p50=${summaryObj.latencies_ms.simple_queries.p50}ms, p95=${summaryObj.latencies_ms.simple_queries.p95}ms, p99=${summaryObj.latencies_ms.simple_queries.p99}ms\nCross-DB Queries Latency: p50=${summaryObj.latencies_ms.cross_database_queries.p50}ms, p95=${summaryObj.latencies_ms.cross_database_queries.p95}ms, p99=${summaryObj.latencies_ms.cross_database_queries.p99}ms\nComplex Queries Latency: p50=${summaryObj.latencies_ms.complex_queries.p50}ms, p95=${summaryObj.latencies_ms.complex_queries.p95}ms, p99=${summaryObj.latencies_ms.complex_queries.p99}ms\nErrors: RATE_LIMIT=${summaryObj.error_counts.rate_limit_exceeded}, FORBIDDEN=${summaryObj.error_counts.forbidden}, TOO_COMPLEX=${summaryObj.error_counts.query_too_complex}, TOO_LARGE=${summaryObj.error_counts.response_too_large}, 500_ERR=${summaryObj.error_counts.internal_server_error}\n=======================================\n`,
  };
}
