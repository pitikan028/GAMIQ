-- CP2 Phase 5 rollback (only possible BEFORE 03_contract.sql)
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/cp2/rollback_contract.sql
--
-- Use it when something looks wrong after the API was switched to Contract and we want to go
-- back. Orders made in Contract have NULL in TotalAmount.
--
-- Steps to go back, traffic can keep running:
--   1. PUT /migration/phase {"phase":"ReadNew"}   always allowed. The API writes TotalAmount
--                                                 again and still reads the new columns.
--   2. run this script                            fills the gap and puts NOT NULL back
-- After that the table is exactly like it was before 02_contract_prepare.sql, and DualWrite /
-- Legacy are allowed again (before the script the API answers 409 for those two).

SET lock_timeout = '2s';

-- Copy the new value back into the legacy column. Only the orders made in Contract are touched,
-- that is a few seconds of traffic, so one statement is fine here (the big table was done in
-- batches by the backfill job).
UPDATE "Orders" SET "TotalAmount" = "PriceAmount" WHERE "TotalAmount" IS NULL;

-- Put NOT NULL back, but only when the API has stopped writing NULL (so not while it is still
-- in Contract and orders keep coming in). SET NOT NULL scans the table under ACCESS EXCLUSIVE,
-- which is fine for our table size. On a big table use the CHECK NOT VALID + VALIDATE way from
-- 03_contract.sql.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "Orders" WHERE "TotalAmount" IS NULL) THEN
        ALTER TABLE "Orders" ALTER COLUMN "TotalAmount" SET NOT NULL;
    ELSE
        RAISE NOTICE 'New orders without TotalAmount arrived. Is the API still in Contract? Switch it to ReadNew and run this script again.';
    END IF;
END $$;

SELECT
    count(*) FILTER (WHERE "TotalAmount" IS NULL)                         AS without_total_amount,
    count(*) FILTER (WHERE "TotalAmount" IS DISTINCT FROM "PriceAmount")  AS mismatched
FROM "Orders";
