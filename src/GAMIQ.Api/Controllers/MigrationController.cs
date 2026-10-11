using GAMIQ.Api.Config;
using GAMIQ.Api.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace GAMIQ.Api.Controllers;

// CP2: lets us move the API to the next expand-contract phase while it keeps serving traffic.
[ApiController]
[Route("api/v1/migration/phase")]
public class MigrationController : ControllerBase
{
    private readonly MigrationState _migration;

    public MigrationController(MigrationState migration)
    {
        _migration = migration;
    }

    // GET /api/v1/migration/phase
    [HttpGet]
    public ActionResult<MigrationPhaseResponse> GetPhase() => Ok(new MigrationPhaseResponse(_migration.Phase.ToString()));

    // PUT /api/v1/migration/phase
    [HttpPut]
    public ActionResult<MigrationPhaseResponse> SetPhase(SetMigrationPhaseRequest request)
    {
        if (!MigrationState.TryParse(request.Phase, out var phase))
        {
            return BadRequest(new { message = $"'{request.Phase}' is not a valid phase. Use one of: {string.Join(", ", Enum.GetNames<MigrationPhase>())}." });
        }

        _migration.Set(phase);
        return Ok(new MigrationPhaseResponse(phase.ToString()));
    }
}
