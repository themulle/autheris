#!/usr/bin/env python3
"""
05_analyze_results.py
Parses k6 JSON summaries and generates markdown tables and CSV comparison reports.
"""

import argparse
import glob
import json
import os
import sys

def parse_k6_summary(filepath):
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception as e:
        print(f"Error reading {filepath}: {e}", file=sys.stderr)
        return None

    metrics = data.get('metrics', {})
    
    # Extract requests
    http_reqs = metrics.get('http_reqs', {}).get('values', {})
    total_reqs = http_reqs.get('count', 0)
    rps = http_reqs.get('rate', 0.0)

    # Extract failed requests
    http_failed = metrics.get('http_req_failed', {}).get('values', {})
    failed_rate = http_failed.get('rate', 0.0) * 100.0

    # Extract latency trends
    duration = metrics.get('http_req_duration', {}).get('values', {})
    avg_latency = duration.get('avg', 0.0)
    med_latency = duration.get('med', 0.0)
    p90_latency = duration.get('p(90)', 0.0)
    p95_latency = duration.get('p(95)', 0.0)
    p99_latency = duration.get('p(99)', 0.0)
    max_latency = duration.get('max', 0.0)

    # Filename format: <gateway>_<query>_<timestamp>.json
    basename = os.path.basename(filepath).replace('.json', '')
    parts = basename.split('_')
    gateway = parts[0] if len(parts) > 0 else 'unknown'
    query = parts[1] if len(parts) > 1 else 'unknown'

    return {
        'gateway': gateway,
        'query': query,
        'total_reqs': int(total_reqs),
        'rps': round(rps, 1),
        'error_pct': round(failed_rate, 2),
        'p50': round(med_latency, 2),
        'p90': round(p90_latency, 2),
        'p95': round(p95_latency, 2),
        'p99': round(p99_latency, 2),
        'max': round(max_latency, 2),
        'file': filepath
    }

def main():
    parser = argparse.ArgumentParser(description="Analyze GraphQL benchmark results")
    parser.add_argument('--results-dir', default=os.path.join(os.path.dirname(__file__), '..', 'results'), help="Results directory")
    parser.add_argument('--timestamp', default='', help="Filter by timestamp pattern")
    args = parser.parse_args()

    results_dir = os.path.abspath(args.results_dir)
    pattern = f"*{args.timestamp}*.json" if args.timestamp else "*.json"
    search_path = os.path.join(results_dir, pattern)

    files = glob.glob(search_path)
    if not files:
        print(f"No benchmark result files found matching: {search_path}")
        return

    records = []
    for f in sorted(files):
        parsed = parse_k6_summary(f)
        if parsed:
            records.append(parsed)

    if not records:
        print("No valid benchmark records parsed.")
        return

    # Sort by query, then gateway
    records.sort(key=lambda r: (r['query'], r['gateway']))

    # Generate Markdown Table
    md_lines = []
    md_lines.append("# GraphQL Gateway Benchmark Results Summary")
    md_lines.append("")
    md_lines.append("| Gateway | Query Type | Total Requests | RPS | Error % | P50 (ms) | P90 (ms) | P95 (ms) | P99 (ms) | Max (ms) |")
    md_lines.append("|:---|:---|---:|---:|---:|---:|---:|---:|---:|---:|")

    for r in records:
        md_lines.append(
            f"| **{r['gateway']}** | {r['query']} | {r['total_reqs']:,} | {r['rps']:,} | "
            f"{r['error_pct']}% | {r['p50']} | {r['p90']} | {r['p95']} | {r['p99']} | {r['max']} |"
        )

    md_output = "\n".join(md_lines)
    print("\n" + md_output + "\n")

    # Write report files
    out_prefix = f"report_{args.timestamp}" if args.timestamp else "benchmark_summary"
    md_file = os.path.join(results_dir, f"{out_prefix}.md")
    csv_file = os.path.join(results_dir, f"{out_prefix}.csv")

    with open(md_file, 'w', encoding='utf-8') as f:
        f.write(md_output + "\n")

    with open(csv_file, 'w', encoding='utf-8') as f:
        f.write("gateway,query,total_requests,rps,error_percent,p50_ms,p90_ms,p95_ms,p99_ms,max_ms\n")
        for r in records:
            f.write(f"{r['gateway']},{r['query']},{r['total_reqs']},{r['rps']},{r['error_pct']},{r['p50']},{r['p90']},{r['p95']},{r['p99']},{r['max']}\n")

    print(f"Summary markdown saved to: {md_file}")
    print(f"Summary CSV saved to:      {csv_file}")

if __name__ == '__main__':
    main()
