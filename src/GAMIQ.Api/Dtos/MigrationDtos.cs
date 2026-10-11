using System.ComponentModel.DataAnnotations;

namespace GAMIQ.Api.Dtos;

public record SetMigrationPhaseRequest([Required] string Phase);

public record MigrationPhaseResponse(string Phase);

// defaults: 100 rows per batch, 50 ms pause between batches
public record StartBackfillRequest(
    [Range(1, 10000)] int BatchSize = 100,
    [Range(0, 60000)] int DelayMs = 50);

// State is NotStarted, Running, Completed or Failed.
// LastOrderId is the checkpoint: every old order up to this id has been migrated.
public record BackfillStatusResponse(
    string State,
    int LastOrderId,
    long RowsMigrated,
    int Batches,
    long? NotMigrated,
    string? Error);
