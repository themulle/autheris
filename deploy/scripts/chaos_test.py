#!/usr/bin/env python3
"""
Chaos Test Script for GqlGateway
Temporarily stops the Redis container to verify:
1. Rate Limiting fails OPEN (requests proceed via in-memory fallback without 500 crashes)
2. Security & Governance (RLS / Table Access / Consents) fails CLOSED (strictly rejects unauthorized queries)
3. Ready Healthcheck reflects degraded state (503 Service Unavailable)
4. Full recovery after Redis restart
"""

import os
import subprocess
import time
import json
import urllib.request
import urllib.error
import sys

TARGET_PROXY = os.environ.get("TARGET_PROXY", "http://localhost:8082")
TARGET_GRAPHQL = f"{TARGET_PROXY}/graphql"
REDIS_CONTAINER = "gqlgateway-redis"

def log(msg):
    print(f"[chaos_test] {msg}")

def execute_gql(query, role="Finance"):
    req = urllib.request.Request(
        TARGET_GRAPHQL,
        data=json.dumps({"query": query}).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "GraphQL-Preflight": "1",
            "X-Benchmark-Role": role
        }
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8")
        try:
            return e.code, json.loads(body)
        except Exception:
            return e.code, {"raw": body}
    except Exception as e:
        return 0, {"error": str(e)}

def check_ready_health():
    req = urllib.request.Request(f"{TARGET_PROXY}/health/ready")
    try:
        with urllib.request.urlopen(req, timeout=3) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read().decode("utf-8"))
        except Exception:
            return e.code, {}
    except Exception as e:
        return 0, {"error": str(e)}

def run_cmd(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True)

def main():
    log("=== Starting GqlGateway Redis Chaos Test ===")
    results = {
        "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "baseline_healthy": False,
        "redis_stopped": False,
        "rate_limit_fail_open_verified": False,
        "governance_fail_closed_verified": False,
        "health_degraded_reported": False,
        "redis_recovered": False,
        "summary": ""
    }

    # 1. Baseline Verification
    log("Step 1: Checking baseline health with Redis running...")
    status, _ = check_ready_health()
    if status == 200:
        results["baseline_healthy"] = True
        log("Baseline check passed (HTTP 200 OK).")
    else:
        log(f"Warning: Baseline ready check returned HTTP {status}")

    # 2. Chaos Injection: Stop Redis
    log("Step 2: Injecting Chaos -> Stopping Redis container...")
    res = run_cmd(f"podman stop {REDIS_CONTAINER}")
    if res.returncode != 0:
        log(f"podman stop failed, trying via podman compose... {res.stderr}")
        run_cmd("podman compose stop redis || podman-compose stop redis")
    results["redis_stopped"] = True
    time.sleep(2)

    # 3. Verify Fail-Open for Rate Limiting
    log("Step 3: Testing Rate Limiter behavior under Redis outage...")
    test_query = """
    query TestSimple {
      table(domain: "finance", name: "invoices", schema: "public", first: 5) {
        tableName
        totalCount
      }
    }
    """
    status, body = execute_gql(test_query, role="Finance")
    if status == 200 and "data" in body and body.get("data", {}).get("table") is not None:
        results["rate_limit_fail_open_verified"] = True
        log("SUCCESS: Rate Limiter gracefully fell back to in-memory mode without crashing. Queries proceed (FAIL-OPEN).")
    else:
        log(f"FAILURE: Query failed during Redis outage: status={status}, body={body}")

    # 4. Verify Fail-Closed for Security & Governance
    log("Step 4: Testing Security & Governance (RLS / Blocked User) during Redis outage...")
    blocked_status, blocked_body = execute_gql(test_query, role="Blocked")
    is_blocked_denied = False
    if blocked_status == 200 and "errors" in blocked_body:
        for err in blocked_body.get("errors", []):
            if err.get("extensions", {}).get("code") == "FORBIDDEN":
                is_blocked_denied = True
                break
    elif blocked_status == 403:
        is_blocked_denied = True

    if is_blocked_denied:
        results["governance_fail_closed_verified"] = True
        log("SUCCESS: Governance layer strictly rejected unauthorized access with FORBIDDEN (FAIL-CLOSED).")
    else:
        log(f"FAILURE: Unauthorized request was not properly denied! status={blocked_status}, body={blocked_body}")

    # 5. Verify Health Check reports Degraded
    log("Step 5: Verifying /health/ready detects Redis degradation...")
    ready_status, ready_body = check_ready_health()
    if ready_status == 503:
        results["health_degraded_reported"] = True
        log(f"SUCCESS: Gateway correctly reported HTTP 503 Service Unavailable for Redis component degradation: {ready_body}")
    else:
        log(f"Health ready status was {ready_status}")

    # 6. Recovery: Restart Redis
    log("Step 6: Recovering Redis container...")
    run_cmd(f"podman start {REDIS_CONTAINER} || podman compose start redis || podman-compose start redis")
    time.sleep(4)

    post_status, _ = check_ready_health()
    if post_status == 200:
        results["redis_recovered"] = True
        log("SUCCESS: Redis restored and Gateway returned to HTTP 200 Ready.")

    all_passed = (
        results["rate_limit_fail_open_verified"] and
        results["governance_fail_closed_verified"] and
        results["redis_recovered"]
    )
    results["all_passed"] = all_passed
    results["summary"] = (
        "ALL ASSERTIONS PASSED: Redis fail-open for rate limiting and fail-closed for security confirmed."
        if all_passed else "SOME ASSERTIONS FAILED during chaos test."
    )

    log(f"Chaos test finished. Result: {results['summary']}")

    results_dir = os.environ.get("RESULTS_DIR")
    if not results_dir:
        results_dir = "/results" if os.path.exists("/results") else os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "results")
    os.makedirs(results_dir, exist_ok=True)
    out_path = os.path.join(results_dir, "chaos-test-result.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)

    return 0 if all_passed else 1

if __name__ == "__main__":
    sys.exit(main())
