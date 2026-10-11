#!/usr/bin/env bash
# CP2: runs the whole expand-contract migration, phase by phase, against the running API.
# Used for the live demo and the fallback video. Start the traffic generator in another
# terminal FIRST, so every step happens under load:
#
#   python3 tools/traffic_gen.py --seconds 0
#   ./tools/run_migration.sh            # waits for Enter before every step
#   AUTO=1 ./tools/run_migration.sh     # no questions, 3 seconds between the steps
#
# Needs a fresh database (docker compose down -v && docker compose up -d) and the API started in
# Legacy. Other settings: API_URL, PG_CONTAINER, PG_USER, PG_DB, PAUSE.
set -euo pipefail

# Git Bash on Windows rewrites arguments that look like /unix/paths into C:/Program Files/Git/...,
# which breaks the script path we give to psql inside the container. This turns that off
# (does nothing on Linux / macOS).
export MSYS_NO_PATHCONV=1

API_URL="${API_URL:-http://localhost:5087}"
PG_CONTAINER="${PG_CONTAINER:-gamiq_postgres}"
PG_USER="${PG_USER:-dev_user}"
PG_DB="${PG_DB:-main_db}"
PAUSE="${PAUSE:-3}"

step() {
    printf '\n\033[1;36m==== %s ====\033[0m\n' "$1"
    if [ "${AUTO:-0}" = "1" ]; then sleep "$PAUSE"; else read -r -p "Enter to run... "; fi
}

# runs one of the scripts in db/postgres/cp2 (mounted into the container by docker-compose)
sql() {
    docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 -f "/docker-entrypoint-initdb.d/cp2/$1"
}

# switches the API phase, and stops the script when the API says the database is not ready (409)
phase() {
    local answer
    answer=$(curl -sS -w ' (HTTP %{http_code})' -X PUT "$API_URL/api/v1/migration/phase" \
        -H 'Content-Type: application/json' -d "{\"phase\":\"$1\"}")
    echo "$answer"
    case "$answer" in *"(HTTP 200)") ;; *) echo "Phase switch to $1 was refused, stopping."; exit 1 ;; esac
}

echo "API phase now: $(curl -sS "$API_URL/api/v1/migration/phase")"

step "Phase 1 EXPAND: add PriceAmount + CurrencyCode next to TotalAmount"
sql 01_expand.sql

step "Phase 2 DUAL WRITE: POST /orders writes the old and the new columns"
phase DualWrite
sleep 2
sql verify_dual_write.sql

step "Phase 3 BACKFILL: verification BEFORE"
sql verify_backfill.sql

step "Phase 3 BACKFILL: start the job (batches of 100, checkpoint after each batch)"
curl -sS -X POST "$API_URL/api/v1/migration/backfill" -H 'Content-Type: application/json' -d '{"batchSize":100,"delayMs":50}'
echo
while true; do
    status=$(curl -sS "$API_URL/api/v1/migration/backfill")
    echo "$status"
    case "$status" in
        *'"state":"Completed"'*) break ;;
        *'"state":"Failed"'*) echo "Backfill failed, stopping."; exit 1 ;;
    esac
    sleep 1
done

step "Phase 3 BACKFILL: verification AFTER"
sql verify_backfill.sql

step "Phase 4 SWITCH READ: check every order can be read from the new columns"
sql verify_switch_read.sql
echo "GET /orders/1 before the switch:"
curl -sS "$API_URL/api/v1/orders/1"; echo

step "Phase 4 SWITCH READ: GET /orders/{id} reads PriceAmount + CurrencyCode"
phase ReadNew
echo "GET /orders/1 after the switch (currencyCode is new):"
curl -sS "$API_URL/api/v1/orders/1"; echo

step "Phase 5 CONTRACT: verification BEFORE"
sql verify_contract.sql

step "Phase 5 CONTRACT 1/3: TotalAmount becomes nullable"
sql 02_contract_prepare.sql

step "Phase 5 CONTRACT 2/3: the API stops reading and writing TotalAmount"
phase Contract
sleep 2

step "Phase 5 CONTRACT 3/3: drop TotalAmount (can't be undone)"
sql 03_contract.sql

step "Phase 5 CONTRACT: verification AFTER"
sql verify_contract.sql
echo "GET /orders/1 after the contract:"
curl -sS "$API_URL/api/v1/orders/1"; echo

printf '\n\033[1;32mMigration finished. Stop the traffic generator with Ctrl+C and read its summary.\033[0m\n'
