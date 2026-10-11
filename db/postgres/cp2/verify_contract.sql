-- CP2 verification for Phase 5 (contract)
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -f /docker-entrypoint-initdb.d/cp2/verify_contract.sql
--
-- Run it BEFORE 03_contract.sql and again AFTER, and compare the two outputs.
-- Read-only. It works with and without the TotalAmount column: to_jsonb(o) ->> 'TotalAmount' is
-- just NULL when the column does not exist, a normal "TotalAmount" reference would be an error.

-- 1) The columns. Before: TotalAmount is there (nullable after 02_contract_prepare.sql).
--    After: TotalAmount is gone, PriceAmount and CurrencyCode are NOT NULL (is_nullable = NO).
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name = 'Orders'
ORDER BY ordinal_position;

-- 2) Constraints. After: CK_Orders_TotalAmount is gone, CK_Orders_PriceAmount is still validated.
SELECT conname AS constraint_name, convalidated AS validated
FROM pg_constraint
WHERE conrelid = '"Orders"'::regclass AND contype = 'c'
ORDER BY conname;

-- 3) No data loss. total_orders and new_sum only grow by the orders made between the two runs,
--    without_price must be 0 both times.
--    legacy_sum is NULL after the drop. Before the drop it can be lower than new_sum, because
--    orders made in Contract have no TotalAmount.
SELECT
    count(*)                                              AS total_orders,
    count(*) FILTER (WHERE "PriceAmount" IS NULL
                        OR "CurrencyCode" IS NULL)        AS without_price,
    sum("PriceAmount")                                    AS new_sum,
    sum((to_jsonb(o) ->> 'TotalAmount')::numeric)         AS legacy_sum
FROM "Orders" o;

-- 4) The check that still works when TotalAmount is gone: the order price against its own
--    items (OrderItems keeps the unit price the customer paid). wrong_price must be 0 before
--    AND after, so dropping the legacy column did not change or lose any amount.
SELECT
    count(*)                                                  AS orders_checked,
    count(*) FILTER (WHERE o."PriceAmount" <> i.items_total)  AS wrong_price,
    sum(o."PriceAmount")                                      AS price_sum,
    sum(i.items_total)                                        AS items_sum
FROM "Orders" o
JOIN (
    SELECT "OrderId", sum("UnitPrice" * "Quantity") AS items_total
    FROM "OrderItems"
    GROUP BY "OrderId"
) i ON i."OrderId" = o."OrderId";

-- 5) Orders per currency.
SELECT "CurrencyCode", count(*) AS orders, sum("PriceAmount") AS amount
FROM "Orders"
GROUP BY "CurrencyCode"
ORDER BY "CurrencyCode" NULLS FIRST;

-- 6) The backfill checkpoint table. Before: t. After: f (dropped by 03_contract.sql).
SELECT to_regclass('"MigrationCheckpoints"') IS NOT NULL AS checkpoint_table_exists;
