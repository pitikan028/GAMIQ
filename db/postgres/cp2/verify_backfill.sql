-- CP2 verification for Phase 3 (backfill)
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -f /docker-entrypoint-initdb.d/cp2/verify_backfill.sql
--
-- Run it BEFORE the backfill (POST /api/v1/migration/backfill) and again AFTER, and compare
-- the two outputs.
-- Read-only, safe to run any time (also while traffic is running).

-- 1) Progress. Before: not_migrated = the old orders. After: not_migrated = 0, percent = 100.
SELECT
    count(*)                                             AS total_orders,
    count(*) FILTER (WHERE "PriceAmount" IS NOT NULL)    AS migrated,
    count(*) FILTER (WHERE "PriceAmount" IS NULL)        AS not_migrated,
    round(100.0 * count(*) FILTER (WHERE "PriceAmount" IS NOT NULL) / greatest(count(*), 1), 2) AS percent_migrated
FROM "Orders";

-- 2) Data corruption check. Every number here must be 0, before AND after.
--    mismatched   = new value is different from the legacy value
--    half_written = only one of the two new columns is filled
--    bad_currency = currency is not a 3 letter code
SELECT
    count(*) FILTER (WHERE "PriceAmount" IS NOT NULL AND "PriceAmount" <> "TotalAmount")     AS mismatched,
    count(*) FILTER (WHERE ("PriceAmount" IS NULL) <> ("CurrencyCode" IS NULL))              AS half_written,
    count(*) FILTER (WHERE "CurrencyCode" IS NOT NULL AND "CurrencyCode" !~ '^[A-Z]{3}$')    AS bad_currency
FROM "Orders";

-- 3) Money check. The backfill must not create or lose a single satang.
--    legacy_sum - new_sum = missing_amount = money in orders that are not migrated yet.
--    Before: missing_amount > 0.  After: missing_amount = 0, so legacy_sum = new_sum.
SELECT
    sum("TotalAmount")                                            AS legacy_sum,
    coalesce(sum("PriceAmount"), 0)                               AS new_sum,
    sum("TotalAmount") - coalesce(sum("PriceAmount"), 0)          AS missing_amount
FROM "Orders";

-- 4) Orders per currency. Old orders can only be THB. (NULL row = not migrated yet.)
SELECT "CurrencyCode", count(*) AS orders, sum("PriceAmount") AS amount
FROM "Orders"
GROUP BY "CurrencyCode"
ORDER BY "CurrencyCode" NULLS FIRST;

-- 5) Is the CHECK on the new column validated? Before: f. After: t.
SELECT conname AS constraint_name, convalidated AS validated
FROM pg_constraint
WHERE conrelid = '"Orders"'::regclass AND conname = 'CK_Orders_PriceAmount';

-- 6) The oldest orders. These are the seeded "old data", so before the backfill the new columns
--    are empty and after it they are the same as TotalAmount.
SELECT "OrderId", "TotalAmount", "PriceAmount", "CurrencyCode", "CreatedAt"
FROM "Orders"
ORDER BY "OrderId"
LIMIT 5;
