-- CP2 Phase 5 (CONTRACT), step 1 of 2: let the API stop writing TotalAmount
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/cp2/02_contract_prepare.sql
--
-- Run it while the API is in ReadNew (reads the new columns, still writes both).
--
-- Why this step exists: TotalAmount is NOT NULL. In Contract the API leaves it out of its INSERT,
-- so with NOT NULL still on, every new order would fail. And we can't drop the column first
-- either, because the API still sends it. So the order is:
--   1. this script        TotalAmount becomes nullable   (the API still writes it, nothing changes)
--   2. PUT /migration/phase {"phase":"Contract"}         (the API stops reading and writing it)
--   3. 03_contract.sql    TotalAmount is dropped         (nobody uses it anymore)
-- Each step is safe for the API version that is running at that moment.
--
-- Nothing is deleted here, it can be undone with rollback_contract.sql. Safe to run more than once.

-- Same as 01_expand.sql: don't queue behind a long transaction, fail after 2 seconds and retry.
SET lock_timeout = '2s';

-- Stop if the new columns are not complete yet. After this point they are the only copy the API
-- maintains, so the backfill has to be finished.
DO $$
DECLARE
    missing bigint;
BEGIN
    SELECT count(*) INTO missing FROM "Orders" WHERE "PriceAmount" IS NULL OR "CurrencyCode" IS NULL;
    IF missing > 0 THEN
        RAISE EXCEPTION '% orders have no PriceAmount / CurrencyCode. Run the backfill first.', missing;
    END IF;
END $$;

-- DROP NOT NULL only changes the catalog. No table rewrite and no scan, so the ACCESS EXCLUSIVE
-- lock is held for a few milliseconds.
ALTER TABLE "Orders" ALTER COLUMN "TotalAmount" DROP NOT NULL;

-- show the result: TotalAmount must be nullable (YES) now
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name = 'Orders'
ORDER BY ordinal_position;
