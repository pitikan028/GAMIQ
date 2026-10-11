-- CP2 Phase 5 (CONTRACT), step 2 of 2: drop the legacy column
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/cp2/03_contract.sql
--
-- Run it ONLY when the API is in Contract:
--   curl http://localhost:5087/api/v1/migration/phase      ->  {"phase":"Contract"}
-- In every other phase the API still has TotalAmount in its SQL and would answer 500.
--
-- THIS IS THE ONE STEP THAT CAN'T BE UNDONE. Everything before it can be rolled back by
-- switching the phase back. Take a backup first on a real system.
--
-- Safe to run more than once.

SET lock_timeout = '2s';

-- 1) Last safety check before deleting data. Stops the script (ON_ERROR_STOP) when the new
--    columns are not a complete and correct copy of the old one.
--    Orders made in Contract have NULL in TotalAmount, that is expected and not a mismatch.
DO $$
DECLARE
    missing bigint;
    mismatched bigint;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = current_schema() AND table_name = 'Orders' AND column_name = 'TotalAmount'
    ) THEN
        RAISE NOTICE 'TotalAmount is already dropped, skipping the check.';
        RETURN;
    END IF;

    SELECT count(*) INTO missing FROM "Orders" WHERE "PriceAmount" IS NULL OR "CurrencyCode" IS NULL;
    IF missing > 0 THEN
        RAISE EXCEPTION '% orders have no PriceAmount / CurrencyCode. Not dropping TotalAmount.', missing;
    END IF;

    -- dynamic SQL so the script can still be parsed when the column is gone (second run)
    EXECUTE 'SELECT count(*) FROM "Orders" WHERE "TotalAmount" IS NOT NULL AND "TotalAmount" <> "PriceAmount"'
        INTO mismatched;
    IF mismatched > 0 THEN
        RAISE EXCEPTION '% orders have a PriceAmount that is different from TotalAmount. Not dropping TotalAmount.', mismatched;
    END IF;
END $$;

-- 2) Drop the legacy column. Postgres only marks the column as dropped in the catalog, it does
--    not rewrite the table, so the ACCESS EXCLUSIVE lock is held for a few milliseconds. The
--    space is reused later by normal updates / vacuum.
--    CK_Orders_TotalAmount only uses this column, so it is dropped together with it.
ALTER TABLE "Orders" DROP COLUMN IF EXISTS "TotalAmount";

-- 3) The new columns are the only price now, so make them NOT NULL like TotalAmount was.
--    A plain SET NOT NULL scans the whole table while holding ACCESS EXCLUSIVE. The safe way:
--      a) add CHECK (... IS NOT NULL) NOT VALID   -> short lock, no scan
--      b) VALIDATE CONSTRAINT                     -> scans, but only SHARE UPDATE EXCLUSIVE,
--                                                    reads and writes keep going
--      c) SET NOT NULL                            -> Postgres 12+ sees the validated CHECK and
--                                                    skips the scan, short lock again
--      d) drop the helper CHECK
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_Orders_PriceAmount_NotNull' AND conrelid = '"Orders"'::regclass) THEN
        ALTER TABLE "Orders" ADD CONSTRAINT "CK_Orders_PriceAmount_NotNull" CHECK ("PriceAmount" IS NOT NULL) NOT VALID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_Orders_CurrencyCode_NotNull' AND conrelid = '"Orders"'::regclass) THEN
        ALTER TABLE "Orders" ADD CONSTRAINT "CK_Orders_CurrencyCode_NotNull" CHECK ("CurrencyCode" IS NOT NULL) NOT VALID;
    END IF;
END $$;

ALTER TABLE "Orders" VALIDATE CONSTRAINT "CK_Orders_PriceAmount_NotNull";
ALTER TABLE "Orders" VALIDATE CONSTRAINT "CK_Orders_CurrencyCode_NotNull";

ALTER TABLE "Orders"
    ALTER COLUMN "PriceAmount" SET NOT NULL,
    ALTER COLUMN "CurrencyCode" SET NOT NULL;

ALTER TABLE "Orders"
    DROP CONSTRAINT "CK_Orders_PriceAmount_NotNull",
    DROP CONSTRAINT "CK_Orders_CurrencyCode_NotNull";

-- 4) The backfill is finished for good, its checkpoint table is not needed anymore.
DROP TABLE IF EXISTS "MigrationCheckpoints";

-- show the result: no TotalAmount, PriceAmount and CurrencyCode are NOT NULL (NO)
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name = 'Orders'
ORDER BY ordinal_position;
