using GAMIQ.Api.Config;
using GAMIQ.Api.Data;
using GAMIQ.Api.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace GAMIQ.Api.Controllers;

// CP2: lets us move the API to the next expand-contract phase while it keeps serving traffic.
[ApiController]
[Route("api/v1/migration/phase")]
public class MigrationController : ControllerBase
{
    private readonly MigrationState _migration;
    private readonly MigrationGuard _guard;

    public MigrationController(MigrationState migration, MigrationGuard guard)
    {
        _migration = migration;
        _guard = guard;
    }

    // GET /api/v1/migration/phase
    [HttpGet]
    public ActionResult<MigrationPhaseResponse> GetPhase() => Ok(new MigrationPhaseResponse(_migration.Phase.ToString()));

    // PUT /api/v1/migration/phase
    // 409 when the database is not ready for that phase yet (see MigrationGuard).
    [HttpPut]
    public async Task<ActionResult<MigrationPhaseResponse>> SetPhase(SetMigrationPhaseRequest request, CancellationToken stop)
    {
        if (!MigrationState.TryParse(request.Phase, out var phase))
        {
            return BadRequest(new { message = $"'{request.Phase}' is not a valid phase. Use one of: {string.Join(", ", Enum.GetNames<MigrationPhase>())}." });
        }

        var problem = await _guard.CheckAsync(phase, stop);
        if (problem is not null)
        {
            return Conflict(new { message = problem });
        }

        _migration.Set(phase);
        return Ok(new MigrationPhaseResponse(phase.ToString()));
    }
}
