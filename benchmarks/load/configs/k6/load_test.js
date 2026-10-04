import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

// Custom Metrics
const graphqlErrors = new Counter('graphql_errors');
const successfulRequests = new Rate('successful_requests');
const requestDurationTrend = new Trend('graphql_duration', true);

// Read configuration from environment
const TARGET_URL = __ENV.TARGET_URL || 'http://localhost:8085/v1/graphql';
const SCENARIO_TYPE = __ENV.SCENARIO_TYPE || 'rps'; // 'rps', 'vus', 'ramp'
const TEST_DURATION = __ENV.TEST_DURATION || '30s';
const TARGET_RPS = parseInt(__ENV.TARGET_RPS || '1000', 10);
const TARGET_VUS = parseInt(__ENV.TARGET_VUS || '50', 10);
const QUERY_TYPE = __ENV.QUERY_TYPE || 'pk'; // 'pk', 'filter', 'join', 'deep', 'gql_table'

// Queries
const QUERIES = {
  pk: {
    name: 'AlbumByPK',
    body: JSON.stringify({
      query: 'query AlbumByPK { albums_by_pk(id: 1) { id title } }'
    })
  },
  filter: {
    name: 'SearchAlbums',
    body: JSON.stringify({
      query: 'query SearchAlbums { albums(where: {title: {_like: "%Rock%"}}) { id title } }'
    })
  },
  join: {
    name: 'SearchAlbumsWithArtist',
    body: JSON.stringify({
      query: 'query SearchAlbumsWithArtist { albums(where: {title: {_like: "%Rock%"}}) { id title artist { id name } } }'
    })
  },
  deep: {
    name: 'AlbumWithTracksAndGenre',
    body: JSON.stringify({
      query: 'query AlbumWithTracksAndGenre { albums_by_pk(id: 1) { id title tracks { id name genre { name } } } }'
    })
  },
  gql_table: {
    name: 'GqlGatewayTable',
    body: JSON.stringify({
      query: 'query { table(domain: "finance", name: "finance_table_1", first: 5) { tableName totalCount jsonRows } }'
    })
  }
};

const selectedQuery = QUERIES[QUERY_TYPE] || QUERIES.pk;

// Headers
const baseHeaders = {
  'Content-Type': 'application/json',
  'Accept': 'application/json',
  'GraphQL-Preflight': '1',
  'X-Hasura-Admin-Secret': 'my-secret',
  'X-Test-User-Sid': 'S-1-5-21-9999',
};

if (__ENV.CUSTOM_HEADERS_JSON) {
  try {
    const extra = JSON.parse(__ENV.CUSTOM_HEADERS_JSON);
    Object.assign(baseHeaders, extra);
  } catch (e) {
    // Ignore invalid JSON in headers
  }
}

// Scenarios definition
export const options = {
  discardResponseBodies: false,
  thresholds: {
    http_req_failed: ['rate<0.01'], // <1% network errors
    successful_requests: ['rate>0.99'], // >99% 200 OK without GraphQL errors
  },
  scenarios: {}
};

if (SCENARIO_TYPE === 'rps') {
  options.scenarios.constant_rps = {
    executor: 'constant-arrival-rate',
    rate: TARGET_RPS,
    timeUnit: '1s',
    duration: TEST_DURATION,
    preAllocatedVUs: Math.min(TARGET_RPS, 200),
    maxVUs: Math.max(TARGET_RPS * 2, 500),
  };
} else if (SCENARIO_TYPE === 'ramp') {
  options.scenarios.ramp = {
    executor: 'ramping-arrival-rate',
    startRate: 100,
    timeUnit: '1s',
    preAllocatedVUs: 100,
    maxVUs: 2000,
    stages: [
      { target: 500, duration: '10s' },
      { target: 1500, duration: '20s' },
      { target: 3000, duration: '20s' },
      { target: 5000, duration: '20s' },
      { target: 500, duration: '10s' },
    ],
  };
} else {
  options.scenarios.constant_vus = {
    executor: 'constant-vus',
    vus: TARGET_VUS,
    duration: TEST_DURATION,
  };
}

export default function () {
  const res = http.post(TARGET_URL, selectedQuery.body, { headers: baseHeaders });

  const isHttpOk = res.status === 200;
  let hasGqlErrors = false;

  if (isHttpOk) {
    try {
      const json = res.json();
      if (json && json.errors && json.errors.length > 0) {
        hasGqlErrors = true;
        graphqlErrors.add(1);
      }
    } catch (_) {
      // response might not be JSON
    }
  }

  const success = isHttpOk && !hasGqlErrors;
  successfulRequests.add(success ? 1 : 0);
  requestDurationTrend.add(res.timings.duration);

  check(res, {
    'status is 200': (r) => r.status === 200,
    'no graphql errors': () => !hasGqlErrors,
  });
}

export function handleSummary(data) {
  const summaryOut = __ENV.SUMMARY_FILE || 'summary.json';
  const out = {};
  out[summaryOut] = JSON.stringify(data, null, 2);
  return out;
}
