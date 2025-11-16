/**
 * ============================================================================
 * k6 Load Test - Progressive Ramp to 1M Users
 * ============================================================================
 *
 * Simulates progressive ramp-up to 1 million concurrent users
 * Validates tickets via gRPC API
 *
 * Usage:
 *   k6 run --out json=results.json load-test-progressive.js
 *   k6 run --out influxdb=http://localhost:8086/k6 load-test-progressive.js
 *
 * Phases:
 *   1. Warmup: 1k users, 2 min
 *   2. Ramp 1: 1k → 10k users, 5 min
 *   3. Plateau 1: 10k users, 10 min
 *   4. Ramp 2: 10k → 100k users, 10 min
 *   5. Plateau 2: 100k users, 20 min
 *   6. Ramp 3: 100k → 500k users, 20 min
 *   7. Plateau 3: 500k users, 30 min
 *   8. Ramp 4: 500k → 1M users, 30 min
 *   9. Plateau 4: 1M users, 60 min (soak test)
 *   10. Rampdown: 1M → 0, 10 min
 *
 * Total duration: ~3.5 hours
 */

import { check, sleep } from 'k6';
import { Counter, Trend, Rate } from 'k6/metrics';
import grpc from 'k6/net/grpc';
import { randomIntBetween, randomItem } from 'https://jslib.k6.io/k6-utils/1.2.0/index.js';

// ============================================================================
// Configuration
// ============================================================================

const GRPC_SERVER = __ENV.GRPC_SERVER || 'localhost:5000';
const TEST_MODE = __ENV.TEST_MODE || 'full';  // 'quick', 'medium', 'full'

// Test scenarios based on mode
const scenarios = {
  quick: {
    // Quick test for validation (10k users max, 10 min total)
    stages: [
      { duration: '1m', target: 1000 },
      { duration: '2m', target: 5000 },
      { duration: '3m', target: 10000 },
      { duration: '3m', target: 10000 },
      { duration: '1m', target: 0 },
    ],
  },
  medium: {
    // Medium test (100k users max, 1 hour total)
    stages: [
      { duration: '2m', target: 1000 },    // Warmup
      { duration: '5m', target: 10000 },   // Ramp 1
      { duration: '10m', target: 10000 },  // Plateau 1
      { duration: '10m', target: 100000 }, // Ramp 2
      { duration: '20m', target: 100000 }, // Plateau 2
      { duration: '10m', target: 0 },      // Rampdown
    ],
  },
  full: {
    // Full test (1M users, ~3.5 hours)
    stages: [
      { duration: '2m', target: 1000 },      // Warmup
      { duration: '5m', target: 10000 },     // Ramp 1
      { duration: '10m', target: 10000 },    // Plateau 1
      { duration: '10m', target: 100000 },   // Ramp 2
      { duration: '20m', target: 100000 },   // Plateau 2
      { duration: '20m', target: 500000 },   // Ramp 3
      { duration: '30m', target: 500000 },   // Plateau 3
      { duration: '30m', target: 1000000 },  // Ramp 4 (ULTIMATE!)
      { duration: '60m', target: 1000000 },  // Plateau 4 (Soak test)
      { duration: '10m', target: 0 },        // Rampdown
    ],
  },
};

export const options = {
  stages: scenarios[TEST_MODE].stages,
  thresholds: {
    // Success rate must be > 99%
    'checks': ['rate>0.99'],

    // P95 latency < 100ms (target < 50ms)
    'grpc_req_duration': ['p(95)<100', 'p(99)<200'],

    // Error rate < 1%
    'http_req_failed': ['rate<0.01'],
  },
  // gRPC specific options
  insecureSkipTLSVerify: true,
  // Connection reuse (important for gRPC)
  noConnectionReuse: false,
  userAgent: 'k6-ticketing-loadtest/1.0',
};

// ============================================================================
// Custom Metrics
// ============================================================================

const validationSuccess = new Counter('validation_success');
const validationFailure = new Counter('validation_failure');
const validationError = new Counter('validation_error');
const validationLatency = new Trend('validation_latency_ms');
const successRate = new Rate('success_rate');

// ============================================================================
// gRPC Client
// ============================================================================

const client = new grpc.Client();
client.load(['../../src/Ticketing.Shared/Protos'], 'ticketing.proto');

// ============================================================================
// Setup (per VU)
// ============================================================================

export function setup() {
  console.log(`Starting load test: mode=${TEST_MODE}, server=${GRPC_SERVER}`);

  // Test connection
  client.connect(GRPC_SERVER, { plaintext: true });

  const healthReq = {
    service_name: 'ticketing-api',
  };

  try {
    const response = client.invoke('ticketing.TicketingService/HealthCheck', healthReq);

    console.log(`Health check: status=${response.message.status}, version=${response.message.version}`);

    if (response.status !== grpc.StatusOK) {
      throw new Error(`Health check failed: ${response.status}`);
    }
  } catch (e) {
    console.error(`Failed to connect to gRPC server: ${e}`);
    throw e;
  } finally {
    client.close();
  }

  return {
    server: GRPC_SERVER,
    startTime: Date.now(),
  };
}

// ============================================================================
// Main Test Function (executed by each VU)
// ============================================================================

export default function (data) {
  // Connect to gRPC server (connection reused across iterations)
  if (!client.isConnected()) {
    client.connect(data.server, {
      plaintext: true,
      timeout: '30s',
      reflect: false,
    });
  }

  // Generate a unique user ID (simulate account-based ticketing)
  // Each VU simulates 1 user across all iterations
  const userId = `user-${__VU.toString().padStart(8, '0')}`;

  // Generate unique validation ID (idempotency key)
  const validationId = `${userId}-${__ITER}-${Date.now()}`;

  // Random validator group (zone)
  const validatorId = `zone-${randomIntBetween(1, 100)}`;

  // Random ticket type
  const ticketTypes = [
    'TICKET_TYPE_SINGLE_JOURNEY',
    'TICKET_TYPE_DAY_PASS',
    'TICKET_TYPE_WEEK_PASS',
    'TICKET_TYPE_MONTH_PASS',
  ];
  const ticketType = randomItem(ticketTypes);

  // Prepare gRPC request
  const request = {
    validation_id: validationId,
    user_id: userId,
    validator_id: validatorId,
    timestamp_ms: Date.now(),
    ticket_type: ticketType,
    metadata: {
      zone: 'test',
      iteration: __ITER.toString(),
    },
    correlation_id: `corr-${validationId}`,
  };

  // Invoke ValidateTicket gRPC call
  const startTime = Date.now();

  try {
    const response = client.invoke('ticketing.TicketingService/ValidateTicket', request, {
      timeout: '10s',
    });

    const latency = Date.now() - startTime;
    validationLatency.add(latency);

    // Check response
    const success = check(response, {
      'status is OK': (r) => r.status === grpc.StatusOK,
      'validation successful': (r) =>
        r.message &&
        (r.message.status === 'VALIDATION_STATUS_SUCCESS' ||
         r.message.status === 'VALIDATION_STATUS_DUPLICATE_VALIDATION'),
      'latency < 100ms': () => latency < 100,
    });

    if (success) {
      validationSuccess.add(1);
      successRate.add(1);
    } else {
      validationFailure.add(1);
      successRate.add(0);

      console.log(
        `Validation failed: user=${userId}, status=${response.message?.status}, error=${response.message?.error_message}`
      );
    }

    // Handle insufficient balance (expected after multiple validations)
    if (response.message?.status === 'VALIDATION_STATUS_INSUFFICIENT_BALANCE') {
      // Recharge account
      rechargeAccount(userId);
    }
  } catch (e) {
    validationError.add(1);
    successRate.add(0);

    console.error(`gRPC error: ${e}`);
  }

  // Think time (simulate realistic user behavior)
  // Users validate every 30-60 seconds on average
  sleep(randomIntBetween(30, 60));
}

// ============================================================================
// Helper: Recharge Account
// ============================================================================

function rechargeAccount(userId) {
  const rechargeReq = {
    user_id: userId,
    amount: 100.0, // Add 100€
    transaction_id: `recharge-${userId}-${Date.now()}`,
    correlation_id: `corr-recharge-${userId}`,
  };

  try {
    const response = client.invoke('ticketing.TicketingService/RechargeAccount', rechargeReq, {
      timeout: '10s',
    });

    if (response.status === grpc.StatusOK && response.message?.success) {
      console.log(`Recharged account: user=${userId}, new_balance=${response.message.new_balance}`);
    }
  } catch (e) {
    console.error(`Failed to recharge account: ${e}`);
  }
}

// ============================================================================
// Teardown (after test completion)
// ============================================================================

export function teardown(data) {
  client.close();

  const duration = (Date.now() - data.startTime) / 1000;
  console.log(`Load test completed in ${duration.toFixed(2)} seconds`);
}

// ============================================================================
// Custom Summary
// ============================================================================

export function handleSummary(data) {
  // Generate custom HTML report
  const htmlReport = `
<!DOCTYPE html>
<html>
<head>
  <title>k6 Load Test Report - Ticketing</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 20px; }
    h1 { color: #333; }
    table { border-collapse: collapse; width: 100%; margin-top: 20px; }
    th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }
    th { background-color: #4CAF50; color: white; }
    .metric { font-size: 1.2em; font-weight: bold; }
    .success { color: green; }
    .warning { color: orange; }
    .error { color: red; }
  </style>
</head>
<body>
  <h1>Orleans Ticketing - Load Test Report</h1>
  <p><strong>Test Mode:</strong> ${TEST_MODE}</p>
  <p><strong>Server:</strong> ${GRPC_SERVER}</p>
  <p><strong>Duration:</strong> ${(data.metrics.iteration_duration.values.max / 1000 / 60).toFixed(2)} minutes</p>

  <h2>Summary</h2>
  <table>
    <tr><th>Metric</th><th>Value</th></tr>
    <tr><td>Total Requests</td><td class="metric">${data.metrics.iterations.values.count}</td></tr>
    <tr><td>Success Rate</td><td class="metric ${data.metrics.success_rate?.values.rate > 0.99 ? 'success' : 'error'}">${(data.metrics.success_rate?.values.rate * 100).toFixed(2)}%</td></tr>
    <tr><td>Validation Success</td><td>${data.metrics.validation_success?.values.count || 0}</td></tr>
    <tr><td>Validation Failure</td><td>${data.metrics.validation_failure?.values.count || 0}</td></tr>
    <tr><td>Validation Error</td><td>${data.metrics.validation_error?.values.count || 0}</td></tr>
  </table>

  <h2>Latency (ms)</h2>
  <table>
    <tr><th>Percentile</th><th>Value (ms)</th><th>Status</th></tr>
    <tr><td>Average</td><td>${data.metrics.validation_latency_ms?.values.avg.toFixed(2)}</td><td>-</td></tr>
    <tr><td>Min</td><td>${data.metrics.validation_latency_ms?.values.min.toFixed(2)}</td><td>-</td></tr>
    <tr><td>Max</td><td>${data.metrics.validation_latency_ms?.values.max.toFixed(2)}</td><td>-</td></tr>
    <tr><td>P50 (median)</td><td>${data.metrics.validation_latency_ms?.values['p(50)'].toFixed(2)}</td><td>-</td></tr>
    <tr><td>P95</td><td class="${data.metrics.validation_latency_ms?.values['p(95)'] < 100 ? 'success' : 'warning'}">${data.metrics.validation_latency_ms?.values['p(95)'].toFixed(2)}</td><td>${data.metrics.validation_latency_ms?.values['p(95)'] < 100 ? '✓' : '⚠'}</td></tr>
    <tr><td>P99</td><td class="${data.metrics.validation_latency_ms?.values['p(99)'] < 200 ? 'success' : 'error'}">${data.metrics.validation_latency_ms?.values['p(99)'].toFixed(2)}</td><td>${data.metrics.validation_latency_ms?.values['p(99)'] < 200 ? '✓' : '✗'}</td></tr>
    <tr><td>P99.9</td><td>${data.metrics.validation_latency_ms?.values['p(99.9)']?.toFixed(2) || 'N/A'}</td><td>-</td></tr>
  </table>

  <h2>Thresholds</h2>
  <ul>
    ${Object.entries(data.thresholds || {}).map(([name, result]) => `
      <li class="${result.ok ? 'success' : 'error'}">${name}: ${result.ok ? '✓ PASS' : '✗ FAIL'}</li>
    `).join('')}
  </ul>
</body>
</html>
  `;

  return {
    'summary.html': htmlReport,
    'summary.json': JSON.stringify(data, null, 2),
    stdout: textSummary(data, { indent: ' ', enableColors: true }),
  };
}

// Fallback text summary
function textSummary(data, options) {
  return `
========================================
k6 Load Test Summary
========================================
Iterations:        ${data.metrics.iterations.values.count}
Success Rate:      ${(data.metrics.success_rate?.values.rate * 100).toFixed(2)}%
Avg Latency:       ${data.metrics.validation_latency_ms?.values.avg.toFixed(2)} ms
P95 Latency:       ${data.metrics.validation_latency_ms?.values['p(95)'].toFixed(2)} ms
P99 Latency:       ${data.metrics.validation_latency_ms?.values['p(99)'].toFixed(2)} ms
========================================
`;
}
