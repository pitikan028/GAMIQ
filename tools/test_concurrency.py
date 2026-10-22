#!/usr/bin/env python3
"""CP3 concurrency verification.

Fires N requests at exactly the same moment at the same shared resource and checks that no lost
update or race condition happened:

  1. last copy      N users buy the last copy of a game          -> exactly 1 order, stock 0
  2. same wallet    N orders paid from a wallet that fits only 1  -> exactly 1 order, balance 0
  3. top-up race    N top-ups of the same wallet at once          -> balance = start + amount * successes
  4. top-up retry   same, but every client retries on 409         -> balance = start + amount * N
  5. If-Match       N top-ups that all send the same ETag         -> exactly 1 x 200, the rest 412

Every test makes its own fresh users and products, so it can be run again and again.

    python3 tools/test_concurrency.py
    python3 tools/test_concurrency.py --workers 50

Only needs Python 3.9+, no pip install. Exit code 0 when every test passes.
"""

import argparse
import json
import random
import sys
import threading
import time
import urllib.error
import urllib.request
from collections import Counter
from concurrent.futures import ThreadPoolExecutor

parser = argparse.ArgumentParser(description="CP3 concurrency verification")
parser.add_argument("--url", default="http://localhost:5087", help="API address")
parser.add_argument("--workers", type=int, default=20, help="requests sent at the same moment")
args = parser.parse_args()

if args.workers < 2:
    sys.exit("--workers must be at least 2")

BASE = args.url.rstrip("/") + "/api/v1"
RUN = f"{int(time.time())}{random.randint(100, 999)}"   # makes usernames / product names unique
PRICE = 100


def call(method, path, body=None, headers=None):
    """Returns (status, parsed json or None, response headers). Status 0 means no answer."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.loads(response.read()), response.headers
    except urllib.error.HTTPError as error:
        return error.code, None, error.headers
    except (OSError, ValueError):
        return 0, None, {}


def must(status, body, expected, what):
    if status != expected:
        sys.exit(f"Setup failed: {what} returned {status or 'no answer'} (expected {expected}). Is the API running?")
    return body


def new_user(name):
    username = f"cp3{name}{RUN}{random.randint(0, 99999)}"[:50]
    status, body, _ = call("POST", "/users", {"username": username, "email": f"{username}@test.local", "password": "Passw0rd!"})
    return must(status, body, 201, "POST /users")["userId"]


def top_up(user_id, amount):
    status, body, _ = call("POST", f"/users/{user_id}/wallet/topup", {"amount": amount})
    must(status, body, 200, "POST /users/{id}/wallet/topup")


def balance(user_id):
    status, body, _ = call("GET", f"/users/{user_id}")
    return must(status, body, 200, "GET /users/{id}")["balance"]


def new_product(stock):
    status, body, _ = call("POST", "/products", {"name": f"CP3 Race Test {RUN}", "price": PRICE, "stock": stock})
    return must(status, body, 201, "POST /products")["id"]


def stock(product_id):
    status, body, _ = call("GET", f"/products/{product_id}")
    return must(status, body, 200, "GET /products/{id}")["stock"]


def at_once(task, inputs):
    """Runs task(x) for every x, all released at the same moment by a barrier. Returns the results."""
    barrier = threading.Barrier(len(inputs))

    def run(x):
        barrier.wait()
        return task(x)

    with ThreadPoolExecutor(max_workers=len(inputs)) as pool:
        return list(pool.map(run, inputs))


def order(user_id, product_id):
    status, _, _ = call("POST", "/orders", {"userId": user_id, "items": [{"productId": product_id, "quantity": 1}]})
    return status


def show(statuses):
    counts = Counter(statuses)
    return ", ".join(f"{status or 'no answer'} x{counts[status]}" for status in sorted(counts))


results = []   # (test name, passed, details)


def check(name, conditions, statuses):
    failed = [text for ok, text in conditions if not ok]
    results.append((name, not failed))
    print(f"\n[{'PASS' if not failed else 'FAIL'}] {name}")
    print(f"       answers: {show(statuses)}")
    for ok, text in conditions:
        print(f"       {'ok ' if ok else 'BAD'} {text}")


def test_last_copy():
    """N different users, enough money each, 1 copy left -> exactly one of them gets it."""
    product_id = new_product(stock=1)
    users = [new_user("lc") for _ in range(args.workers)]
    for user_id in users:
        top_up(user_id, PRICE * 10)

    statuses = at_once(lambda u: order(u, product_id), users)
    balances = [balance(u) for u in users]
    winners = statuses.count(201)
    charged = sum(b != PRICE * 10 for b in balances)

    print(f"\nConcurrent Test Results -> Successes: {winners} (Must be 1) | "
          f"Conflicts: {statuses.count(409)} | Out of stock: {statuses.count(422)}")
    check(f"1. last copy: {args.workers} users buy the last copy at the same moment", [
        (winners == 1, f"exactly 1 order created (got {winners})"),
        (all(s in (201, 409, 422) for s in statuses), "every other answer is 409 or 422, no 5xx"),
        (stock(product_id) == 0, f"stock is 0, not negative (stock {stock(product_id)})"),
        (charged == 1, f"only the winner paid, the losers got their money back (paid: {charged})"),
    ], statuses)


def test_same_wallet():
    """1 user whose wallet fits exactly 1 game sends N orders at once -> only one is paid."""
    product_id = new_product(stock=args.workers * 10)
    user_id = new_user("sw")
    top_up(user_id, PRICE)

    statuses = at_once(lambda _: order(user_id, product_id), range(args.workers))
    winners = statuses.count(201)
    left = balance(user_id)
    sold = args.workers * 10 - stock(product_id)

    check(f"2. same wallet: {args.workers} orders from one wallet that can pay for 1", [
        (winners == 1, f"exactly 1 order created (got {winners})"),
        (all(s in (201, 409, 422) for s in statuses), "every other answer is 409 or 422, no 5xx"),
        (left == 0, f"balance is 0, not negative (balance {left})"),
        (sold == winners, f"stock went down once per order, the rest was put back (sold {sold})"),
    ], statuses)


def test_top_up_race():
    """N top-ups at once without retry. Lost update would mean balance < 10 * successes."""
    user_id = new_user("tr")
    amount = 10

    statuses = at_once(lambda _: call("POST", f"/users/{user_id}/wallet/topup", {"amount": amount})[0],
                       range(args.workers))
    successes = statuses.count(200)
    left = balance(user_id)

    check(f"3. top-up race: {args.workers} top-ups of {amount} THB on one wallet, no retry", [
        (successes >= 1, f"at least 1 top-up went through (got {successes})"),
        (all(s in (200, 409) for s in statuses), "every other answer is 409, no 5xx"),
        (left == amount * successes, f"balance {left} = {amount} x {successes} successes, no top-up was lost"),
    ], statuses)


def test_top_up_retry():
    """Same race, but the client does what 409 asks: try again. Then every top-up must land."""
    user_id = new_user("rt")
    amount = 10
    tries = []

    def top_up_until_done(_):
        for attempt in range(1, 101):
            status = call("POST", f"/users/{user_id}/wallet/topup", {"amount": amount})[0]
            if status != 409:
                tries.append(attempt)
                return status
            time.sleep(random.uniform(0.005, 0.03) * attempt)   # back off a bit so we don't collide again
        return 409

    statuses = at_once(top_up_until_done, range(args.workers))
    left = balance(user_id)

    check(f"4. top-up retry: {args.workers} top-ups of {amount} THB, client retries on 409", [
        (statuses.count(200) == args.workers, f"all {args.workers} top-ups went through in the end"),
        (left == amount * args.workers, f"balance {left} = {amount} x {args.workers}, nothing lost or doubled"),
    ], statuses)
    print(f"       needed up to {max(tries, default=0)} tries, {sum(tries) - len(tries)} retries in total")


def test_if_match():
    """N clients read the wallet, then all top up with the version they read -> only 1 may win."""
    user_id = new_user("im")
    status, _, headers = call("GET", f"/users/{user_id}")
    etag = headers.get("ETag") if status == 200 else None
    if not etag:
        sys.exit("Setup failed: GET /users/{id} has no ETag header.")

    statuses = at_once(lambda _: call("POST", f"/users/{user_id}/wallet/topup", {"amount": 10},
                                      {"If-Match": etag})[0], range(args.workers))
    stale = call("POST", f"/users/{user_id}/wallet/topup", {"amount": 10}, {"If-Match": etag})[0]
    left = balance(user_id)

    check(f"5. If-Match: {args.workers} top-ups that all send ETag {etag}", [
        (statuses.count(200) == 1, f"exactly 1 top-up went through (got {statuses.count(200)})"),
        (statuses.count(412) == args.workers - 1, f"the other {args.workers - 1} got 412 Precondition Failed"),
        (stale == 412, f"the same old ETag again later is still 412 (got {stale})"),
        (left == 10, f"balance is 10 (balance {left})"),
    ], statuses)


print(f"Target {BASE}, {args.workers} concurrent requests per test")
for test in (test_last_copy, test_same_wallet, test_top_up_race, test_top_up_retry, test_if_match):
    test()

passed = sum(ok for _, ok in results)
print(f"\n{passed} of {len(results)} tests passed")
if passed != len(results):
    print("RESULT: Race Condition Detected! See FAIL above")
    sys.exit(1)
print("RESULT: OK, no race condition and no lost update")
