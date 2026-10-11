#!/usr/bin/env python3
"""CP2 traffic generator.

Sends a steady mix of POST /orders and GET /orders/{id} to the API, so we can run every migration
step "under load" and see if any request fails, returns broken data or gets slow.

    python3 tools/traffic_gen.py --rps 20 --seconds 60
    python3 tools/traffic_gen.py --seconds 0          # keep going until Ctrl+C (live demo)

Only needs Python 3.9+, no pip install.

The run passes (exit code 0) when all three CP2 criteria hold:
  - 0 HTTP 5xx and 0 requests without an answer
  - 0 bad data: a 2xx answer with a missing field or a total that does not match its items
  - p99 latency below 500 ms
409 (two orders on the same wallet at the same moment) and 422 (out of stock / money) are normal
answers from a healthy API, they are counted but they are not errors.
"""

import argparse
import json
import random
import re
import sys
import threading
import time
import urllib.error
import urllib.request
from collections import Counter
from concurrent.futures import ThreadPoolExecutor

P99_LIMIT_MS = 500

parser = argparse.ArgumentParser(description="CP2 traffic generator")
parser.add_argument("--url", default="http://localhost:5087", help="API address")
parser.add_argument("--rps", type=int, default=20, help="requests per second, half POST half GET")
parser.add_argument("--seconds", type=int, default=60, help="how long to run, 0 = until Ctrl+C")
parser.add_argument("--users", type=int, default=1000, help="seeded users are UserId 1..N")
parser.add_argument("--seed-orders", type=int, default=500, help="seeded orders are OrderId 1..N")
args = parser.parse_args()

if args.rps < 1 or args.seconds < 0 or args.users < 1 or args.seed_orders < 1:
    sys.exit("--rps, --users and --seed-orders must be at least 1, --seconds at least 0")

BASE = args.url.rstrip("/") + "/api/v1"

lock = threading.Lock()
results = []            # (method, status, milliseconds), status 0 = no answer
bad_data = []           # what was wrong with a 2xx answer
created_order_ids = []  # orders made in this run, so GET can read them back
with_currency = [0]     # 2xx answers that had currencyCode, so they were read from the new columns


def call(method, path, body=None):
    """Returns (status, parsed json or None). Status 0 means the client got no usable answer."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, None      # 4xx / 5xx
    except (OSError, ValueError):
        return 0, None               # connection refused, timeout, body is not JSON


def check_order(order, expected_id=None):
    """Data corruption / missing field check. Returns a message, or None when the order is fine."""
    for field in ("orderId", "userId", "totalAmount", "status", "createdAt", "items"):
        if order.get(field) is None:
            return f"order {order.get('orderId')}: field '{field}' is missing"

    if expected_id is not None and order["orderId"] != expected_id:
        return f"asked for order {expected_id} but got {order['orderId']}"

    if not order["items"]:
        return f"order {order['orderId']}: no items"

    # the total must be what the customer paid for the items, in every migration phase
    items_total = sum(item["unitPrice"] * item["quantity"] for item in order["items"])
    if abs(order["totalAmount"] - items_total) > 0.005:
        return f"order {order['orderId']}: totalAmount {order['totalAmount']} but items add up to {items_total}"

    # Phase 4: once the API reads the new columns the answer also has currencyCode. Before that
    # the field is not there at all, which is fine. But if it is there it has to be a real code.
    if "currencyCode" in order:
        if not re.fullmatch("[A-Z]{3}", str(order["currencyCode"])):
            return f"order {order['orderId']}: currencyCode is '{order['currencyCode']}'"
        with lock:
            with_currency[0] += 1

    return None


def record(method, status, started, problem=None):
    with lock:
        results.append((method, status, (time.perf_counter() - started) * 1000))
        if problem:
            bad_data.append(problem)


def post_order():
    body = {
        "userId": random.randint(1, args.users),
        "items": [{"productId": random.choice(product_ids), "quantity": 1}],
    }
    started = time.perf_counter()
    status, order = call("POST", "/orders", body)

    problem = None
    if status == 201:
        problem = check_order(order)
        if problem is None:
            with lock:
                created_order_ids.append(order["orderId"])
    record("POST", status, started, problem)


def get_order():
    # half of the reads go to old (seeded) orders, the other half to orders made in this run,
    # so we read rows from before and after the migration
    with lock:
        own = random.choice(created_order_ids) if created_order_ids else None
    order_id = own if own is not None and random.random() < 0.5 else random.randint(1, args.seed_orders)

    started = time.perf_counter()
    status, order = call("GET", f"/orders/{order_id}")
    record("GET", status, started, check_order(order, order_id) if status == 200 else None)


def percentile(sorted_values, percent):
    index = max(0, -(-len(sorted_values) * percent // 100) - 1)   # ceil without importing math
    return sorted_values[index]


def progress(elapsed):
    with lock:
        statuses = [status for _, status, _ in results]
        bad = len(bad_data)
    print(f"  {elapsed:4.0f}s  done {len(statuses):6}  2xx {sum(200 <= s < 300 for s in statuses):6}"
          f"  4xx {sum(400 <= s < 500 for s in statuses):5}  5xx {sum(s >= 500 for s in statuses):4}"
          f"  failed {sum(s == 0 for s in statuses):4}  bad data {bad:4}", flush=True)


def summary(elapsed):
    with lock:
        done = list(results)
        bad = list(bad_data)

    if not done:
        print("No request was sent.")
        return 1

    print(f"\nSent {len(done)} requests in {elapsed:.1f} s ({len(done) / elapsed:.1f} requests/second)\n")
    print("Method  Status  Count")
    for (method, status), count in sorted(Counter((m, s) for m, s, _ in done).items(), reverse=True):
        print(f"{method:<6}  {status if status else 'failed':<6}  {count:5}")

    print("\nLatency (ms)     p50      p95      p99      max")
    for method in ("POST", "GET", "all"):
        times = sorted(ms for m, _, ms in done if method in (m, "all"))
        if times:
            print(f"{method:<12} {percentile(times, 50):7.0f}  {percentile(times, 95):7.0f}"
                  f"  {percentile(times, 99):7.0f}  {times[-1]:7.0f}")

    server_errors = sum(status >= 500 for _, status, _ in done)
    failed = sum(status == 0 for _, status, _ in done)
    p99 = percentile(sorted(ms for _, _, ms in done), 99)

    ok_answers = sum(200 <= status < 300 for _, status, _ in done)
    print(f"\nRead from the new columns (answer has currencyCode): {with_currency[0]} of {ok_answers} 2xx answers")

    for problem in bad[:10]:
        print(f"BAD DATA: {problem}")

    checks = [
        (server_errors == 0 and failed == 0, f"0 HTTP 5xx (5xx: {server_errors}, no answer: {failed})"),
        (not bad, f"0 data corruption / missing fields (bad answers: {len(bad)})"),
        (p99 < P99_LIMIT_MS, f"p99 below {P99_LIMIT_MS} ms (p99: {p99:.0f} ms)"),
    ]
    print()
    for passed, text in checks:
        print(f"[{'PASS' if passed else 'FAIL'}] {text}")

    ok = all(passed for passed, _ in checks)
    print("RESULT: OK, zero downtime" if ok else "RESULT: PROBLEM, see FAIL above")
    return 0 if ok else 1


# Pick the games to buy. Only ones with plenty of stock, otherwise we would just measure 422s.
product_ids = []
for page in (1, 2, 3):
    status, body = call("GET", f"/products?page={page}&pageSize=100")
    if status != 200:
        sys.exit(f"Cannot load products from {BASE} (status {status or 'no answer'}). Is the API running?")
    product_ids += [p["id"] for p in body["items"] if p["stock"] >= 20]

if not product_ids:
    sys.exit("No product with enough stock was found.")

duration = f"{args.seconds} s" if args.seconds else "until Ctrl+C"
print(f"Target {BASE}, {args.rps} requests/second for {duration}, {len(product_ids)} products, users 1..{args.users}")

start = time.perf_counter()
next_report = 1
sent = 0

# Requests are sent on a fixed schedule from a thread pool. We do not wait for one answer before
# sending the next request, so a slow API still gets the full load (and it shows up in the p99).
with ThreadPoolExecutor(max_workers=max(32, args.rps * 4)) as pool:
    try:
        while args.seconds == 0 or sent < args.rps * args.seconds:
            wait = sent / args.rps - (time.perf_counter() - start)   # request i is due at i / rps
            if wait > 0:
                time.sleep(wait)

            pool.submit(post_order if sent % 2 == 0 else get_order)
            sent += 1

            if time.perf_counter() - start >= next_report:
                progress(time.perf_counter() - start)
                next_report = int(time.perf_counter() - start) + 1
    except KeyboardInterrupt:
        print("\nStopping, waiting for the last answers...")

sys.exit(summary(time.perf_counter() - start))
