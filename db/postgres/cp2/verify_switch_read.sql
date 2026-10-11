-- CP2 verification for Phase 4 (switch read)
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -f /docker-entrypoint-initdb.d/cp2/verify_switch_read.sql
--
-- Run it BEFORE switching the API to ReadNew. From ReadNew on, GET /orders/{id} answers with
-- PriceAmount + CurrencyCode only, so every order must have them and they must be right.
-- Read-only, safe to run any time while TotalAmount still exists (before 03_contract.sql).

-- 1) Is every order readable from the new columns? All four numbers must be 0.
--    not_migrated = no PriceAmount yet (the backfill is not finished)
--    no_currency  = PriceAmount without CurrencyCode
--    mismatched   = a read from the new column would give a different amount than the old one
--    bad_currency = currency is not a 3 letter code
SELECT
    count(*) FILTER (WHERE "PriceAmount" IS NULL)                                            AS not_migrated,
    count(*) FILTER (WHERE "PriceAmount" IS NOT NULL AND "CurrencyCode" IS NULL)             AS no_currency,
    count(*) FILTER (WHERE "PriceAmount" IS DISTINCT FROM "TotalAmount")                     AS mismatched,
    count(*) FILTER (WHERE "CurrencyCode" IS NOT NULL AND "CurrencyCode" !~ '^[A-Z]{3}$')    AS bad_currency
FROM "Orders";

-- 2) Old read path against new read path, for the whole table. difference must be 0.00.
SELECT
    sum("TotalAmount")                         AS old_read_sum,
    sum("PriceAmount")                         AS new_read_sum,
    sum("TotalAmount") - sum("PriceAmount")    AS difference
FROM "Orders";

-- 3) One line answer. ready_to_switch_read must be t.
--    (The API checks the same thing itself: PUT /migration/phase ReadNew answers 409 if not.)
SELECT
    NOT EXISTS (
        SELECT 1 FROM "Orders"
        WHERE "PriceAmount" IS NULL OR "CurrencyCode" IS NULL OR "PriceAmount" <> "TotalAmount"
    ) AS ready_to_switch_read;

-- 4) An old (backfilled) order and a new (dual written) order, to compare with the API:
--      curl http://localhost:5087/api/v1/orders/1
--    After the switch, totalAmount in the answer is PriceAmount and currencyCode shows up.
(SELECT 'oldest' AS which, "OrderId", "TotalAmount", "PriceAmount", "CurrencyCode" FROM "Orders" ORDER BY "OrderId" LIMIT 1)
UNION ALL
(SELECT 'newest', "OrderId", "TotalAmount", "PriceAmount", "CurrencyCode" FROM "Orders" ORDER BY "OrderId" DESC LIMIT 1);
