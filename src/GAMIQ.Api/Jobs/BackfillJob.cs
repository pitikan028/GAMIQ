using GAMIQ.Api.Config;
using GAMIQ.Api.Dtos;
using Npgsql;

namespace GAMIQ.Api.Jobs;

// CP2 Phase 3 (BACKFILL)
// Old orders (created before dual write was turned on) still have NULL in PriceAmount and
// CurrencyCode. This job copies TotalAmount into the new columns for those rows, in the
// background, while the API keeps serving requests. Start it with POST /api/v1/migration/backfill.
//
// How it avoids locking the table:
// - Batching: one big "UPDATE Orders SET ..." would lock every old row in one long transaction.
//   We update a small batch, commit, wait a little, and repeat.
// - Cursor pagination: each batch asks for the next rows "WHERE OrderId > last id ORDER BY
//   OrderId LIMIT n". OrderId is the primary key, so that is a short index range scan and it
//   stays fast no matter how far we are (OFFSET would get slower and slower).
// - Checkpointing: the last OrderId is saved in the MigrationCheckpoints table in the SAME
//   transaction as the batch. If the API is stopped half way, the next run continues from the
//   checkpoint instead of starting over.
//
// It uses plain SQL instead of AppDbContext, so it does not depend on which columns the EF model
// has in the current phase.
public class BackfillJob
{
    private const string JobName = "orders_price_backfill";

    // A pass can miss an order that a Legacy request was still saving when the cursor went by.
    // When a pass ends and there are still NULL rows we simply go again from the start.
    private const int MaxPasses = 5;

    private readonly string _connectionString;
    private readonly MigrationState _migration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BackfillJob> _logger;

    private readonly object _startLock = new();
    private volatile BackfillStatusResponse _status = new("NotStarted", 0, 0, 0, null, null);

    public BackfillJob(
        IConfiguration configuration,
        MigrationState migration,
        IHostApplicationLifetime lifetime,
        ILogger<BackfillJob> logger)
    {
        _connectionString = configuration.GetConnectionString("Postgres")!;
        _migration = migration;
        _lifetime = lifetime;
        _logger = logger;
    }

    public BackfillStatusResponse Status => _status;

    // Returns null when the job was started, otherwise the reason why it was not.
    public string? TryStart(int batchSize, int delayMs)
    {
        // In Legacy the API still inserts orders with NULL in the new columns, so the backfill
        // would never catch up. Dual write has to be on first.
        if (_migration.Phase < MigrationPhase.DualWrite)
        {
            return "The API is still in Legacy. Run 01_expand.sql and switch to DualWrite before the backfill.";
        }

        // The job copies FROM TotalAmount. In Contract that column is not written anymore and
        // may already be dropped.
        if (_migration.Phase == MigrationPhase.Contract)
        {
            return "The API is in Contract, TotalAmount is not used anymore. There is nothing left to backfill.";
        }

        lock (_startLock)
        {
            if (_status.State == "Running")
            {
                return "The backfill is already running.";
            }

            _status = new BackfillStatusResponse("Running", 0, 0, 0, null, null);
        }

        // not awaited on purpose: the HTTP request returns right away and the job keeps going
        _ = Task.Run(() => RunAsync(batchSize, delayMs, _lifetime.ApplicationStopping));
        return null;
    }

    private async Task RunAsync(int batchSize, int delayMs, CancellationToken stop)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(stop);

            // A new, empty table. Creating it does not touch or lock Orders.
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS "MigrationCheckpoints" (
                    "JobName" character varying(100) PRIMARY KEY,
                    "LastId" integer NOT NULL,
                    "RowsDone" bigint NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL
                )
                """, stop);

            // If a row is held by somebody else, give up after 2 seconds instead of hanging.
            // The batches that already finished stay committed.
            await ExecuteAsync(connection, "SET lock_timeout = '2s'", stop);

            var (lastId, rowsDone) = await ReadCheckpointAsync(connection, stop);
            var batches = 0;
            _logger.LogInformation("Backfill started after OrderId {LastId} (batch size {BatchSize}, pause {DelayMs} ms)", lastId, batchSize, delayMs);

            for (var pass = 1; pass <= MaxPasses; pass++)
            {
                while (true)
                {
                    stop.ThrowIfCancellationRequested();

                    var (changed, newLastId) = await RunBatchAsync(connection, lastId, batchSize, stop);
                    if (changed == 0)
                    {
                        break; // nothing left after the cursor, this pass is finished
                    }

                    lastId = newLastId;
                    rowsDone += changed;
                    batches++;
                    _status = new BackfillStatusResponse("Running", lastId, rowsDone, batches, null, null);
                    _logger.LogInformation("Backfill batch {Batch}: {Changed} rows, up to OrderId {LastId}", batches, changed, lastId);

                    // let the API's own queries through between batches
                    await Task.Delay(delayMs, stop);
                }

                var remaining = await CountNotMigratedAsync(connection, stop);
                if (remaining == 0)
                {
                    // The CHECK from 01_expand.sql was added NOT VALID, so old rows were never
                    // checked. Now they have values. VALIDATE only takes a SHARE UPDATE EXCLUSIVE
                    // lock, which does not block SELECT / INSERT / UPDATE.
                    await ExecuteAsync(connection, """ALTER TABLE "Orders" VALIDATE CONSTRAINT "CK_Orders_PriceAmount" """, stop);

                    _status = new BackfillStatusResponse("Completed", lastId, rowsDone, batches, 0, null);
                    _logger.LogInformation("Backfill completed: {Rows} rows in {Batches} batches", rowsDone, batches);
                    return;
                }

                _logger.LogWarning("Backfill pass {Pass} ended with {Remaining} rows still not migrated, going again", pass, remaining);
                lastId = 0;
            }

            _status = _status with { State = "Failed", Error = "Rows are still arriving without the new fields. Is the API really in DualWrite?" };
        }
        catch (Exception ex)
        {
            // The checkpoint is already saved, so starting the job again continues from there.
            _logger.LogError(ex, "Backfill stopped");
            _status = _status with { State = "Failed", Error = ex.Message };
        }
    }

    // One batch = one short transaction: update the next rows after the cursor and save the
    // checkpoint. Either both are committed or neither.
    private static async Task<(int Changed, int LastId)> RunBatchAsync(NpgsqlConnection connection, int lastId, int batchSize, CancellationToken stop)
    {
        await using var transaction = await connection.BeginTransactionAsync(stop);

        // "PriceAmount" IS NULL: never overwrite an order the API has already dual-written.
        // 'THB': the legacy column had no currency, it was always THB.
        await using var update = new NpgsqlCommand("""
            WITH batch AS (
                SELECT "OrderId" FROM "Orders"
                WHERE "OrderId" > @lastId AND "PriceAmount" IS NULL
                ORDER BY "OrderId"
                LIMIT @batchSize
                FOR UPDATE
            ),
            done AS (
                UPDATE "Orders" o
                SET "PriceAmount" = o."TotalAmount", "CurrencyCode" = 'THB'
                FROM batch
                WHERE o."OrderId" = batch."OrderId"
                RETURNING o."OrderId"
            )
            SELECT count(*)::int, coalesce(max("OrderId"), 0) FROM done
            """, connection, transaction);
        update.Parameters.AddWithValue("lastId", lastId);
        update.Parameters.AddWithValue("batchSize", batchSize);

        int changed, newLastId;
        await using (var reader = await update.ExecuteReaderAsync(stop))
        {
            await reader.ReadAsync(stop);
            changed = reader.GetInt32(0);
            newLastId = reader.GetInt32(1);
        }

        if (changed > 0)
        {
            await using var checkpoint = new NpgsqlCommand("""
                INSERT INTO "MigrationCheckpoints" ("JobName", "LastId", "RowsDone", "UpdatedAt")
                VALUES (@job, @lastId, @changed, now())
                ON CONFLICT ("JobName") DO UPDATE
                SET "LastId" = excluded."LastId",
                    "RowsDone" = "MigrationCheckpoints"."RowsDone" + excluded."RowsDone",
                    "UpdatedAt" = excluded."UpdatedAt"
                """, connection, transaction);
            checkpoint.Parameters.AddWithValue("job", JobName);
            checkpoint.Parameters.AddWithValue("lastId", newLastId);
            checkpoint.Parameters.AddWithValue("changed", (long)changed);
            await checkpoint.ExecuteNonQueryAsync(stop);
        }

        await transaction.CommitAsync(stop);
        return (changed, newLastId);
    }

    private static async Task<(int LastId, long RowsDone)> ReadCheckpointAsync(NpgsqlConnection connection, CancellationToken stop)
    {
        await using var command = new NpgsqlCommand(
            """SELECT "LastId", "RowsDone" FROM "MigrationCheckpoints" WHERE "JobName" = @job""", connection);
        command.Parameters.AddWithValue("job", JobName);

        await using var reader = await command.ExecuteReaderAsync(stop);
        return await reader.ReadAsync(stop) ? (reader.GetInt32(0), reader.GetInt64(1)) : (0, 0);
    }

    private static async Task<long> CountNotMigratedAsync(NpgsqlConnection connection, CancellationToken stop)
    {
        await using var command = new NpgsqlCommand("""SELECT count(*) FROM "Orders" WHERE "PriceAmount" IS NULL""", connection);
        return (long)(await command.ExecuteScalarAsync(stop))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken stop)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(stop);
    }
}
