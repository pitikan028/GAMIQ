-- CP2 Phase 1 (EXPAND)
-- Orders.TotalAmount (THB only) is being replaced by Orders.PriceAmount + Orders.CurrencyCode.
-- This script only ADDS the new columns next to the old one. Nothing is changed or removed, so
-- the API that is running right now (it only knows TotalAmount) keeps working.
--
-- Run it while the API is serving traffic:
--   docker exec gamiq_postgres psql -U dev_user -d main_db -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/cp2/01_expand.sql
--
-- (docker-compose mounts db/postgres into /docker-entrypoint-initdb.d. Postgres only auto-runs
-- the .sql files directly in that folder, it ignores sub folders, so cp2/ is never run by itself.)
--
-- Safe to run more than once.

-- ALTER TABLE needs an ACCESS EXCLUSIVE lock for a moment. If a long transaction is holding the
-- table we give up after 2 seconds instead of waiting, because every request to Orders would
-- queue up behind our waiting ALTER. If it times out, just run the script again.
SET lock_timeout = '2s';

-- Nullable and no DEFAULT: Postgres only updates the catalog, it does not rewrite the table,
-- so this takes a few milliseconds no matter how many orders there are.
ALTER TABLE "Orders"
    ADD COLUMN IF NOT EXISTS "PriceAmount" numeric(18,2),
    ADD COLUMN IF NOT EXISTS "CurrencyCode" character varying(3);

-- Same rule as CK_Orders_TotalAmount. NOT VALID means new and updated rows are checked from now
-- on, but Postgres does not scan the existing rows while holding the lock.
-- (After the backfill it can be validated with: ALTER TABLE "Orders" VALIDATE CONSTRAINT "CK_Orders_PriceAmount";)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_Orders_PriceAmount' AND conrelid = '"Orders"'::regclass
    ) THEN
        ALTER TABLE "Orders"
            ADD CONSTRAINT "CK_Orders_PriceAmount" CHECK ("PriceAmount" >= 0) NOT VALID;
    END IF;
END $$;

-- show the result
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name = 'Orders'
ORDER BY ordinal_position;
