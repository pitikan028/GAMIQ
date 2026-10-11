-- CP2 demo helper (NOT part of the migration)
-- The traffic generator buys games with the seeded users. Their wallets only have 0 - 5,000 THB,
-- so after a few seconds most orders come back 422 (insufficient balance) instead of 201 and we
-- would not really be testing the write path. This gives every wallet enough money for the demo.
--
--   docker exec gamiq_postgres psql -U dev_user -d main_db -f /docker-entrypoint-initdb.d/cp2/demo_topup_wallets.sql
--
-- Run it BEFORE starting the traffic generator. It changes every wallet row, so an order that
-- is being placed at that exact moment would get a 409 (wallet row version).
-- Local demo data only. Never run something like this on real wallets.

UPDATE "Accounts" SET "Balance" = 1000000 WHERE "Balance" < 1000000;

SELECT count(*) AS wallets, min("Balance") AS min_balance FROM "Accounts";
