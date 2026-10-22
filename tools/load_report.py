#!/usr/bin/env python3
"""CP3 Part 4: turns the raw k6 results into the charts and tables of the performance report.

    k6 run --out csv=k6_raw.csv --summary-export docs/cp3/k6_summary.json tools/k6_script.js
    python3 tools/load_report.py k6_raw.csv

Writes into docs/cp3/:
  rps.svg          requests per second over time
  latency.svg      p95 and p99 latency over time, with the 200 / 500 ms limits
  errors.svg       HTTP error rate over time, with the 1% limit
  timeseries.csv   the numbers behind the three charts (one row per 5 seconds)
and prints the Markdown tables for the README (whole run and per endpoint).

Only the requests of the load test itself are used (scenario "shop"), not the setup / warm-up.
Only needs Python 3.9+, no pip install. The raw CSV is big, so it is not kept in git.
"""

import argparse
import csv
import math
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path

BUCKET_SECONDS = 5
STABLE_WINDOW = 10      # RPS stability is judged on 10 second windows
STABLE_LIMIT = 0.10     # every full window within +-10% of the mean

parser = argparse.ArgumentParser(description="CP3 load test report")
parser.add_argument("csv", help="file written by k6 --out csv=...")
parser.add_argument("--out", default="docs/cp3", help="where to write the charts")
args = parser.parse_args()

out = Path(args.out)
out.mkdir(parents=True, exist_ok=True)


# --- read ------------------------------------------------------------------------------------

def endpoint_of(extra_tags):
    for part in extra_tags.split("&"):
        if part.startswith("endpoint="):
            return part[len("endpoint="):]
    return "?"


requests = []   # (timestamp seconds, endpoint, status, duration ms)

# k6 writes one http_req_duration row per request, with the status and tags of that request
with open(args.csv, newline="") as f:
    for row in csv.DictReader(f):
        if row["metric_name"] == "http_req_duration" and row.get("scenario") == "shop":
            requests.append((int(row["timestamp"]), endpoint_of(row.get("extra_tags", "")),
                             int(row["status"] or 0), float(row["metric_value"])))

if not requests:
    sys.exit("No requests of scenario 'shop' in that file. Was it written by tools/k6_script.js?")

start = min(t for t, *_ in requests)
end = max(t for t, *_ in requests)


# --- numbers ---------------------------------------------------------------------------------

def percentile(values, p):
    """Same as k6: linear interpolation between the closest ranks."""
    values = sorted(values)
    if not values:
        return float("nan")
    k = (len(values) - 1) * p / 100
    lo, hi = math.floor(k), math.ceil(k)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def is_error(status):
    return not 200 <= status < 300


buckets = defaultdict(list)
for t, endpoint, status, ms in requests:
    buckets[(t - start) // BUCKET_SECONDS].append((status, ms))

# the last bucket is usually cut short by the end of the test, leave it out of the charts
last_full = (end - start + 1) // BUCKET_SECONDS - 1
series = []
for b in range(0, last_full + 1):
    items = buckets.get(b, [])
    times = [ms for _, ms in items]
    errors = sum(is_error(s) for s, _ in items)
    series.append({
        "second": (b + 1) * BUCKET_SECONDS,
        "rps": len(items) / BUCKET_SECONDS,
        "p95": percentile(times, 95),
        "p99": percentile(times, 99),
        "error_rate": 100 * errors / len(items) if items else 0.0,
    })

with open(out / "timeseries.csv", "w", newline="") as f:
    writer = csv.writer(f)
    writer.writerow(["second", "rps", "p95_ms", "p99_ms", "error_rate_percent"])
    for s in series:
        writer.writerow([s["second"], f"{s['rps']:.1f}", f"{s['p95']:.1f}", f"{s['p99']:.1f}", f"{s['error_rate']:.2f}"])


# --- charts ----------------------------------------------------------------------------------
# Plain SVG so GitHub shows it in the README. Colors are tokens with a dark mode version;
# series 1 blue / series 2 orange from the validated default palette, text never in a series color.

W, H = 760, 300
LEFT, RIGHT, TOP, BOTTOM = 64, 112, 48, 44

STYLE = """
<style>
  svg { --surface: #fcfcfb; --text-1: #0b0b0b; --text-2: #52514e; --grid: #e4e3df;
        --s1: #2a78d6; --s2: #eb6834; --limit: #8a8984; }
  @media (prefers-color-scheme: dark) {
    svg { --surface: #1a1a19; --text-1: #f0efec; --text-2: #c3c2b7; --grid: #383835;
          --s1: #3987e5; --s2: #d95926; --limit: #8f8e88; }
  }
  .bg { fill: var(--surface); }
  .title { fill: var(--text-1); font: 600 15px system-ui, -apple-system, sans-serif; }
  .sub { fill: var(--text-2); font: 12px system-ui, -apple-system, sans-serif; }
  .tick { fill: var(--text-2); font: 11px system-ui, -apple-system, sans-serif; }
  .label { fill: var(--text-1); font: 12px system-ui, -apple-system, sans-serif; }
  .grid { stroke: var(--grid); stroke-width: 1; }
  .axis { stroke: var(--text-2); stroke-width: 1; }
  .limit { stroke: var(--limit); stroke-width: 1.5; stroke-dasharray: 6 4; }
  .s1 { stroke: var(--s1); fill: none; stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }
  .s2 { stroke: var(--s2); fill: none; stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }
  .k1 { fill: var(--s1); } .k2 { fill: var(--s2); }
</style>"""


def nice_max(value):
    if value <= 0:
        return 1
    exp = 10 ** math.floor(math.log10(value))
    for m in (1, 2, 2.5, 5, 10):
        if value <= m * exp:
            return m * exp
    return 10 * exp


def chart(filename, title, subtitle, unit, lines, limits, y_max):
    """lines: [(css class, label, [values])], limits: [(value, label)]"""
    pw, ph = W - LEFT - RIGHT, H - TOP - BOTTOM
    x_max = series[-1]["second"]

    def x(sec):
        return LEFT + pw * sec / x_max

    def y(v):
        return TOP + ph * (1 - min(v, y_max) / y_max)

    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img">',
             f"<title>{title}</title>", STYLE,
             f'<rect class="bg" width="{W}" height="{H}" rx="8"/>',
             f'<text class="title" x="{LEFT}" y="22">{title}</text>',
             f'<text class="sub" x="{LEFT}" y="38">{subtitle}</text>']

    for i in range(5):                       # 4 grid steps
        v = y_max * i / 4
        parts.append(f'<line class="grid" x1="{LEFT}" x2="{LEFT + pw}" y1="{y(v):.1f}" y2="{y(v):.1f}"/>')
        text = f"{v:g}{unit}"
        parts.append(f'<text class="tick" x="{LEFT - 8}" y="{y(v) + 4:.1f}" text-anchor="end">{text}</text>')

    for sec in range(0, x_max + 1, 30):
        parts.append(f'<text class="tick" x="{x(sec):.1f}" y="{TOP + ph + 18}" text-anchor="middle">{sec // 60}:{sec % 60:02d}</text>')
    parts.append(f'<text class="tick" x="{LEFT + pw}" y="{TOP + ph + 34}" text-anchor="end">time into the test (m:ss)</text>')
    parts.append(f'<line class="axis" x1="{LEFT}" x2="{LEFT + pw}" y1="{TOP + ph}" y2="{TOP + ph}"/>')

    for value, label in limits:
        parts.append(f'<line class="limit" x1="{LEFT}" x2="{LEFT + pw}" y1="{y(value):.1f}" y2="{y(value):.1f}"/>')
        parts.append(f'<text class="sub" x="{LEFT + pw + 8}" y="{y(value) + 4:.1f}">{label}</text>')

    for css, label, values in lines:
        points = " ".join(f"{x(s['second']):.1f},{y(v):.1f}" for s, v in zip(series, values))
        parts.append(f'<polyline class="{css}" points="{points}"/>')

    # Direct labels at the end of the lines (a color key next to text in text color). Only with
    # two or more lines, a single line is named by the title. Pushed apart when the lines end
    # close together, the higher value on top.
    if len(lines) > 1:
        ends = sorted(((y(values[-1]), css, label) for css, label, values in lines), reverse=True)
        placed = []
        for pos, css, label in ends:
            if placed and placed[-1] - pos < 15:
                pos = placed[-1] - 15
            placed.append(pos)
            parts.append(f'<rect class="k{css[1]}" x="{LEFT + pw + 8}" y="{pos - 5:.1f}" width="10" height="10" rx="2"/>')
            parts.append(f'<text class="label" x="{LEFT + pw + 22}" y="{pos + 4:.1f}">{label}</text>')

    if len(lines) > 1:                       # legend for two or more series
        lx = LEFT + pw
        for css, label, _ in reversed(lines):
            width = 22 + 7 * len(label)
            lx -= width
            parts.append(f'<rect class="k{css[1]}" x="{lx}" y="12" width="10" height="10" rx="2"/>')
            parts.append(f'<text class="label" x="{lx + 14}" y="21">{label}</text>')

    parts.append("</svg>")
    (out / filename).write_text("\n".join(parts) + "\n")


rps_values = [s["rps"] for s in series]
p95_values = [s["p95"] for s in series]
p99_values = [s["p99"] for s in series]
err_values = [s["error_rate"] for s in series]

chart("rps.svg", "Requests per second", f"50 virtual users, every point is {BUCKET_SECONDS} s",
      "", [("s1", "RPS", rps_values)], [], nice_max(max(rps_values) * 1.15))
chart("latency.svg", "Latency p95 and p99", f"all endpoints together, every point is {BUCKET_SECONDS} s",
      " ms", [("s1", "p95", p95_values), ("s2", "p99", p99_values)],
      [(200, "p95 limit"), (500, "p99 limit")], 600)
chart("errors.svg", "HTTP error rate", "non-2xx answers and requests without an answer",
      "%", [("s1", "errors", err_values)], [(1, "1% limit")], 2)


# --- tables ----------------------------------------------------------------------------------

duration = end - start + 1


def table_row(name, items):
    times = [ms for _, ms in items]
    errors = sum(is_error(s) for s, _ in items)
    return (f"| {name} | {len(items):,} | {len(items) / duration:.1f} | {percentile(times, 50):.1f} | "
            f"{percentile(times, 95):.1f} | {percentile(times, 99):.1f} | {max(times):.1f} | "
            f"{errors} ({100 * errors / len(items):.2f}%) |")


print("| Endpoint | Requests | RPS | p50 ms | p95 ms | p99 ms | max ms | Errors |")
print("|---|---:|---:|---:|---:|---:|---:|---:|")
by_endpoint = defaultdict(list)
for _, endpoint, status, ms in requests:
    by_endpoint[endpoint].append((status, ms))
for endpoint in sorted(by_endpoint, key=lambda e: -len(by_endpoint[e])):
    print(table_row(f"`{endpoint}`", by_endpoint[endpoint]))
print(table_row("**all**", [(s, ms) for _, _, s, ms in requests]))

print("\nStatus codes:", ", ".join(f"{s} x{n:,}" for s, n in sorted(Counter(s for _, _, s, _ in requests).items())))

# RPS stability on full 10 second windows
windows = Counter((t - start) // STABLE_WINDOW for t, *_ in requests)
full = [windows[w] / STABLE_WINDOW for w in range(duration // STABLE_WINDOW)]
mean = statistics.mean(full)
worst = max(abs(v - mean) / mean for v in full)
cv = statistics.pstdev(full) / mean
stable = worst <= STABLE_LIMIT
print(f"\nRPS per {STABLE_WINDOW} s window: mean {mean:.1f}, min {min(full):.1f}, max {max(full):.1f}, "
      f"std dev {statistics.pstdev(full):.2f} (CV {100 * cv:.1f}%), worst window {100 * worst:.1f}% from the mean")
print(f"[{'PASS' if stable else 'FAIL'}] RPS stable: every {STABLE_WINDOW} s window within {100 * STABLE_LIMIT:.0f}% of the mean")
print(f"\nCharts and timeseries.csv written to {out}/")

sys.exit(0 if stable else 1)
