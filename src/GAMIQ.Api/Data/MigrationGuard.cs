using GAMIQ.Api.Config;
using Npgsql;

namespace GAMIQ.Api.Data;

// CP2: checks that the Orders table is ready for a phase BEFORE the API switches to it.
// Switching too early does not fail at the switch, it fails on the next order request with a 5xx
// (for example ReadNew while old orders still have NULL in PriceAmount, or Contract while
// TotalAmount is still NOT NULL). So PUT /api/v1/migration/phase asks here first and answers 409.
//
// Plain SQL instead of AppDbContext, same reason as BackfillJob: it must work in every phase.
public class MigrationGuard
{
    private readonly string _connectionString;
    private readonly MigrationState _migration;
    private readonly ILogger<MigrationGuard> _logger;

    public MigrationGuard(IConfiguration configuration, MigrationState migration, ILogger<MigrationGuard> logger)
    {
        _connectionString = configuration.GetConnectionString("Postgres")!;
        _migration = migration;
        _logger = logger;
    }

    // Returns null when the database is ready for the phase, otherwise what is missing.
    public async Task<string?> CheckAsync(MigrationPhase target, CancellationToken stop = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stop);

        var schema = await ReadSchemaAsync(connection, stop);

        if (!schema.HasTotalAmount)
        {
            return target == MigrationPhase.Contract
                ? null
                : "Orders.TotalAmount is already dropped (03_contract.sql), the API can only run in Contract.";
        }

        if (target == MigrationPhase.Legacy)
        {
            return await LegacyValueProblemAsync(connection, stop);
        }

        if (!schema.HasNewColumns)
        {
            return "Orders.PriceAmount / CurrencyCode do not exist yet. Run 01_expand.sql first.";
        }

        if (target == MigrationPhase.DualWrite)
        {
            return await LegacyValueProblemAsync(connection, stop);
        }

        // ReadNew and Contract answer with PriceAmount, so every order needs it.
        if (await ExistsAsync(connection, """SELECT 1 FROM "Orders" WHERE "PriceAmount" IS NULL OR "CurrencyCode" IS NULL""", stop))
        {
            return "Some orders still have no PriceAmount / CurrencyCode. Run the backfill first (POST /api/v1/migration/backfill).";
        }

        if (target == MigrationPhase.ReadNew)
        {
            return null;
        }

        // Contract: the API stops sending TotalAmount, so the column must accept NULL.
        return schema.TotalAmountNullable
            ? null
            : "Orders.TotalAmount is still NOT NULL. Run 02_contract_prepare.sql first.";
    }

    // The phase is kept in memory and starts from appsettings.json (Legacy). If the API is
    // restarted after TotalAmount was dropped, Legacy would crash on every order, so we look at
    // the table once at startup and move to Contract ourselves.
    public async Task SyncPhaseWithSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var schema = await ReadSchemaAsync(connection, default);
        if (schema.HasNewColumns && !schema.HasTotalAmount && _migration.Phase != MigrationPhase.Contract)
        {
            _logger.LogWarning("Orders.TotalAmount does not exist anymore, starting in Contract instead of {Phase}", _migration.Phase);
            _migration.Set(MigrationPhase.Contract);
        }
    }

    // Legacy and DualWrite answer with TotalAmount. Orders made while the API was in Contract
    // have NULL there, so going back that far needs rollback_contract.sql first. (Going back to
    // ReadNew is always fine, it does not read TotalAmount.)
    private static async Task<string?> LegacyValueProblemAsync(NpgsqlConnection connection, CancellationToken stop) =>
        await ExistsAsync(connection, """SELECT 1 FROM "Orders" WHERE "TotalAmount" IS NULL""", stop)
            ? "Some orders have no TotalAmount (they were made in Contract). Switch to ReadNew and run rollback_contract.sql before going back this far."
            : null;

    private static async Task<OrdersSchema> ReadSchemaAsync(NpgsqlConnection connection, CancellationToken stop)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name, is_nullable = 'YES'
            FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'Orders'
              AND column_name IN ('TotalAmount', 'PriceAmount', 'CurrencyCode')
            """, connection);

        var nullable = new Dictionary<string, bool>();
        await using var reader = await command.ExecuteReaderAsync(stop);
        while (await reader.ReadAsync(stop))
        {
            nullable[reader.GetString(0)] = reader.GetBoolean(1);
        }

        return new OrdersSchema(
            HasTotalAmount: nullable.ContainsKey("TotalAmount"),
            TotalAmountNullable: nullable.GetValueOrDefault("TotalAmount"),
            HasNewColumns: nullable.ContainsKey("PriceAmount") && nullable.ContainsKey("CurrencyCode"));
    }

    // EXISTS stops at the first row it finds, it does not count the whole table.
    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, string query, CancellationToken stop)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS ({query})", connection);
        return (bool)(await command.ExecuteScalarAsync(stop))!;
    }

    private record OrdersSchema(bool HasTotalAmount, bool TotalAmountNullable, bool HasNewColumns);
}
