using System.ComponentModel.DataAnnotations;

namespace GAMIQ.Api.Dtos;

public record SetMigrationPhaseRequest([Required] string Phase);

public record MigrationPhaseResponse(string Phase);
