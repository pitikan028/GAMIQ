-- CP2 verification for Phase 1 + 2 (expand, dual write)
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -f /docker-entrypoint-initdb.d/cp2/verify_dual_write.sql
--
-- Run it after 01_expand.sql. Read-only, safe to run any time.

-- 1) How far is the table?
--    not_migrated = old orders that still wait for the backfill (it is NOT an error in phase 2)
SELECT
    count(*)                                             AS total_orders,
    count(*) FILTER (WHERE "PriceAmount" IS NOT NULL)    AS has_new_fields,
    count(*) FILTER (WHERE "PriceAmount" IS NULL)        AS not_migrated
FROM "Orders";

-- 2) Data corruption check. Every number here must be 0.
--    mismatched   = new value is different from the legacy value
--    half_written = only one of the two new columns is filled
SELECT
    count(*) FILTER (WHERE "PriceAmount" IS NOT NULL AND "PriceAmount" <> "TotalAmount")     AS mismatched,
    count(*) FILTER (WHERE ("PriceAmount" IS NULL) <> ("CurrencyCode" IS NULL))              AS half_written
FROM "Orders";

-- 3) Is dual write really on? The newest orders must have both the old and the new fields.
SELECT "OrderId", "TotalAmount", "PriceAmount", "CurrencyCode", "CreatedAt"
FROM "Orders"
ORDER BY "OrderId" DESC
LIMIT 5;

-- 4) Orders created after dual write was turned on that are missing the new fields.
--    Should return 0 rows. (Looks for a NULL row that is newer than the first dual-written row.)
--    A request that was already running at the moment of the switch can still show up here.
--    That is not a problem: the backfill treats it like any other old order.
SELECT "OrderId", "TotalAmount", "CreatedAt"
FROM "Orders"
WHERE "PriceAmount" IS NULL
  AND "OrderId" > (SELECT min("OrderId") FROM "Orders" WHERE "PriceAmount" IS NOT NULL);
