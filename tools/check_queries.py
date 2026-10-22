#!/usr/bin/env python3
"""CP3 N+1 check.

Proves that the read endpoints need the same number of database round trips no matter how much
data they return. An endpoint with an N+1 problem would need one more query for every extra order
or item, so its count would grow with the data.

The API (in Development) puts the counts of every request in two response headers:
  X-Sql-Queries     SQL commands sent to PostgreSQL
  X-Mongo-Commands  commands sent to MongoDB

  1. GET /users/{id}           -> exactly 1 SQL query
  2. GET /orders/{id}          order with 1 item vs 10 items      -> 1 SQL query both times
  3. GET /users/{id}/orders    user with 1 order vs 50 orders     -> same count both times (3)
  4. GET /products             page of 1 game vs 100 games        -> same count both times (2 Mongo, 0 SQL)

Every run makes its own user, products and orders, so it can be run again and again.

    python3 tools/check_queries.py

Only needs Python 3.9+, no pip install. Exit code 0 when every check passes.
"""

import argparse
import json
import random
import sys
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser(description="CP3 N+1 check")
parser.add_argument("--url", default="http://localhost:5087", help="API address")
args = parser.parse_args()

BASE = args.url.rstrip("/") + "/api/v1"
RUN = f"{int(time.time())}{random.randint(100, 999)}"   # makes usernames / product names unique
MANY_ORDERS = 50
MANY_ITEMS = 10


def call(method, path, body=None):
    """Returns (status, parsed json or None, sql queries, mongo commands). Status 0 means no answer."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            headers, status, body = response.headers, response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        headers, status, body = error.headers, error.code, None
    except (OSError, ValueError):
        return 0, None, None, None

    sql, mongo = headers.get("X-Sql-Queries"), headers.get("X-Mongo-Commands")
    return status, body, int(sql) if sql else None, int(mongo) if mongo else None


def must(status, body, expected, what):
    if status != expected:
        sys.exit(f"Setup failed: {what} returned {status or 'no answer'} (expected {expected}). Is the API running?")
    return body


def new_order(user_id, product_ids):
    status, body, _, _ = call("POST", "/orders", {
        "userId": user_id,
        "items": [{"productId": product_id, "quantity": 1} for product_id in product_ids],
    })
    return must(status, body, 201, "POST /orders")["orderId"]


# --- setup -----------------------------------------------------------------------------------

status, _, sql, _ = call("GET", "/users/1")
if status == 0:
    sys.exit(f"Cannot reach the API at {BASE}. Is it running?")
if sql is None:
    sys.exit("The API does not send X-Sql-Queries. Start it in Development (dotnet run does that).")

print("Setting up a fresh user, products and orders...")

username = f"cp3q{RUN}"[:50]
status, body, _, _ = call("POST", "/users", {"username": username, "email": f"{username}@test.local", "password": "Passw0rd!"})
user_id = must(status, body, 201, "POST /users")["userId"]
must(*call("POST", f"/users/{user_id}/wallet/topup", {"amount": 100000})[:2], 200, "POST /users/{id}/wallet/topup")

product_ids = []
for i in range(MANY_ITEMS):
    status, body, _, _ = call("POST", "/products", {"name": f"CP3 Query Test {RUN} #{i}", "price": 10, "stock": 1000})
    product_ids.append(must(status, body, 201, "POST /products")["id"])

small_order = new_order(user_id, product_ids[:1])            # 1 item
big_order = new_order(user_id, product_ids)                  # MANY_ITEMS items

# a second user with exactly one order, to compare against the user with MANY_ORDERS orders
username = f"cp3q1{RUN}"[:50]
status, body, _, _ = call("POST", "/users", {"username": username, "email": f"{username}@test.local", "password": "Passw0rd!"})
one_order_user = must(status, body, 201, "POST /users")["userId"]
must(*call("POST", f"/users/{one_order_user}/wallet/topup", {"amount": 100})[:2], 200, "POST /users/{id}/wallet/topup")
new_order(one_order_user, product_ids[:1])

for _ in range(MANY_ORDERS - 2):                             # the user already has 2 orders
    new_order(user_id, product_ids[:1])

# --- checks ----------------------------------------------------------------------------------

results = []


def check(name, passed, detail):
    results.append(passed)
    print(f"[{'PASS' if passed else 'FAIL'}] {name:<52} {detail}")


print()

status, body, sql, _ = call("GET", f"/users/{user_id}")
check("1. GET /users/{id}", status == 200 and sql == 1, f"status {status}, {sql} SQL")

status_a, body_a, sql_a, _ = call("GET", f"/orders/{small_order}")
status_b, body_b, sql_b, _ = call("GET", f"/orders/{big_order}")
items_ok = status_a == status_b == 200 and len(body_a["items"]) == 1 and len(body_b["items"]) == MANY_ITEMS
check("2. GET /orders/{id}, 1 item vs %d items" % MANY_ITEMS,
      items_ok and sql_a == sql_b == 1,
      f"{sql_a} SQL vs {sql_b} SQL")

status_a, body_a, sql_a, _ = call("GET", f"/users/{one_order_user}/orders?pageSize=100")
status_b, body_b, sql_b, _ = call("GET", f"/users/{user_id}/orders?pageSize=100")
lists_ok = (status_a == status_b == 200
            and body_a["totalCount"] == 1 and len(body_b["items"]) == MANY_ORDERS
            and sorted(o["itemCount"] for o in body_b["items"])[-1] == MANY_ITEMS)
check("3. GET /users/{id}/orders, 1 order vs %d orders" % MANY_ORDERS,
      lists_ok and sql_a == sql_b and sql_a is not None and sql_a <= 3,
      f"{sql_a} SQL vs {sql_b} SQL")

status_a, body_a, sql_a, mongo_a = call("GET", "/products?pageSize=1")
status_b, body_b, sql_b, mongo_b = call("GET", "/products?pageSize=100")
pages_ok = status_a == status_b == 200 and len(body_a["items"]) == 1 and len(body_b["items"]) == 100
check("4. GET /products, 1 game vs 100 games",
      pages_ok and mongo_a == mongo_b == 2 and sql_a == sql_b == 0,
      f"{mongo_a} Mongo vs {mongo_b} Mongo, {sql_b} SQL")

status, body, _, _ = call("GET", f"/users/{user_id}/orders?pageSize=1")
check("5. paging: GET /users/{id}/orders?pageSize=1",
      status == 200 and len(body["items"]) == 1 and body["totalCount"] == MANY_ORDERS
      and body["totalPages"] == MANY_ORDERS,
      f"1 of {body['totalCount'] if body else '?'} orders, {body['totalPages'] if body else '?'} pages")

print(f"\n{sum(results)} of {len(results)} checks passed")
sys.exit(0 if all(results) else 1)
