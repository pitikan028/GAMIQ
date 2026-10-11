using GAMIQ.Api.Dtos;
using GAMIQ.Api.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace GAMIQ.Api.Controllers;

// CP2 Phase 3: starts the backfill job and shows how far it is.
[ApiController]
[Route("api/v1/migration/backfill")]
public class BackfillController : ControllerBase
{
    private readonly BackfillJob _backfill;

    public BackfillController(BackfillJob backfill)
    {
        _backfill = backfill;
    }

    // GET /api/v1/migration/backfill
    [HttpGet]
    public ActionResult<BackfillStatusResponse> GetStatus() => Ok(_backfill.Status);

    // POST /api/v1/migration/backfill
    // Returns right away with 202, the job keeps running in the background. Body is optional.
    [HttpPost]
    public ActionResult<BackfillStatusResponse> Start([FromBody] StartBackfillRequest? request)
    {
        request ??= new StartBackfillRequest();

        var problem = _backfill.TryStart(request.BatchSize, request.DelayMs);
        if (problem is not null)
        {
            return Conflict(new { message = problem });
        }

        return Accepted(_backfill.Status);
    }
}
