// CP3 Part 3: load test with k6 (https://k6.io)
//
// 50 virtual users for 3 minutes, a mix of reads and writes:
//   POST /orders, GET /orders/{id}, GET /users/{id}/orders, GET /products, GET /users/{id}
//
//   k6 run tools/k6_script.js
//   k6 run -e BASE_URL=http://localhost:5087 -e VUS=50 -e DURATION=3m tools/k6_script.js
//
// The run fails (k6 exits with code 99) when a threshold below is not met:
//   p95 < 200 ms, p99 < 500 ms, HTTP error rate < 1%, and 99% of the answer checks pass.
//
// No database step is needed before the run. setup() gives each virtual user its own seeded
// user and puts enough money in that wallet through the API, so:
//   - a virtual user never races another one on the same wallet (no 409 from the wallet check)
//   - no order fails because the wallet is empty (no 422 from the balance check)
// Only games with plenty of stock are bought, so stock does not run out during the 3 minutes.
// That is why every answer that is not 2xx counts as an error here, 409 and 422 included.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';

const BASE = `${(__ENV.BASE_URL || 'http://localhost:5087').replace(/\/$/, '')}/api/v1`;
const VUS = Number(__ENV.VUS || 50);
const DURATION = __ENV.DURATION || '3m';

// Virtual user n uses seeded user FIRST_USER + n. Seeded users are 1..1000.
const FIRST_USER = 100;
const TOP_UP = 100000;      // the most one top-up allows; a run spends about 45,000 THB per user
const MIN_STOCK = 200;      // a run buys about 5 copies of each game, so this is far from 0
const SEEDED_ORDERS = 500;  // seeded orders are OrderId 1..500

// Think time between two requests of one virtual user. 50 users x 1 request per ~0.5 s gives a
// steady ~95 requests/second.
const THINK_MIN = 0.3;
const THINK_MAX = 0.7;

// Answers that are "normal" for a busy shop but should not happen in this setup. Counted on their
// own, so the report can tell them apart from real failures (5xx, timeouts).
const conflicts = new Counter('orders_409_conflict');
const unprocessable = new Counter('orders_422_unprocessable');
const serverErrors = new Counter('http_5xx');

export const options = {
    scenarios: {
        shop: {
            executor: 'constant-vus',
            vus: VUS,
            duration: DURATION,
            gracefulStop: '10s',
        },
    },
    thresholds: {
        // the CP3 criteria, over every request of the scenario
        'http_req_duration{scenario:shop}': ['p(95)<200', 'p(99)<500'],
        'http_req_failed{scenario:shop}': ['rate<0.01'],
        // every answer must have the right shape, not only the right status
        'checks{scenario:shop}': ['rate>0.99'],
        // the same latency limits per endpoint, so one slow endpoint can't hide behind fast ones
        'http_req_duration{endpoint:POST /orders}': ['p(95)<200', 'p(99)<500'],
        'http_req_duration{endpoint:GET /orders/{id}}': ['p(95)<200', 'p(99)<500'],
        'http_req_duration{endpoint:GET /users/{id}/orders}': ['p(95)<200', 'p(99)<500'],
        'http_req_duration{endpoint:GET /products}': ['p(95)<200', 'p(99)<500'],
        'http_req_duration{endpoint:GET /users/{id}}': ['p(95)<200', 'p(99)<500'],
        'http_5xx': ['count==0'],
    },
    summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const JSON_HEADERS = { 'Content-Type': 'application/json' };

function get(path, endpoint) {
    return record(http.get(`${BASE}${path}`, { tags: { endpoint } }));
}

function post(path, body, endpoint) {
    return record(http.post(`${BASE}${path}`, JSON.stringify(body), { headers: JSON_HEADERS, tags: { endpoint } }));
}

function record(res) {
    if (res.status >= 500) serverErrors.add(1);
    return res;
}

// Runs once before the virtual users start. Its requests are not part of the thresholds
// (they don't have the scenario:shop tag).
export function setup() {
    // 1. games with plenty of stock
    const productIds = [];
    for (let page = 1; ; page++) {
        const res = http.get(`${BASE}/products?page=${page}&pageSize=100`);
        if (res.status !== 200) {
            throw new Error(`GET /products returned ${res.status}. Is the API running at ${BASE}?`);
        }
        const body = res.json();
        for (const p of body.items) {
            if (p.stock >= MIN_STOCK) productIds.push(p.id);
        }
        if (page >= body.totalPages) break;
    }
    if (productIds.length < 50) {
        throw new Error(`Only ${productIds.length} games have ${MIN_STOCK}+ stock. Reset the database (docker compose down -v).`);
    }

    // 2. money for every virtual user's wallet (the top-up endpoint from Part 1)
    for (let vu = 1; vu <= VUS; vu++) {
        const res = http.post(`${BASE}/users/${FIRST_USER + vu}/wallet/topup`,
            JSON.stringify({ amount: TOP_UP }), { headers: JSON_HEADERS });
        if (res.status !== 200) {
            throw new Error(`Top-up of user ${FIRST_USER + vu} returned ${res.status}.`);
        }
    }

    // 3. warm-up: the first call of every endpoint pays for JIT and EF query compilation. We
    //    measure the API when it is running, not when it has just started.
    for (let i = 0; i < 20; i++) {
        http.get(`${BASE}/users/${FIRST_USER + 1}`);
        http.get(`${BASE}/users/${FIRST_USER + 1}/orders?page=1&pageSize=20`);
        http.get(`${BASE}/orders/${1 + i}`);
        http.get(`${BASE}/products?page=${1 + i}&pageSize=20`);
    }

    console.log(`${productIds.length} games with ${MIN_STOCK}+ stock, users ${FIRST_USER + 1}..${FIRST_USER + VUS} topped up with ${TOP_UP} THB`);
    return { productIds };
}

// per virtual user: the orders it placed, so GET /orders/{id} also reads fresh rows
const myOrders = [];

function pick(list) {
    return list[Math.floor(Math.random() * list.length)];
}

function placeOrder(userId, productIds) {
    const res = post('/orders', { userId, items: [{ productId: pick(productIds), quantity: 1 }] }, 'POST /orders');
    if (res.status === 409) conflicts.add(1);
    if (res.status === 422) unprocessable.add(1);

    const ok = check(res, {
        'POST /orders is 201': (r) => r.status === 201,
        'POST /orders has orderId and items': (r) => r.status === 201 && r.json('orderId') > 0 && r.json('items.#') === 1,
    });
    if (ok) myOrders.push(res.json('orderId'));
}

function readOrder() {
    // half fresh orders of this run, half old seeded orders
    const id = myOrders.length > 0 && Math.random() < 0.5 ? pick(myOrders) : 1 + Math.floor(Math.random() * SEEDED_ORDERS);
    const res = get(`/orders/${id}`, 'GET /orders/{id}');
    check(res, {
        'GET /orders/{id} is 200': (r) => r.status === 200,
        'GET /orders/{id} is the asked order': (r) => r.status === 200 && r.json('orderId') === id && r.json('items.#') > 0,
    });
}

function readOrderList(userId) {
    const res = get(`/users/${userId}/orders?page=1&pageSize=20`, 'GET /users/{id}/orders');
    check(res, {
        'GET /users/{id}/orders is 200': (r) => r.status === 200,
        'GET /users/{id}/orders has a page': (r) => r.status === 200 && Array.isArray(r.json('items')) && r.json('totalCount') >= 0,
    });
}

function readProducts() {
    const page = 1 + Math.floor(Math.random() * 12);    // 1,200 games, 100 per page at most
    const res = get(`/products?page=${page}&pageSize=20`, 'GET /products');
    check(res, {
        'GET /products is 200': (r) => r.status === 200,
        'GET /products has 20 games': (r) => r.status === 200 && r.json('items.#') === 20,
    });
}

function readUser(userId) {
    const res = get(`/users/${userId}`, 'GET /users/{id}');
    check(res, {
        'GET /users/{id} is 200': (r) => r.status === 200,
        'GET /users/{id} has a balance': (r) => r.status === 200 && r.json('userId') === userId && r.json('balance') >= 0,
    });
}

// One iteration = one request, picked by weight. A shop is read much more than it is written to,
// so 20% of the requests are orders and 80% are reads.
export default function (data) {
    const userId = FIRST_USER + exec.vu.idInTest;
    const roll = Math.random();

    if (roll < 0.20) placeOrder(userId, data.productIds);
    else if (roll < 0.40) readOrder();
    else if (roll < 0.60) readOrderList(userId);
    else if (roll < 0.80) readProducts();
    else readUser(userId);

    sleep(THINK_MIN + Math.random() * (THINK_MAX - THINK_MIN));
}
